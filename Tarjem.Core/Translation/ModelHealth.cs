namespace Tarjem.Core.Translation;

public enum ModelState
{
    Unknown,
    Healthy,
    RateLimited,
    Retired,
    Unauthorized
}

/// <summary>
/// Persisted per-model status so a dead/rate-limited model is skipped on the
/// next lookup instead of being retried (and paying its latency) every time.
/// Mutable + parameterless-constructible so it round-trips through
/// System.Text.Json inside settings.json.
/// </summary>
public sealed class ModelHealth
{
    public string Id { get; set; } = string.Empty;
    public ModelState State { get; set; } = ModelState.Unknown;
    public DateTimeOffset? CooldownUntil { get; set; }
    public int LastLatencyMs { get; set; }
    public string? LastError { get; set; }
}
