namespace Tarjem.Core.Translation;

/// <summary>Result of a single lookup or an explicit "Test AI connection" probe, for display in Settings.</summary>
public abstract record AiStatus
{
    private AiStatus() { }

    public sealed record Ok(string ModelId, int LatencyMs) : AiStatus;
    public sealed record Degraded(GeminiErrorKind Kind, string Reason) : AiStatus;
    public sealed record Off(string Reason) : AiStatus;
}
