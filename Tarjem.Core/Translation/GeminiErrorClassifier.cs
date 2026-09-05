using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tarjem.Core.Translation;

public enum GeminiErrorKind
{
    /// <summary>The request succeeded.</summary>
    None,
    /// <summary>The API key itself is rejected. Stop the chain entirely; no other model will work either.</summary>
    InvalidKey,
    /// <summary>Caller has no access to this model/key combination. Stop the chain.</summary>
    Unauthorized,
    /// <summary>The request body used a field this model rejects (e.g. thinkingConfig). Retry the same model once without it.</summary>
    UnsupportedParameter,
    /// <summary>The model id doesn't exist (renamed/removed) or its quota is permanently 0 for this tier. Mark dead, move to the next model.</summary>
    Retired,
    /// <summary>Temporary quota exhaustion. Cool down, move to the next model.</summary>
    RateLimited,
    /// <summary>Transient server/network failure. Worth a quick retry or moving on.</summary>
    Transient,
    /// <summary>Anything else. Treated as Transient by callers.</summary>
    Unknown
}

public sealed record GeminiClassification(GeminiErrorKind Kind, TimeSpan? Cooldown = null, string? Reason = null)
{
    public static readonly GeminiClassification Success = new(GeminiErrorKind.None);
}

/// <summary>
/// Turns a Gemini API HTTP response into an actionable classification. This exists because
/// treating every non-2xx response as "the model is broken" is what silently disabled AI
/// translation in the shipped app: gemini-2.0-flash's 429 "limit: 0" (a permanently retired
/// free-tier quota) looks identical, at the status-code level, to an ordinary transient
/// rate limit - but the correct response is completely different (skip forever vs. cool down
/// and retry).
/// </summary>
public static class GeminiErrorClassifier
{
    private static readonly Regex ZeroLimitPattern = new(@"limit[""']?\s*[:=]\s*0\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetryDelayPattern = new(@"""retryDelay""\s*:\s*""(\d+)s""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GeminiClassification Classify(HttpStatusCode status, string body)
    {
        var (apiStatus, message) = ExtractError(body);

        // Any 2xx is a success. Only 200 was matched before, so a 204/206 - which this API
        // doesn't currently return, but nothing guarantees it never will - was routed into
        // Unknown and treated as a failure despite the request having worked.
        if ((int)status is >= 200 and < 300)
            return GeminiClassification.Success;

        return status switch
        {
            HttpStatusCode.BadRequest when MentionsThinkingConfig(message) =>
                new GeminiClassification(GeminiErrorKind.UnsupportedParameter, Reason: message),

            HttpStatusCode.BadRequest =>
                new GeminiClassification(GeminiErrorKind.InvalidKey, Reason: message ?? "Invalid request (likely a bad API key)"),

            // 401 is the textbook "this key isn't accepted" response. It used to fall through
            // to Unknown, which leaves the model's persisted state untouched and no cooldown
            // set - so a rejected key was re-sent on every single lookup, forever, instead of
            // stopping the chain the way the 400/403 paths already do.
            HttpStatusCode.Unauthorized =>
                new GeminiClassification(GeminiErrorKind.InvalidKey, Reason: message ?? "API key rejected"),

            HttpStatusCode.Forbidden =>
                new GeminiClassification(GeminiErrorKind.Unauthorized, Reason: message ?? "Access denied for this key/model"),

            HttpStatusCode.NotFound =>
                new GeminiClassification(GeminiErrorKind.Retired, Reason: message ?? "Model not found"),

            HttpStatusCode.RequestTimeout =>
                new GeminiClassification(GeminiErrorKind.Transient, Reason: message ?? "Request timed out"),

            HttpStatusCode.TooManyRequests => ClassifyRateLimit(body, message),

            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout =>
                new GeminiClassification(GeminiErrorKind.Transient, Reason: message ?? apiStatus),

            _ => new GeminiClassification(GeminiErrorKind.Unknown, Reason: message ?? $"HTTP {(int)status}")
        };
    }

    private static GeminiClassification ClassifyRateLimit(string body, string? message)
    {
        // A "limit: 0" quota violation means this model/tier combination has zero
        // allotted requests - it is not going to start working if we wait and retry,
        // unlike an ordinary "you sent too many requests" 429.
        if (ZeroLimitPattern.IsMatch(body))
            return new GeminiClassification(GeminiErrorKind.Retired, Reason: message ?? "Quota permanently zero for this model");

        var retryMatch = RetryDelayPattern.Match(body);
        var cooldown = retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(60);

        return new GeminiClassification(GeminiErrorKind.RateLimited, cooldown, message ?? "Rate limited");
    }

    private static bool MentionsThinkingConfig(string? message) =>
        message != null && message.Contains("thinking", StringComparison.OrdinalIgnoreCase);

    private static (string? Status, string? Message) ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error))
                return (null, null);

            var status = error.TryGetProperty("status", out var s) ? s.GetString() : null;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            return (status, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
