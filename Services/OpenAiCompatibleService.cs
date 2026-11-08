using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Tarjem.Core.Providers;
using Tarjem.Core.Translation;

namespace Tarjem.Services;

/// <summary>
/// Translation through any provider that speaks the OpenAI chat-completions shape - which Groq
/// and Cerebras both do, so one implementation covers both. Both hand out free keys without a
/// card, and both exist here for the same reason: when Gemini's shared key gets throttled or
/// flagged, a lookup should quietly come back from somewhere else rather than fail.
///
/// Constructed lazily by <see cref="TranslationService"/> - a user who never selects one of
/// these never pays for its HttpClient.
/// </summary>
public sealed class OpenAiCompatibleService
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string[] _preferredModels;

    public string Id { get; }

    /// <summary>The model each provider resolved to this session, shared across the clients
    /// <see cref="TranslationService"/> rebuilds whenever a key changes.</summary>
    private static readonly Dictionary<string, string> ResolvedModels = new();

    private OpenAiCompatibleService(string id, string endpoint, string[] preferredModels, string apiKey, TimeSpan timeout)
    {
        Id = id;
        _endpoint = endpoint;
        _preferredModels = preferredModels;
        _http = new HttpClient { Timeout = timeout };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>
    /// Groq, best first. These providers retire models on short notice: llama-3.3-70b-versatile
    /// was this app's only model until it disappeared, after which every Groq request failed with
    /// model_not_found and nothing said so. The client now asks the provider which of these it
    /// still serves and uses the best one, and asks again if that one goes away.
    /// </summary>
    public static OpenAiCompatibleService ForGroq(string apiKey) => new(
        "groq", "https://api.groq.com/openai/v1/chat/completions",
        ["openai/gpt-oss-120b", "llama-3.3-70b-versatile", "qwen/qwen3.8-27b", "openai/gpt-oss-20b"],
        apiKey, TimeSpan.FromSeconds(10));

    public static OpenAiCompatibleService ForCerebras(string apiKey) => new(
        "cerebras", "https://api.cerebras.ai/v1/chat/completions",
        ["gpt-oss-120b", "llama-3.3-70b", "qwen-3-32b", "llama3.1-8b"],
        apiKey, TimeSpan.FromSeconds(10));

    private string ModelsEndpoint => _endpoint.Replace("/chat/completions", "/models");

    /// <summary>The model to use: the best preferred one the provider lists, found once per session.</summary>
    private async Task<string> ModelAsync(CancellationToken ct)
    {
        lock (ResolvedModels)
        {
            if (ResolvedModels.TryGetValue(Id, out var known)) return known;
        }

        var chosen = _preferredModels[0];
        try
        {
            using var response = await _http.GetAsync(ModelsEndpoint, ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var offered = doc.RootElement.TryGetProperty("data", out var data)
                    ? data.EnumerateArray()
                        .Select(m => m.TryGetProperty("id", out var mid) ? mid.GetString() : null)
                        .OfType<string>()
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>();

                chosen = _preferredModels.FirstOrDefault(offered.Contains) ?? chosen;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProviderUnavailableException) { throw; }
        catch (Exception ex)
        {
            Log.Information("{Provider}: could not list models ({Error}); trying {Model}", Id, ex.Message, chosen);
        }

        lock (ResolvedModels) ResolvedModels[Id] = chosen;
        Log.Information("{Provider} using model {Model}", Id, chosen);
        return chosen;
    }

    private static bool IsReasoningModel(string model) =>
        model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("qwen3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Translates a block of text. Returns null on any failure so the caller's failover
    /// chain can move on - these providers are alternatives to each other, never the last word.</summary>
    public async Task<string?> TranslateTextAsync(
        string text, string sourceLanguageName, string targetLanguageName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var content = await CompleteAsync(
                TranslationPrompts.SystemInstruction(targetLanguageName),
                TranslationPrompts.TextTranslationRequest(text, sourceLanguageName, targetLanguageName),
                maxTokens: 2048, TranslationPrompts.RewriteTemperature, ct);

            return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProviderUnavailableException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Provider} text translation failed", Id);
            return null;
        }
    }

    /// <summary>Word + sentence translation, matching what the popup needs. The word is returned
    /// on the first line and the sentence on the second, which these models follow far more
    /// reliably than they follow a JSON schema.</summary>
    public async Task<(string Word, string Sentence)?> TranslateWordAsync(
        string word, string sentence, string sourceLanguageName, string targetLanguageName, CancellationToken ct = default)
    {
        try
        {
            var content = await CompleteAsync(
                TranslationPrompts.SystemInstruction(targetLanguageName),
                TranslationPrompts.WordTranslationRequest(word, sentence, sourceLanguageName, targetLanguageName),
                maxTokens: 512, TranslationPrompts.RewriteTemperature, ct);

            if (string.IsNullOrWhiteSpace(content)) return null;

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) return null;

            var translatedWord = StripLabel(lines[0]);
            var translatedSentence = lines.Length > 1 ? StripLabel(lines[1]) : "";

            return string.IsNullOrWhiteSpace(translatedWord) ? null : (translatedWord, translatedSentence);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProviderUnavailableException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Provider} word translation failed", Id);
            return null;
        }
    }

    /// <summary>
    /// "What is this?" (Alt+E), for names Wikipedia and Wikidata both came back empty on. Returns
    /// null when the request failed <em>and</em> when the model said it didn't recognize the name -
    /// which for a 70B model asked about an obscure proper noun is the common case, and the one
    /// <see cref="EncyclopediaAnswerParser"/> exists to enforce.
    /// </summary>
    public async Task<EncyclopediaAnswer?> ExplainAsync(
        string term, string sourceLanguageName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;

        try
        {
            var content = await CompleteAsync(
                TranslationPrompts.EncyclopediaSystemInstruction(sourceLanguageName),
                TranslationPrompts.EncyclopediaRequest(term, sourceLanguageName),
                maxTokens: 512, TranslationPrompts.FactualTemperature, ct);

            return EncyclopediaAnswerParser.Parse(content);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProviderUnavailableException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Provider} encyclopedia lookup failed", Id);
            return null;
        }
    }

    /// <summary>One round trip against the shared endpoint. Public so the Settings "check this
    /// source" indicator can probe it without a translation to show for it.</summary>
    public async Task<bool> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var reply = await CompleteAsync("You are a test endpoint.", "Reply with the single word: ok", maxTokens: 8, temperature: 0, ct);
            return !string.IsNullOrWhiteSpace(reply);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProviderUnavailableException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Provider} connection test failed", Id);
            return false;
        }
    }

    private async Task<string?> CompleteAsync(string system, string user, int maxTokens, double temperature, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var model = await ModelAsync(ct);
            var reasoning = IsReasoningModel(model);

            var body = new JsonObject
            {
                ["model"] = model,
                ["temperature"] = temperature,
                // Reasoning models spend part of the budget thinking before they answer; without
                // the headroom a translation was cut off, or came back empty.
                ["max_tokens"] = reasoning ? maxTokens + 1024 : maxTokens,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = user }),
            };

            // Translation needs no deliberation, and every token of it is latency.
            if (reasoning)
                body["reasoning_effort"] = "low";

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException($"{Id} unreachable: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderUnavailableException($"{Id} timed out");
            }

            using var _ = response;
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;

                // The model was retired: find out what is served now and try once more.
                if (attempt == 0 && (status == 404 || json.Contains("model_not_found") || json.Contains("model_decommissioned")))
                {
                    Log.Warning("{Provider} no longer serves {Model}; looking for another", Id, model);
                    lock (ResolvedModels) ResolvedModels.Remove(Id);
                    continue;
                }

                Log.Warning("{Provider} returned {Status}: {Body}", Id, status, Truncate(json));
                if (status == 429 || status >= 500)
                    throw new ProviderUnavailableException($"{Id} returned {status}", rateLimited: status == 429);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;

            var text = choices[0].TryGetProperty("message", out var message) &&
                       message.TryGetProperty("content", out var content)
                ? content.GetString()
                : null;

            return StripThinking(text);
        }

        return null;
    }

    /// <summary>Some reasoning models put their deliberation inline in &lt;think&gt; tags rather
    /// than in a separate field; none of it belongs in a translation.</summary>
    private static string? StripThinking(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var end = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? text[(end + "</think>".Length)..].Trim() : text;
    }

    /// <summary>Removes a leading "Word:" / "Translation:" style label. The prompt asks for bare
    /// lines, but smaller models like to caption their output, and a caption left in place ends
    /// up rendered as though it were part of the translation.</summary>
    private static string StripLabel(string line)
    {
        var colon = line.IndexOf(':');
        if (colon > 0 && colon < 20 && line[..colon].All(c => char.IsLetter(c) || char.IsWhiteSpace(c)))
            return line[(colon + 1)..].Trim();
        return line.Trim();
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
