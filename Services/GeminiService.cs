using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Serilog;
using Tarjem.Core.Translation;
using Tarjem.Models;

namespace Tarjem.Services;

/// <summary>
/// Calls the Gemini API through an ordered model fallback chain (see <see cref="ModelChain"/>),
/// classifying every failure with <see cref="GeminiErrorClassifier"/> so a permanently retired
/// model (e.g. gemini-2.0-flash's free-tier quota, which returns 429 "limit: 0" forever) is
/// skipped from then on instead of being retried - and paying its timeout - on every lookup.
/// </summary>
public class GeminiService : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;
    private readonly Action _persistHealth;
    private string _apiKey;

    private Dictionary<string, ModelHealth> _health => _settings.ModelHealth;

    private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    public string Id => "gemini";

    /// <param name="apiKey">The key already resolved by <see cref="TranslationService.ResolveKey"/>
    /// (the user's own, else the shipped one). Passed in rather than looked up here so there is a
    /// single place that decides which key wins.</param>
    public GeminiService(AppSettings settings, Action persistHealth, string? apiKey)
    {
        _httpClient = new HttpClient();
        _settings = settings;
        _persistHealth = persistHealth;
        _apiKey = apiKey ?? "";
        DiscardHealthIfKeyChanged();
    }

    public void SetApiKey(string? apiKey)
    {
        _apiKey = apiKey ?? "";
        DiscardHealthIfKeyChanged();
    }

    /// <summary>
    /// Throws away every cached model verdict when the key in use isn't the one those verdicts
    /// were recorded against. Health is a judgement about a (model, key) pair, not about a model
    /// alone: "this key is unauthorized" says nothing about the replacement key the user just
    /// pasted in, and keeping the old verdict means the fix they just applied appears not to
    /// work. The fingerprint is a salted hash - the key itself is never written to settings.json,
    /// which is plaintext.
    /// </summary>
    private void DiscardHealthIfKeyChanged()
    {
        var fingerprint = Fingerprint(_apiKey);
        if (_settings.ModelHealthKeyFingerprint == fingerprint)
            return;

        _settings.ModelHealthKeyFingerprint = fingerprint;
        if (_health.Count > 0)
        {
            _health.Clear();
            Log.Information("Gemini API key changed - cleared cached model health so every model is retried with the new key");
        }
        _persistHealth();
    }

    private static string Fingerprint(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? ""
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes("tarjem-model-health\0" + key)))[..16];

    /// <summary>Wipes every cached verdict so the next call re-probes the whole chain from
    /// scratch. Backs the Settings "Test AI connection" button: a user pressing Test is asking
    /// what the API does *right now*, so answering from a cooldown recorded an hour ago - and
    /// reporting a failure without having sent a single request - would be worse than useless.</summary>
    public void ResetHealth()
    {
        if (_health.Count == 0) return;
        _health.Clear();
        _persistHealth();
    }

    public async Task<TranslationResult?> TranslateAsync(
        string word, string fullSentence, string sourceLanguageCode, string sourceLanguageName,
        string targetLanguageCode, string targetLanguageName, CancellationToken ct = default)
    {
        var (result, _) = await RunChainAsync(word, fullSentence, sourceLanguageCode, sourceLanguageName, targetLanguageCode, targetLanguageName, ct);
        return result;
    }

    /// <summary>
    /// Plain "translate this block of text" call, used by the region (Alt+W) overlay. The
    /// word-oriented <see cref="TranslateAsync"/> above returns a dictionary entry - phonetics,
    /// CEFR level, a highlighted word inside a sentence - none of which applies to an arbitrary
    /// chunk of text lifted off the screen. Runs the same model fallback chain and feeds the
    /// same health bookkeeping; only the prompt and the response shape differ.
    /// Returns null if every model in the chain failed.
    /// </summary>
    public Task<string?> TranslateTextAsync(
        string text, string sourceLanguageName, string targetLanguageName, CancellationToken ct = default,
        IReadOnlyList<(string Source, string Translation)>? context = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult<string?>(null);

        return RunTextChainAsync(
            TranslationPrompts.SystemInstruction(targetLanguageName),
            TranslationPrompts.TextTranslationRequest(text, sourceLanguageName, targetLanguageName, context),
            maxOutputTokens: 2048, TranslationPrompts.RewriteTemperature, ct);
    }

    /// <summary>
    /// "What is this?" (Alt+E), for names Wikipedia and Wikidata both came back empty on. Returns
    /// null both when every model failed and - just as importantly - when the model that answered
    /// said it did not recognize the name; see <see cref="EncyclopediaAnswerParser"/> for why that
    /// second case is the one this feature lives or dies on.
    /// </summary>
    public async Task<EncyclopediaAnswer?> ExplainAsync(
        string term, string sourceLanguageName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;

        var content = await RunTextChainAsync(
            TranslationPrompts.EncyclopediaSystemInstruction(sourceLanguageName),
            TranslationPrompts.EncyclopediaRequest(term, sourceLanguageName),
            maxOutputTokens: 512, TranslationPrompts.FactualTemperature, ct);

        return EncyclopediaAnswerParser.Parse(content);
    }

    /// <summary>
    /// Walks the model chain for any prompt that wants plain text back, recording health as it
    /// goes. Both the region translation and the encyclopedia lookup are this shape - only the
    /// prompt differs - and the chain bookkeeping is the part worth having exactly once.
    /// </summary>
    private async Task<string?> RunTextChainAsync(
        string systemInstruction, string userPrompt, int maxOutputTokens, double temperature, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return null;

        var chainDeadline = DateTime.UtcNow.AddMilliseconds(9500);

        foreach (var model in ModelChain.UsableInOrder(ModelChain.Default, _health, DateTimeOffset.UtcNow))
        {
            if (DateTime.UtcNow >= chainDeadline || ct.IsCancellationRequested)
                break;

            var (text, classification, latencyMs) =
                await TryModelPlainTextAsync(model, systemInstruction, userPrompt, maxOutputTokens, temperature, ct);

            RecordHealth(model.Id, classification, latencyMs);

            if (classification.Kind == GeminiErrorKind.None && !string.IsNullOrWhiteSpace(text))
            {
                _persistHealth();
                return text;
            }

            if (classification.Kind is GeminiErrorKind.InvalidKey or GeminiErrorKind.Unauthorized)
                break;
        }

        _persistHealth();
        return null;
    }

    private async Task<(string? Text, GeminiClassification Classification, int LatencyMs)> TryModelPlainTextAsync(
        GeminiModel model, string systemInstruction, string userPrompt, int maxOutputTokens, double temperature, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(model.TimeoutMs);
        var sw = Stopwatch.StartNew();

        try
        {
            var requestBody = new JsonObject
            {
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = systemInstruction })
                },
                ["contents"] = new JsonArray(new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = userPrompt })
                }),
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = temperature,
                    ["maxOutputTokens"] = maxOutputTokens
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/{model.Id}:generateContent")
            {
                Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", _apiKey);

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            sw.Stop();

            var classification = GeminiErrorClassifier.Classify(response.StatusCode, responseJson);
            if (classification.Kind != GeminiErrorKind.None)
                return (null, classification, (int)sw.ElapsedMilliseconds);

            var translated = ExtractPlainText(responseJson);
            if (string.IsNullOrWhiteSpace(translated))
                return (null, new GeminiClassification(GeminiErrorKind.Unknown, Reason: "Malformed response body"), (int)sw.ElapsedMilliseconds);

            return (translated.Trim(), GeminiClassification.Success, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Debug("Gemini model {ModelId} timed out after {TimeoutMs}ms (plain text)", model.Id, model.TimeoutMs);
            return (null, new GeminiClassification(GeminiErrorKind.Transient, Reason: $"Timed out after {model.TimeoutMs}ms"), (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            Log.Debug(ex, "Gemini model {ModelId} request failed (plain text)", model.Id);
            return (null, new GeminiClassification(GeminiErrorKind.Transient, Reason: ex.Message), (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Pulls the single text part out of a generateContent response. Returns null for
    /// anything unexpected - including a body that isn't JSON at all, which is what a captive
    /// portal or a corporate proxy returns and which used to throw straight out of the model
    /// chain and abandon every remaining model.</summary>
    private static string? ExtractPlainText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return FirstPartText(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Walks candidates[0].content.parts[0].text, checking each hop's kind - a bare
    /// GetArrayLength() on a property that came back as an object throws rather than returning 0.</summary>
    private static string? FirstPartText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return null;

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var contentEl) ||
            !contentEl.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0 ||
            !parts[0].TryGetProperty("text", out var textEl) ||
            textEl.ValueKind != JsonValueKind.String)
            return null;

        return textEl.GetString();
    }

    /// <summary>Runs the chain once against a fixed probe sentence and reports which model
    /// answered and how fast, or the classified reason every model failed. Powers the
    /// Settings "Test AI connection" button. Source/target language don't matter for a pure
    /// connectivity probe, so they're fixed rather than threaded in from the caller.</summary>
    public async Task<AiStatus> TestConnectionAsync(CancellationToken ct = default)
    {
        ResetHealth();
        var (_, status) = await RunChainAsync("resilient", "The team proved remarkably resilient under pressure.", "en", "English", "ar", "Arabic", ct);
        return status;
    }

    private async Task<(TranslationResult? Result, AiStatus Status)> RunChainAsync(
        string word, string fullSentence, string sourceLanguageCode, string sourceLanguageName,
        string targetLanguageCode, string targetLanguageName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return (null, new AiStatus.Off("No API key configured"));

        // Must exceed the combined timeout of every model that can precede the last-resort
        // "thinking" model (4000ms + 4000ms, see ModelChain.Default) - otherwise that model,
        // the most reliable one in the chain, can never even be started once the faster ones
        // have failed, and every lookup silently bottoms out at the weak dictionary fallback.
        var chainDeadline = DateTime.UtcNow.AddMilliseconds(9500);
        GeminiClassification? lastFailure = null;

        foreach (var model in ModelChain.UsableInOrder(ModelChain.Default, _health, DateTimeOffset.UtcNow))
        {
            if (DateTime.UtcNow >= chainDeadline || ct.IsCancellationRequested)
                break;

            var attempt = await TryModelAsync(model, word, fullSentence, sourceLanguageCode, sourceLanguageName, targetLanguageCode, targetLanguageName, ct);
            RecordHealth(model.Id, attempt.Classification, attempt.LatencyMs);

            if (attempt.Classification.Kind == GeminiErrorKind.None)
            {
                _persistHealth();
                return (attempt.Result, new AiStatus.Ok(model.Id, attempt.LatencyMs));
            }

            lastFailure = attempt.Classification;

            // No other model will authenticate any better with the same key - stop now
            // instead of burning the rest of the chain budget on guaranteed failures.
            if (attempt.Classification.Kind is GeminiErrorKind.InvalidKey or GeminiErrorKind.Unauthorized)
                break;
        }

        _persistHealth();
        var reason = lastFailure?.Reason ?? "No usable Gemini model right now (all retired or cooling down)";
        var kind = lastFailure?.Kind ?? GeminiErrorKind.Unknown;
        return (null, new AiStatus.Degraded(kind, reason));
    }

    private readonly record struct AttemptResult(TranslationResult? Result, GeminiClassification Classification, int LatencyMs);

    private async Task<AttemptResult> TryModelAsync(
        GeminiModel model, string word, string fullSentence, string sourceLanguageCode, string sourceLanguageName,
        string targetLanguageCode, string targetLanguageName, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(model.TimeoutMs);
        var sw = Stopwatch.StartNew();

        try
        {
            var requestBody = new JsonObject
            {
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = TranslationPrompts.SystemInstruction(targetLanguageName) })
                },
                ["contents"] = new JsonArray(new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject
                    {
                        ["text"] = TranslationPrompts.DictionaryEntryRequest(word, fullSentence, sourceLanguageName, targetLanguageName)
                    })
                }),
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = 0.2,
                    ["maxOutputTokens"] = 2048,
                    ["responseMimeType"] = "application/json",
                    ["responseSchema"] = BuildResponseSchema(!string.IsNullOrEmpty(fullSentence))
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/{model.Id}:generateContent")
            {
                Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", _apiKey);

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            sw.Stop();

            var classification = GeminiErrorClassifier.Classify(response.StatusCode, responseJson);
            if (classification.Kind != GeminiErrorKind.None)
                return new AttemptResult(null, classification, (int)sw.ElapsedMilliseconds);

            var result = ParseResult(responseJson, word, fullSentence, sourceLanguageCode, targetLanguageCode);
            if (result == null)
                return new AttemptResult(null, new GeminiClassification(GeminiErrorKind.Unknown, Reason: "Malformed response body"), (int)sw.ElapsedMilliseconds);

            return new AttemptResult(result, GeminiClassification.Success, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own per-model timeout fired, not the caller's cancellation - treat as transient and move on.
            Log.Debug("Gemini model {ModelId} timed out after {TimeoutMs}ms", model.Id, model.TimeoutMs);
            return new AttemptResult(null, new GeminiClassification(GeminiErrorKind.Transient, Reason: $"Timed out after {model.TimeoutMs}ms"), (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            Log.Debug(ex, "Gemini model {ModelId} request failed", model.Id);
            return new AttemptResult(null, new GeminiClassification(GeminiErrorKind.Transient, Reason: ex.Message), (int)sw.ElapsedMilliseconds);
        }
    }

    private void RecordHealth(string modelId, GeminiClassification classification, int latencyMs)
    {
        if (!_health.TryGetValue(modelId, out var health))
        {
            health = new ModelHealth { Id = modelId };
            _health[modelId] = health;
        }

        health.LastLatencyMs = latencyMs;
        health.LastError = classification.Reason;

        switch (classification.Kind)
        {
            case GeminiErrorKind.None:
                health.State = ModelState.Healthy;
                health.CooldownUntil = null;
                break;
            case GeminiErrorKind.Retired:
                MarkUnhealthy(health, ModelState.Retired, classification.Cooldown);
                break;
            case GeminiErrorKind.InvalidKey:
            case GeminiErrorKind.Unauthorized:
                MarkUnhealthy(health, ModelState.Unauthorized, classification.Cooldown);
                break;
            case GeminiErrorKind.RateLimited:
                MarkUnhealthy(health, ModelState.RateLimited, classification.Cooldown);
                break;
            // Transient/UnsupportedParameter/Unknown: leave the persisted state as-is,
            // a single blip shouldn't demote a model that was healthy a moment ago.
        }
    }

    /// <summary>Parks a model for the cooldown its failure earned. Always sets an expiry - a
    /// state with no <see cref="ModelHealth.CooldownUntil"/> would be skipped forever by
    /// <see cref="ModelChain.UsableInOrder"/>, which is how a transient key flag used to
    /// disable AI translation permanently.</summary>
    private static void MarkUnhealthy(ModelHealth health, ModelState state, TimeSpan? explicitCooldown)
    {
        health.State = state;
        health.CooldownUntil = DateTimeOffset.UtcNow + (explicitCooldown ?? ModelChain.CooldownFor(state));
    }

    private static JsonObject BuildResponseSchema(bool hasSentence)
    {
        var properties = new JsonObject
        {
            ["word"] = new JsonObject { ["type"] = "STRING" },
            ["phonetic"] = new JsonObject { ["type"] = "STRING" },
            ["partOfSpeech"] = new JsonObject { ["type"] = "STRING" },
            ["cefrLevel"] = new JsonObject
            {
                ["type"] = "STRING",
                ["enum"] = new JsonArray("A1", "A2", "B1", "B2", "C1", "C2")
            },
            ["definition"] = new JsonObject { ["type"] = "STRING" },
            ["synonyms"] = new JsonObject
            {
                ["type"] = "ARRAY",
                ["items"] = new JsonObject { ["type"] = "STRING" }
            },
            ["arabicWord"] = new JsonObject { ["type"] = "STRING" },
            ["arabicTranslation"] = new JsonObject { ["type"] = "STRING" }
        };

        var required = new JsonArray("word", "phonetic", "partOfSpeech", "cefrLevel", "definition", "synonyms", "arabicWord", "arabicTranslation");

        if (hasSentence)
        {
            properties["fullSentenceTranslation"] = new JsonObject { ["type"] = "STRING" };
            required.Add("fullSentenceTranslation");
        }

        return new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = properties,
            ["required"] = required
        };
    }

    private static TranslationResult? ParseResult(string responseJson, string word, string fullSentence, string sourceLanguageCode, string targetLanguageCode)
    {
        // Both documents are disposed here rather than leaked: JsonDocument rents pooled buffers,
        // and this is the hottest path in the app. Everything below is copied into the returned
        // TranslationResult before the documents go out of scope, so nothing dangles.
        string text;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            text = FirstPartText(doc.RootElement) ?? "";
        }
        catch (JsonException)
        {
            return null;
        }

        if (text.Length == 0)
            return null;

        using var resultDoc = ParseJsonLeniently(text);
        if (resultDoc == null)
            return null;

        var root = resultDoc.RootElement;

        var result = new TranslationResult
        {
            OriginalWord = root.TryGetProperty("word", out var w) ? w.GetString() ?? word : word,
            PartOfSpeech = root.TryGetProperty("partOfSpeech", out var pos) ? pos.GetString() ?? "" : "",
            Phonetic = root.TryGetProperty("phonetic", out var ph) ? ph.GetString() ?? "" : "",
            CefrLevel = root.TryGetProperty("cefrLevel", out var cefr) ? cefr.GetString() ?? "" : "",
            ArabicWord = root.TryGetProperty("arabicWord", out var aw) ? aw.GetString() ?? "" : "",
            ArabicTranslation = root.TryGetProperty("arabicTranslation", out var ar) ? ar.GetString() ?? "" : "",
            SourceLanguage = sourceLanguageCode,
            TargetLanguage = targetLanguageCode,
            IsFromGemini = true,
        };

        if (string.IsNullOrEmpty(result.ArabicTranslation))
            result.ArabicTranslation = result.ArabicWord;
        if (string.IsNullOrEmpty(result.ArabicWord))
            result.ArabicWord = result.ArabicTranslation;

        if (root.TryGetProperty("definition", out var def) && def.ValueKind == JsonValueKind.String)
            result.Meanings = new[] { def.GetString() ?? "" };
        else
            result.Meanings = new[] { "Definition not found" };

        if (root.TryGetProperty("synonyms", out var syns) && syns.ValueKind == JsonValueKind.Array)
        {
            var synList = new List<string>();
            for (int i = 0; i < syns.GetArrayLength(); i++)
            {
                var s = syns[i].GetString();
                if (!string.IsNullOrEmpty(s)) synList.Add(s);
            }
            result.Synonyms = synList.ToArray();
        }

        if (root.TryGetProperty("fullSentenceTranslation", out var sentTrans) && sentTrans.ValueKind == JsonValueKind.String)
        {
            result.FullSentence = fullSentence;
            var (clean, start, length) = ExtractHighlightMarker(sentTrans.GetString() ?? "");
            result.TranslatedSentence = clean;
            result.TranslatedSentenceHighlightStart = start;
            result.TranslatedSentenceHighlightLength = length;
        }
        else if (!string.IsNullOrEmpty(fullSentence))
        {
            result.FullSentence = fullSentence;
            result.TranslatedSentence = "";
        }

        return result;
    }

    private static readonly Regex HighlightMarkerRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

    /// <summary>Strips the "**...**" marker(s) Gemini wraps around the translated word inside
    /// <c>fullSentenceTranslation</c> and returns the clean text plus the position/length of the
    /// first marked span within it, so the UI can highlight exactly what Gemini actually used -
    /// instead of re-searching for a separately generated "arabicWord" that may not match due to
    /// Arabic inflection (attached articles, case endings, etc.).</summary>
    private static (string CleanText, int Start, int Length) ExtractHighlightMarker(string text)
    {
        var matches = HighlightMarkerRegex.Matches(text);
        if (matches.Count == 0)
            return (text, -1, 0);

        var sb = new StringBuilder(text.Length);
        int cursor = 0;
        int highlightStart = -1, highlightLength = 0;

        foreach (Match m in matches)
        {
            sb.Append(text, cursor, m.Index - cursor);
            if (highlightStart < 0)
            {
                highlightStart = sb.Length;
                highlightLength = m.Groups[1].Length;
            }
            sb.Append(m.Groups[1].Value);
            cursor = m.Index + m.Length;
        }
        sb.Append(text, cursor, text.Length - cursor);

        return (sb.ToString(), highlightStart, highlightLength);
    }

    /// <summary>Moved to <see cref="LenientJson"/> once Groq and Cerebras needed the same
    /// tolerance for fenced and prose-wrapped JSON.</summary>
    private static JsonDocument? ParseJsonLeniently(string text) => LenientJson.Parse(text);
}
