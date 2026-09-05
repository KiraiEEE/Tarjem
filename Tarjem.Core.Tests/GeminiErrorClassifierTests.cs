using System.Net;
using Tarjem.Core.Translation;

namespace Tarjem.Core.Tests;

/// <summary>
/// Regression suite for the exact failure that silently disabled AI translation in the
/// shipped app: gemini-2.0-flash's free-tier quota returns HTTP 429 RESOURCE_EXHAUSTED
/// with "limit: 0" - a *permanent* condition, not an ordinary rate limit - and the old
/// code treated every non-2xx response identically, so it just fell back forever without
/// ever surfacing why. Bodies below are real responses captured live against the
/// generativelanguage.googleapis.com API during development, except where noted.
/// </summary>
public class GeminiErrorClassifierTests
{
    // Captured verbatim: gemini-2.0-flash, three currently-issued API keys, all identical.
    private const string ZeroLimitQuotaBody = """
        {
          "error": {
            "code": 429,
            "message": "You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://ai.google.dev/gemini-api/docs/rate-limits. \n* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 0, model: gemini-2.0-flash\n* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 0, model: gemini-2.0-flash\n* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_input_token_count, limit: 0, model: gemini-2.0-flash\nPlease retry in 49.761228834s.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.RetryInfo",
                "retryDelay": "49s"
              }
            ]
          }
        }
        """;

    // Captured verbatim: calling generateContent with ?key= (query-param auth) against a
    // current model. This is why auth had to move to the x-goog-api-key header.
    private const string InvalidArgumentQueryParamBody = """
        {
          "error": {
            "code": 400,
            "message": "Request contains an invalid argument.",
            "status": "INVALID_ARGUMENT"
          }
        }
        """;

    // Captured verbatim: an unregistered model id (gemini-9-flash) with x-goog-api-key auth.
    private const string ModelNotFoundBody = """
        {
          "error": {
            "code": 404,
            "message": "models/gemini-9-flash is not found for API version v1beta, or is not supported for GenerateContent. Call ListModels to see the list of available models and their supported methods.",
            "status": "NOT_FOUND"
          }
        }
        """;

    // Constructed from Google's documented error shape (not independently captured): a
    // literally invalid key against the header-auth path.
    private const string InvalidKeyBody = """
        {
          "error": {
            "code": 400,
            "message": "API key not valid. Please pass a valid API key.",
            "status": "INVALID_ARGUMENT"
          }
        }
        """;

    // Constructed: an ordinary transient rate limit, i.e. a nonzero quota that is
    // temporarily exhausted - the case that must NOT be confused with ZeroLimitQuotaBody.
    private const string TransientRateLimitBody = """
        {
          "error": {
            "code": 429,
            "message": "Resource has been exhausted (e.g. check quota).",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.RetryInfo",
                "retryDelay": "12s"
              }
            ]
          }
        }
        """;

    private const string ServerErrorBody = """
        {
          "error": {
            "code": 503,
            "message": "The model is overloaded. Please try again later.",
            "status": "UNAVAILABLE"
          }
        }
        """;

    [Fact]
    public void ZeroLimitQuota_IsClassifiedAsRetired_NotRateLimited()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.TooManyRequests, ZeroLimitQuotaBody);

        Assert.Equal(GeminiErrorKind.Retired, result.Kind);
    }

    [Fact]
    public void OrdinaryRateLimit_IsClassifiedAsRateLimited_WithCooldownFromRetryDelay()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.TooManyRequests, TransientRateLimitBody);

        Assert.Equal(GeminiErrorKind.RateLimited, result.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), result.Cooldown);
    }

    [Fact]
    public void RateLimitWithoutRetryDelay_DefaultsCooldownTo60Seconds()
    {
        const string body = """{"error":{"code":429,"message":"Resource exhausted","status":"RESOURCE_EXHAUSTED"}}""";

        var result = GeminiErrorClassifier.Classify(HttpStatusCode.TooManyRequests, body);

        Assert.Equal(GeminiErrorKind.RateLimited, result.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Cooldown);
    }

    [Fact]
    public void QueryParamAuthOn400_IsClassifiedAsInvalidKey()
    {
        // This is the auth-method bug: ?key= returns 400 INVALID_ARGUMENT on current
        // models even with a perfectly valid key. The classifier can't distinguish "bad
        // key" from "bad auth method" from the body alone - both must route to the same
        // "stop the chain, tell the user" action, which is what matters operationally.
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.BadRequest, InvalidArgumentQueryParamBody);

        Assert.Equal(GeminiErrorKind.InvalidKey, result.Kind);
    }

    [Fact]
    public void GenuinelyInvalidKey_IsClassifiedAsInvalidKey()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.BadRequest, InvalidKeyBody);

        Assert.Equal(GeminiErrorKind.InvalidKey, result.Kind);
    }

    [Fact]
    public void ThinkingConfigRejection_IsClassifiedAsUnsupportedParameter()
    {
        const string body = """
            {"error":{"code":400,"message":"Unable to submit request because thinking_budget is not supported for this model.","status":"INVALID_ARGUMENT"}}
            """;

        var result = GeminiErrorClassifier.Classify(HttpStatusCode.BadRequest, body);

        Assert.Equal(GeminiErrorKind.UnsupportedParameter, result.Kind);
    }

    [Fact]
    public void UnknownModelId_IsClassifiedAsRetired()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.NotFound, ModelNotFoundBody);

        Assert.Equal(GeminiErrorKind.Retired, result.Kind);
    }

    [Fact]
    public void ServiceUnavailable_IsClassifiedAsTransient()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.ServiceUnavailable, ServerErrorBody);

        Assert.Equal(GeminiErrorKind.Transient, result.Kind);
    }

    [Fact]
    public void Forbidden_IsClassifiedAsUnauthorized()
    {
        const string body = """{"error":{"code":403,"message":"Permission denied","status":"PERMISSION_DENIED"}}""";

        var result = GeminiErrorClassifier.Classify(HttpStatusCode.Forbidden, body);

        Assert.Equal(GeminiErrorKind.Unauthorized, result.Kind);
    }

    [Fact]
    public void Ok_IsClassifiedAsSuccess()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.OK, "{}");

        Assert.Equal(GeminiErrorKind.None, result.Kind);
    }

    [Fact]
    public void Unauthorized_IsClassifiedAsInvalidKey_NotUnknown()
    {
        // 401 used to fall through to Unknown, which RecordHealth deliberately treats as "leave
        // the model's state alone" - so no cooldown was ever set and a rejected key was re-sent
        // on every single lookup, forever. It has to stop the chain like the 400/403 paths do.
        const string body = """{"error":{"code":401,"message":"Request had invalid authentication credentials.","status":"UNAUTHENTICATED"}}""";

        var result = GeminiErrorClassifier.Classify(HttpStatusCode.Unauthorized, body);

        Assert.Equal(GeminiErrorKind.InvalidKey, result.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public void AnySuccessStatus_IsClassifiedAsSuccess(HttpStatusCode status)
    {
        // Only 200 was matched, so any other 2xx was reported as a failure despite the request
        // having worked.
        var result = GeminiErrorClassifier.Classify(status, "");

        Assert.Equal(GeminiErrorKind.None, result.Kind);
    }

    [Fact]
    public void RequestTimeout_IsClassifiedAsTransient()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.RequestTimeout, "");

        Assert.Equal(GeminiErrorKind.Transient, result.Kind);
    }

    [Fact]
    public void MalformedBody_DoesNotThrow_AndFallsBackToStatusCodeOnly()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.TooManyRequests, "not json at all");

        Assert.Equal(GeminiErrorKind.RateLimited, result.Kind);
    }

    [Fact]
    public void EmptyBody_DoesNotThrow()
    {
        var result = GeminiErrorClassifier.Classify(HttpStatusCode.InternalServerError, "");

        Assert.Equal(GeminiErrorKind.Transient, result.Kind);
    }
}
