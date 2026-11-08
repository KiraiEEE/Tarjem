namespace Tarjem.Core.Learning;

/// <summary>How well the learner remembered a word.</summary>
public enum ReviewGrade
{
    /// <summary>Didn't remember it. Shown again in a few minutes, and the word gets harder.</summary>
    Again,
    /// <summary>Remembered, with effort.</summary>
    Hard,
    /// <summary>Remembered.</summary>
    Good,
    /// <summary>Remembered instantly.</summary>
    Easy,
}

/// <summary>Where one word stands in review.</summary>
/// <param name="Due">When it's next due; null for a word never reviewed.</param>
/// <param name="IntervalDays">The gap that produced <paramref name="Due"/>; 0 while still learning.</param>
/// <param name="Ease">How quickly the gaps grow (SM-2's ease factor).</param>
public readonly record struct ReviewState(DateTime? Due, double IntervalDays, double Ease, int Reviews, int Lapses)
{
    public static readonly ReviewState New = new(null, 0, ReviewScheduler.StartingEase, 0, 0);

    public bool IsNew => Reviews == 0;
}

/// <summary>
/// Spaced repetition for the words in History, so the vocabulary list is something to learn from
/// rather than only a record. The schedule is SM-2 - the algorithm behind the original SuperMemo and
/// Anki's default - with Anki's four buttons and its learning step: a word you miss comes back in
/// ten minutes, not tomorrow, and a word you know grows its gap by its ease every time.
///
/// Kept deliberately standard. People who study seriously already know how these buttons behave
/// from Anki, and a schedule that differs in clever ways is one they can't predict.
/// </summary>
public static class ReviewScheduler
{
    public const double StartingEase = 2.5;
    public const double MinimumEase = 1.3;
    public const double MaximumIntervalDays = 365;

    /// <summary>How soon a missed word comes back.</summary>
    public static readonly TimeSpan RelearnStep = TimeSpan.FromMinutes(10);

    public static ReviewState Next(ReviewState state, ReviewGrade grade, DateTime now)
    {
        var ease = state.Ease <= 0 ? StartingEase : state.Ease;
        var reviews = state.Reviews + 1;

        if (grade == ReviewGrade.Again)
        {
            return new ReviewState(now + RelearnStep, 0,
                state.IsNew ? ease : Math.Max(MinimumEase, ease - 0.2),
                reviews, state.IsNew ? 0 : state.Lapses + 1);
        }

        double interval;
        if (state.IntervalDays <= 0)
        {
            // New, or just relearned after a miss.
            interval = grade switch { ReviewGrade.Hard => 1, ReviewGrade.Good => 1, _ => 4 };
        }
        else
        {
            interval = grade switch
            {
                ReviewGrade.Hard => state.IntervalDays * 1.2,
                ReviewGrade.Good => state.IntervalDays * ease,
                _ => state.IntervalDays * ease * 1.3,
            };
            interval = Math.Max(interval, state.IntervalDays + 1);
        }

        ease = grade switch
        {
            ReviewGrade.Hard => Math.Max(MinimumEase, ease - 0.15),
            ReviewGrade.Easy => ease + 0.15,
            _ => ease,
        };

        interval = Math.Min(MaximumIntervalDays, Math.Round(interval, 1, MidpointRounding.AwayFromZero));
        return new ReviewState(now.AddDays(interval), interval, ease, reviews, state.Lapses);
    }

    public static bool IsDue(ReviewState state, DateTime now) => state.Due == null || state.Due <= now;

    /// <summary>What a button will do, for its label: "10 min", "1 day", "3 wk".</summary>
    public static string Describe(ReviewState state, ReviewGrade grade, DateTime now)
    {
        var next = Next(state, grade, now).Due!.Value - now;
        if (next.TotalHours < 1) return $"{Math.Max(1, (int)Math.Round(next.TotalMinutes))} min";
        if (next.TotalDays < 1) return $"{(int)Math.Round(next.TotalHours)} h";
        if (next.TotalDays < 14) return $"{(int)Math.Round(next.TotalDays)} day{(Math.Round(next.TotalDays) == 1 ? "" : "s")}";
        if (next.TotalDays < 60) return $"{(int)Math.Round(next.TotalDays / 7)} wk";
        if (next.TotalDays < 365) return $"{(int)Math.Round(next.TotalDays / 30)} mo";
        return $"{next.TotalDays / 365:0.#} yr";
    }
}
