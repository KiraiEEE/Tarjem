using System.Windows;
using Tarjem.Core.Learning;
using Tarjem.Models;
using Tarjem.Services;
using Tarjem.Views;
using Xunit;

namespace Tarjem.UiTests;

/// <summary>0.8: the word sense chosen from its sentence, settings repair, review scheduling over
/// real history, and the new windows.</summary>
[Collection(IsolatedDataCollection.Name)]
public class ReliabilityFeatureTests
{
    // ── The word's sense, from its sentence ──

    [Fact]
    public void TheTranslationThatAppearsInTheSentenceWins()
    {
        // "bank" on its own is the money one; the sentence is about a river.
        var chosen = TranslationService.InContext("بنك", ["بنك", "ضفة", "مصرف"], "جلسنا على ضفة النهر");
        Assert.Equal("ضفة", chosen);
    }

    [Fact]
    public void TheFirstChoiceStandsWhenItIsInTheSentenceOrNothingElseIs()
    {
        Assert.Equal("بنك", TranslationService.InContext("بنك", ["ضفة"], "ذهبت إلى البنك"));
        Assert.Equal("بنك", TranslationService.InContext("بنك", ["ضفة"], "جملة أخرى تماما"));
        Assert.Equal("بنك", TranslationService.InContext("بنك", null, "جلسنا على ضفة النهر"));
    }

    // ── Settings repair ──

    [Fact]
    public void BrokenSettingsAreRepairedNotFatal()
    {
        var s = new AppSettings
        {
            TargetLanguage = "klingon",
            SourceLanguage = "zh-Hans",
            RecentTargetLanguages = null!,
            WordHotkey = null!,
            RegionDisplayMode = "Hologram",
            Theme = "",
            MainWindowWidth = 0,
            ShowDictionarySection = false,
            ShowTranslationSection = false,
        }.Sanitized();

        Assert.Equal("ar", s.TargetLanguage);
        Assert.Equal("zh-CN", s.SourceLanguage);
        Assert.NotNull(s.RecentTargetLanguages);
        Assert.NotNull(s.WordHotkey);
        Assert.Equal("InPlace", s.RegionDisplayMode);
        Assert.Equal("System", s.Theme);
        Assert.True(s.MainWindowWidth >= 400);
        Assert.True(s.ShowDictionarySection);
    }

    // ── Review over history ──

    private static TranslationResult Word(string word, string translation = "x") => new()
    {
        OriginalWord = word, ArabicTranslation = translation, ArabicWord = translation,
        Meanings = ["a meaning"], SourceLanguage = "en", TargetLanguage = "ar",
    };

    [Fact]
    public void NewWordsAreDueAndAnsweredWordsAreScheduled() => StaRunner.Run(() =>
    {
        var history = new HistoryService();
        history.Clear();
        history.Add(Word("harbor"));
        history.Add(Word("lantern"));

        var now = DateTime.Now;
        var due = history.DueForReview(now);
        Assert.Equal(2, due.Count);

        history.RecordReview(due[0], ReviewGrade.Good, now);
        Assert.Single(history.DueForReview(now));
        Assert.Equal(2, history.DueForReview(now.AddDays(2)).Count);
    });

    [Fact]
    public void LookingAWordUpAgainKeepsItsReviewProgress() => StaRunner.Run(() =>
    {
        var history = new HistoryService();
        history.Clear();
        history.Add(Word("harbor"));
        var entry = history.Entries[0];
        history.RecordReview(entry, ReviewGrade.Easy, DateTime.Now);

        history.Add(Word("harbor"));

        Assert.Equal(1, history.Entries[0].ReviewCount);
        Assert.Equal(2, history.Entries[0].LookupCount);
    });

    [Fact]
    public void FailedAndEncyclopediaLinesAreNotReviewed() => StaRunner.Run(() =>
    {
        var history = new HistoryService();
        history.Clear();
        history.Add(new TranslationResult { OriginalWord = "zzz", Meanings = ["No definition found"], SourceLanguage = "en", TargetLanguage = "ar" });
        Assert.Empty(history.DueForReview(DateTime.Now));
    });

    // ── New windows ──

    [Fact]
    public void TheNewWindowsCanBeConstructed() => StaRunner.Run(() =>
    {
        var history = new HistoryService();
        history.Clear();
        history.Add(Word("harbor", "ميناء"));

        var review = new ReviewWindow(history) { Left = -32000, Top = -32000, ShowActivated = false };
        review.Show();
        review.Close();

        var bar = new LiveBarWindow();
        bar.SetState(paused: true, busy: false, showingOriginal: true);
        bar.PlaceBeside(new Rect(100, 100, 300, 80));
        bar.Close();

        ToastWindow.Show("No text found under the cursor.", new System.Drawing.Point(200, 200));
    });
}
