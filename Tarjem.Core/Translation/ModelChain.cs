namespace Tarjem.Core.Translation;

/// <summary>
/// Ordered fallback list of Gemini models, cheapest/fastest first. Latencies are from a
/// live benchmark against the free tier: gemini-flash-lite-latest ~0.65-0.83s,
/// gemini-3.1-flash-lite ~1.0s, gemini-flash-latest ~6.4s (it is a thinking model - kept
/// only as a last resort). gemini-2.0-flash is deliberately absent: it returns
/// 429 "limit: 0" on every key, i.e. it is retired from the free tier, not merely busy.
/// </summary>
public static class ModelChain
{
    public static readonly IReadOnlyList<GeminiModel> Default =
    [
        new GeminiModel("gemini-flash-lite-latest", 4000),
        new GeminiModel("gemini-3.1-flash-lite", 4000),
        new GeminiModel("gemini-flash-latest", 9000)
    ];

    /// <summary>
    /// Yields chain entries in order, skipping models that are still inside the cooldown their
    /// last failure earned them.
    ///
    /// Every unhealthy state is time-boxed - none of them is permanent. An earlier version
    /// treated Retired/Unauthorized as forever, which turned one bad afternoon into a
    /// permanently dead feature: a key that Google had temporarily flagged (or a model that was
    /// briefly 404ing) marked every model in the chain Unauthorized, that verdict was persisted
    /// to settings.json, and from then on this method yielded nothing at all - so the app never
    /// discovered that the key had started working again. States differ only in how long they
    /// wait before being retried, never in whether they are.
    /// </summary>
    public static IEnumerable<GeminiModel> UsableInOrder(
        IReadOnlyList<GeminiModel> chain,
        IReadOnlyDictionary<string, ModelHealth> health,
        DateTimeOffset now)
    {
        foreach (var model in chain)
        {
            if (!health.TryGetValue(model.Id, out var h))
            {
                yield return model;
                continue;
            }

            if (h.State is ModelState.Healthy or ModelState.Unknown)
            {
                yield return model;
                continue;
            }

            if (h.CooldownUntil is { } until && until > now)
                continue;

            yield return model;
        }
    }

    /// <summary>How long a model sits out after each kind of failure. Retired waits the longest
    /// (a genuinely removed model won't be back soon) but still expires, so a model id that
    /// starts resolving again - or a free tier whose quota is restored - recovers on its own.</summary>
    public static TimeSpan CooldownFor(ModelState state) => state switch
    {
        ModelState.RateLimited => TimeSpan.FromSeconds(60),
        ModelState.Unauthorized => TimeSpan.FromMinutes(30),
        ModelState.Retired => TimeSpan.FromDays(7),
        _ => TimeSpan.Zero,
    };
}
