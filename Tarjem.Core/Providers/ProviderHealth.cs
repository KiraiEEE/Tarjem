namespace Tarjem.Core.Providers;

/// <summary>
/// Remembers which sources are failing right now, so the failover chain stops paying for them on
/// every lookup.
///
/// Without this a dead Lingva mirror, or Google rate-limiting a heavy session, cost its full
/// timeout on every single press before the next source was even asked - the app felt slow in
/// exactly the moments it was already struggling. A source that fails twice in a row is moved to
/// the back of the chain for a while, longer each time it keeps failing; one success puts it back.
/// It is never dropped outright: when everything is cooling down, the chain still tries them all,
/// because "probably still down" is a better bet than certain failure.
///
/// Only transport failures count - timeouts, refused connections, 429s and 5xx. A dictionary that
/// answers "no entry for that word" is working perfectly.
/// </summary>
public sealed class ProviderHealthTracker
{
    /// <summary>First cool-down, doubled for every further consecutive failure up to <see cref="MaxCooldown"/>.</summary>
    public static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(10);

    /// <summary>A rate limit is the service saying "not now" outright; wait at least this long.</summary>
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);

    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, (int Failures, DateTime Until)> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ProviderHealthTracker(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public void RecordSuccess(string key)
    {
        lock (_gate) _state.Remove(key);
    }

    public void RecordFailure(string key, bool rateLimited = false)
    {
        lock (_gate)
        {
            _state.TryGetValue(key, out var s);
            var failures = s.Failures + 1;
            var now = _clock();

            var cooldown = TimeSpan.Zero;
            if (failures >= 2)
            {
                var factor = Math.Pow(2, Math.Min(10, failures - 2));
                cooldown = TimeSpan.FromTicks(Math.Min(MaxCooldown.Ticks, (long)(BaseCooldown.Ticks * factor)));
            }
            if (rateLimited && cooldown < RateLimitCooldown)
                cooldown = RateLimitCooldown;

            var until = now + cooldown;
            _state[key] = (failures, until > s.Until ? until : s.Until);
        }
    }

    /// <summary>How much longer <paramref name="key"/> is being skipped, or null when it isn't.</summary>
    public TimeSpan? CoolingDownFor(string key)
    {
        lock (_gate)
        {
            if (!_state.TryGetValue(key, out var s)) return null;
            var left = s.Until - _clock();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    public bool IsHealthy(string key) => CoolingDownFor(key) == null;

    /// <summary>The chain with every source that is cooling down moved to the end, keeping the
    /// order within each group.</summary>
    public IReadOnlyList<T> Order<T>(IReadOnlyList<T> chain, Func<T, string> key)
    {
        var healthy = new List<T>(chain.Count);
        var cooling = new List<T>();
        foreach (var item in chain)
            (IsHealthy(key(item)) ? healthy : cooling).Add(item);

        healthy.AddRange(cooling);
        return healthy;
    }

    /// <summary>Every source currently being skipped, for the diagnostics report.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            return _state
                .Where(kv => kv.Value.Until > now)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Until - now);
        }
    }
}

/// <summary>A source could not be reached or refused to answer - as opposed to answering "no".</summary>
public sealed class ProviderUnavailableException(string message, bool rateLimited = false) : Exception(message)
{
    public bool RateLimited { get; } = rateLimited;
}
