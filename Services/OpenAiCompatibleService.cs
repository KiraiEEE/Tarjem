using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
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
    private readonly string _model;

    public string Id { get; }

    private OpenAiCompatibleService(string id, string endpoint, string model, string apiKey, TimeSpan timeout)
    {
        Id = id;
        _endpoint = endpoint;
        _model = model;
        _http = new HttpClient { Timeout = timeout };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>Groq's Llama 3.3 70B: the best free-tier quality-per-millisecond available, and
    /// fast enough to sit on the automatic path rather than only behind an explicit choice.</summary>
    public static OpenAiCompatibleService ForGroq(string apiKey) => new(
        "groq", "https://api.groq.com/openai/v1/chat/completions", "llama-3.3-70b-versatile",
        apiKey, TimeSpan.FromSeconds(8));

    public static OpenAiCompatibleService ForCerebras(string apiKey) => new(
        "cerebras", "https://api.cerebras.ai/v1/chat/completions", "llama-3.3-70b",
        apiKey, TimeSpan.FromSeconds(8));

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
        catch (Exception ex)
        {
            Log.Debug(ex, "{Provider} connection test failed", Id);
            return false;
        }
    }

    private async Task<string?> CompleteAsync(string system, string user, int maxTokens, double temperature, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Log.Debug("{Provider} returned {Status}: {Body}", Id, (int)response.StatusCode, Truncate(json));
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return null;

        return choices[0].TryGetProperty("message", out var message) &&
               message.TryGetProperty("content", out var text)
            ? text.GetString()
            : null;
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
