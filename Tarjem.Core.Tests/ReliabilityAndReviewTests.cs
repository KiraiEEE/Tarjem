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
    public void AMissedWordComesBackInTenMinutes()
    {
        var next = ReviewScheduler.Next(ReviewState.New, ReviewGrade.Again, Now);
        Assert.Equal(Now + ReviewScheduler.RelearnStep, next.Due);
        Assert.Equal(0, next.IntervalDays);
        Assert.False(ReviewScheduler.IsDue(next, Now));
    }

    [Fact]
    public void GoodGrowsTheGapByTheEase()
    {
        var first = ReviewScheduler.Next(ReviewState.New, ReviewGrade.Good, Now);
        Assert.Equal(1, first.IntervalDays);

        var second = ReviewScheduler.Next(first, ReviewGrade.Good, Now.AddDays(1));
        Assert.Equal(2.5, second.IntervalDays);

        var third = ReviewScheduler.Next(second, ReviewGrade.Good, Now.AddDays(4));
        Assert.Equal(6.3, third.IntervalDays, 1);
    }

    [Fact]
    public void ALapseResetsTheGapAndLowersTheEase()
    {
        var known = new ReviewState(Now, 20, 2.5, 5, 0);
        var missed = ReviewScheduler.Next(known, ReviewGrade.Again, Now);

        Assert.Equal(0, missed.IntervalDays);
        Assert.Equal(2.3, missed.Ease, 3);
        Assert.Equal(1, missed.Lapses);

        var relearned = ReviewScheduler.Next(missed, ReviewGrade.Good, Now.AddMinutes(10));
        Assert.Equal(1, relearned.IntervalDays);
    }

    [Fact]
    public void EaseNeverDropsBelowTheFloorAndIntervalsAreCapped()
    {
        var s = new ReviewState(Now, 300, 1.35, 30, 3);
        s = ReviewScheduler.Next(s, ReviewGrade.Hard, Now);
        Assert.Equal(ReviewScheduler.MinimumEase, s.Ease, 3);

        s = ReviewScheduler.Next(s with { IntervalDays = 300 }, ReviewGrade.Easy, Now);
        Assert.Equal(ReviewScheduler.MaximumIntervalDays, s.IntervalDays);
    }

    [Theory]
    [InlineData(ReviewGrade.Again, "10 min")]
    [InlineData(ReviewGrade.Good, "1 day")]
    [InlineData(ReviewGrade.Easy, "4 days")]
    public void ButtonsDescribeWhatTheyDo(ReviewGrade grade, string expected) =>
        Assert.Equal(expected, ReviewScheduler.Describe(ReviewState.New, grade, Now));
}
