namespace Tarjem.Core.Learning;

/// <summary>How well the learner remembered a word.</summary>
public enum ReviewGrade
{
    /// <summary>Didn't remember it. Shown again in a few minutes.</summary>
    Again = 1,
    /// <summary>Remembered, with effort.</summary>
    Hard = 2,
    /// <summary>Remembered.</summary>
    Good = 3,
    /// <summary>Remembered instantly.</summary>
    Easy = 4,
}

/// <summary>Where one word stands in review - FSRS's memory model of it.</summary>
/// <param name="Due">When it's next due; null for a word never reviewed.</param>
/// <param name="IntervalDays">The gap that produced <paramref name="Due"/>; 0 while relearning.</param>
/// <param name="Stability">Days until recall probability falls to 90%.</param>
/// <param name="Difficulty">1 (easy) to 10 (hard).</param>
/// <param name="LastReview">When it was last answered.</param>
public readonly record struct ReviewState(
    DateTime? Due, double IntervalDays, double Stability, double Difficulty, int Reviews, int Lapses, DateTime? LastReview)
{
    public static readonly ReviewState New = new(null, 0, 0, 0, 0, 0, null);

    public bool IsNew => Reviews == 0;
}

/// <summary>
/// Spaced repetition for the words in History, using FSRS-5 - the Free Spaced Repetition
/// Scheduler that replaced SM-2 as Anki's recommended algorithm.
///
/// 0.8 first shipped this as SM-2, the 1987 algorithm behind early SuperMemo and old Anki. FSRS
/// models each word's memory with a stability (how long until recall drops to 90%) and a
/// difficulty, and schedules the next review for when recall is predicted to reach the target
/// retention. Benchmarked on roughly 350 million reviews from about 10,000 Anki collections, its
/// default parameters predict recall better than SM-2 for nearly every user, so the same retention
/// costs fewer reviews - a learner's time being the scarce thing here. The defaults are used as-is:
/// optimizing them needs hundreds of reviews per user, which a vocabulary list built from hotkey
/// presses won't have for a long time.
///
/// Formulas and the default weights are FSRS-5's, from the open-spaced-repetition project
/// (awesome-fsrs wiki, "The Algorithm").
/// </summary>
public static class ReviewScheduler
{
    /// <summary>FSRS-5 default weights, w0..w18.</summary>
    private static readonly double[] W =
    [
        0.40255, 1.18385, 3.173, 15.69105, 7.1949, 0.5345, 1.4604, 0.0046, 1.54575, 0.1192,
        1.01925, 1.9395, 0.11, 0.29605, 2.2698, 0.2315, 2.9898, 0.51655, 0.6621,
    ];

    private const double Decay = -0.5;
    private const double Factor = 19.0 / 81.0;

    /// <summary>Probability of recall the schedule aims for. 0.9 is FSRS's and Anki's default:
    /// higher costs disproportionately more reviews, lower forgets noticeably more.</summary>
    public const double DesiredRetention = 0.9;

    public const double MaximumIntervalDays = 365;

    /// <summary>How soon a missed word comes back - a relearning step, as in Anki.</summary>
    public static readonly TimeSpan RelearnStep = TimeSpan.FromMinutes(10);

    /// <summary>Probability the word is recalled <paramref name="elapsedDays"/> after the last
    /// review, given its stability (FSRS's power forgetting curve).</summary>
    public static double Retrievability(double elapsedDays, double stability) =>
        stability <= 0 ? 0 : Math.Pow(1 + Factor * Math.Max(0, elapsedDays) / stability, Decay);

    /// <summary>The gap at which recall is predicted to fall to <see cref="DesiredRetention"/>.</summary>
    public static double IntervalFor(double stability) =>
        stability / Factor * (Math.Pow(DesiredRetention, 1 / Decay) - 1);

    public static ReviewState Next(ReviewState state, ReviewGrade grade, DateTime now)
    {
        var g = (int)grade;
        double stability, difficulty;

        if (state.IsNew || state.Stability <= 0)
        {
            stability = W[g - 1];
            difficulty = InitialDifficulty(g);
        }
        else
        {
            var elapsed = state.LastReview is { } last ? (now - last).TotalDays : state.IntervalDays;
            difficulty = NextDifficulty(state.Difficulty, g);

            if (elapsed < 1)
            {
                // Seen again the same day (relearning after a miss): FSRS-5's short-term update.
                stability = state.Stability * Math.Exp(W[17] * (g - 3 + W[18]));
            }
            else
            {
                var r = Retrievability(elapsed, state.Stability);
                stability = grade == ReviewGrade.Again
                    ? Forgotten(state.Difficulty, state.Stability, r)
                    : Recalled(state.Difficulty, state.Stability, r, grade);
            }
        }

        stability = Math.Clamp(stability, 0.01, 36500);
        var lapses = state.Lapses + (grade == ReviewGrade.Again && !state.IsNew ? 1 : 0);

        if (grade == ReviewGrade.Again)
            return new ReviewState(now + RelearnStep, 0, stability, difficulty, state.Reviews + 1, lapses, now);

        var interval = Math.Clamp(Math.Round(IntervalFor(stability)), 1, MaximumIntervalDays);

        // Each better answer must never schedule sooner than a worse one would have.
        if (grade == ReviewGrade.Good) interval = Math.Max(interval, Bound(state, ReviewGrade.Hard, now) + 1);
        if (grade == ReviewGrade.Easy) interval = Math.Max(interval, Bound(state, ReviewGrade.Good, now) + 1);
        interval = Math.Min(interval, MaximumIntervalDays);

        return new ReviewState(now.AddDays(interval), interval, stability, difficulty, state.Reviews + 1, lapses, now);
    }

    private static double Bound(ReviewState state, ReviewGrade grade, DateTime now) => Next(state, grade, now).IntervalDays;

    private static double InitialDifficulty(int g) =>
        Math.Clamp(W[4] - Math.Exp(W[5] * (g - 1)) + 1, 1, 10);

    private static double NextDifficulty(double d, int g)
    {
        var delta = -W[6] * (g - 3);
        var damped = d + delta * (10 - d) / 9;
        var reverted = W[7] * InitialDifficulty(4) + (1 - W[7]) * damped;
        return Math.Clamp(reverted, 1, 10);
    }

    private static double Recalled(double d, double s, double r, ReviewGrade grade)
    {
        var hard = grade == ReviewGrade.Hard ? W[15] : 1;
        var easy = grade == ReviewGrade.Easy ? W[16] : 1;
        return s * (Math.Exp(W[8]) * (11 - d) * Math.Pow(s, -W[9]) * (Math.Exp(W[10] * (1 - r)) - 1) * hard * easy + 1);
    }

    private static double Forgotten(double d, double s, double r)
    {
        var next = W[11] * Math.Pow(d, -W[12]) * (Math.Pow(s + 1, W[13]) - 1) * Math.Exp(W[14] * (1 - r));
        return Math.Min(next, s);
    }

    public static bool IsDue(ReviewState state, DateTime now) => state.Due == null || state.Due <= now;

    /// <summary>What a button will do, for its label: "10 min", "3 days", "2 wk".</summary>
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
