using Tarjem.Core.Learning;
using Tarjem.Core.Providers;
using Xunit;

namespace Tarjem.Core.Tests;

public class ProviderHealthTests
{
    private DateTime _now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private ProviderHealthTracker Tracker() => new(() => _now);

    [Fact]
    public void OneFailureIsForgivenTwoCoolTheSourceDown()
    {
        var t = Tracker();
        t.RecordFailure("lingva");
        Assert.True(t.IsHealthy("lingva"));

        t.RecordFailure("lingva");
        Assert.False(t.IsHealthy("lingva"));
        Assert.Equal(ProviderHealthTracker.BaseCooldown, t.CoolingDownFor("lingva"));
    }

    [Fact]
    public void CooldownsDoubleAndAreCapped()
    {
        var t = Tracker();
        for (var i = 0; i < 3; i++) t.RecordFailure("google");
        Assert.Equal(TimeSpan.FromSeconds(60), t.CoolingDownFor("google"));

        for (var i = 0; i < 20; i++) t.RecordFailure("google");
        Assert.Equal(ProviderHealthTracker.MaxCooldown, t.CoolingDownFor("google"));
    }

    [Fact]
    public void ARateLimitCoolsDownAtOnce()
    {
        var t = Tracker();
        t.RecordFailure("google", rateLimited: true);
        Assert.Equal(ProviderHealthTracker.RateLimitCooldown, t.CoolingDownFor("google"));
    }

    [Fact]
    public void ASuccessRestoresTheSource()
    {
        var t = Tracker();
        t.RecordFailure("google");
        t.RecordFailure("google");
        t.RecordSuccess("google");
        Assert.True(t.IsHealthy("google"));

        // And the count starts over: one new failure is forgiven again.
        t.RecordFailure("google");
        Assert.True(t.IsHealthy("google"));
    }

    [Fact]
    public void CooldownsExpire()
    {
        var t = Tracker();
        t.RecordFailure("mymemory");
        t.RecordFailure("mymemory");
        _now += TimeSpan.FromSeconds(31);
        Assert.True(t.IsHealthy("mymemory"));
    }

    [Fact]
    public void CoolingSourcesMoveToTheBackButAreNeverDropped()
    {
        var t = Tracker();
        t.RecordFailure("google");
        t.RecordFailure("google");

        var ordered = t.Order(new[] { "google", "gemini", "mymemory" }, s => s);
        Assert.Equal(["gemini", "mymemory", "google"], ordered);
    }
}

public class ReviewSchedulerTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 9, 0, 0);

    [Fact]
    public void ANewWordIsDue()
    {
        Assert.True(ReviewScheduler.IsDue(ReviewState.New, Now));
        Assert.True(ReviewState.New.IsNew);
    }

    [Fact]
    public void TheForgettingCurvePassesNinetyPercentAtOneStability()
    {
        Assert.Equal(0.9, ReviewScheduler.Retrievability(10, 10), 6);
        Assert.Equal(1.0, ReviewScheduler.Retrievability(0, 10), 6);
        Assert.True(ReviewScheduler.Retrievability(30, 10) < 0.9);

        // At 90% desired retention, the interval equals the stability.
        Assert.Equal(7.5, ReviewScheduler.IntervalFor(7.5), 6);
    }

    [Theory]
    // FSRS-5 initial stabilities w0..w3, rounded to whole days (at least one).
    [InlineData(ReviewGrade.Hard, 1)]
    [InlineData(ReviewGrade.Good, 3)]
    [InlineData(ReviewGrade.Easy, 16)]
    public void FirstAnswersUseTheDefaultInitialStabilities(ReviewGrade grade, double days)
    {
        var next = ReviewScheduler.Next(ReviewState.New, grade, Now);
        Assert.Equal(days, next.IntervalDays);
        Assert.Equal(Now.AddDays(days), next.Due);
    }

    [Fact]
    public void AMissedWordComesBackInTenMinutes()
    {
        var next = ReviewScheduler.Next(ReviewState.New, ReviewGrade.Again, Now);
        Assert.Equal(Now + ReviewScheduler.RelearnStep, next.Due);
        Assert.Equal(0, next.IntervalDays);
        Assert.False(ReviewScheduler.IsDue(next, Now));
    }

    [Fact]
    public void RecallingOnTimeGrowsTheGap()
    {
        var s = ReviewScheduler.Next(ReviewState.New, ReviewGrade.Good, Now);
        var t = Now;
        var previous = s.IntervalDays;
        for (var i = 0; i < 4; i++)
        {
            t = s.Due!.Value;
            s = ReviewScheduler.Next(s, ReviewGrade.Good, t);
            Assert.True(s.IntervalDays > previous, $"review {i}: {s.IntervalDays} after {previous}");
            previous = s.IntervalDays;
        }
    }

    [Fact]
    public void ALapseCutsStabilityAndRaisesDifficulty()
    {
        var known = ReviewScheduler.Next(ReviewState.New, ReviewGrade.Good, Now);
        known = ReviewScheduler.Next(known, ReviewGrade.Good, known.Due!.Value);
        var when = known.Due!.Value;

        var missed = ReviewScheduler.Next(known, ReviewGrade.Again, when);

        Assert.True(missed.Stability < known.Stability);
        Assert.True(missed.Difficulty > known.Difficulty);
        Assert.Equal(1, missed.Lapses);
        Assert.Equal(when + ReviewScheduler.RelearnStep, missed.Due);

        // Relearned the same day: back to at least a day.
        var relearned = ReviewScheduler.Next(missed, ReviewGrade.Good, when.AddMinutes(10));
        Assert.True(relearned.IntervalDays >= 1);
    }

    [Fact]
    public void BetterAnswersNeverScheduleSoonerAndIntervalsAreCapped()
    {
        var s = ReviewScheduler.Next(ReviewState.New, ReviewGrade.Good, Now);
        var t = s.Due!.Value;
        var hard = ReviewScheduler.Next(s, ReviewGrade.Hard, t).IntervalDays;
        var good = ReviewScheduler.Next(s, ReviewGrade.Good, t).IntervalDays;
        var easy = ReviewScheduler.Next(s, ReviewGrade.Easy, t).IntervalDays;
        Assert.True(hard < good && good < easy, $"{hard} {good} {easy}");

        var veteran = new ReviewState(Now, 300, 5000, 2, 40, 0, Now.AddDays(-300));
        Assert.Equal(ReviewScheduler.MaximumIntervalDays, ReviewScheduler.Next(veteran, ReviewGrade.Easy, Now).IntervalDays);
    }

    [Theory]
    [InlineData(ReviewGrade.Again, "10 min")]
    [InlineData(ReviewGrade.Good, "3 days")]
    [InlineData(ReviewGrade.Easy, "2 wk")]
    public void ButtonsDescribeWhatTheyDo(ReviewGrade grade, string expected) =>
        Assert.Equal(expected, ReviewScheduler.Describe(ReviewState.New, grade, Now));
}
