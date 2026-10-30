using System.IO;
using FlowDirection = System.Windows.FlowDirection;
using Tarjem.Models;
using Tarjem.Services;
using Xunit;

namespace Tarjem.UiTests;

[Collection(IsolatedDataCollection.Name)]
public class HistoryServiceTests
{
    private static TranslationResult Result(string word = "library", string target = "ar", string translation = "مكتبة") => new()
    {
        OriginalWord = word,
        Meanings = ["A building containing books."],
        ArabicTranslation = translation,
        FullSentence = "The library was quiet.",
        TranslatedSentence = "كانت المكتبة هادئة.",
        CefrLevel = "A2",
        SourceLanguage = "en",
        TargetLanguage = target,
    };

    /// <summary>Every test shares the isolated data folder, so start from an empty history.</summary>
    private static HistoryService FreshHistory()
    {
        var history = new HistoryService();
        history.Clear();
        history.Flush(TimeSpan.FromSeconds(5));
        return history;
    }

    [Fact]
    public void LookingAWordUpAgainCountsItInsteadOfAddingADuplicate()
    {
        var history = FreshHistory();

        history.Add(Result());
        history.Add(Result("other"));
        history.Add(Result("LIBRARY"));

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal("LIBRARY", history.Entries[0].Word);
        Assert.Equal(2, history.Entries[0].LookupCount);
        Assert.Equal("×2", history.Entries[0].LookupCountBadge);
    }

    [Fact]
    public void ARepeatLookupKeepsTheFirstSeenDateAndFillsFieldsTheNewAnswerLacks()
    {
        var history = FreshHistory();

        history.Add(Result());
        var first = history.Entries[0].Timestamp;

        var thin = Result();
        thin.Meanings = [];
        thin.CefrLevel = "";
        history.Add(thin);

        var merged = history.Entries[0];
        Assert.Equal(first, merged.FirstLookedUp);
        Assert.Equal("A building containing books.", merged.Definition);
        Assert.Equal("A2", merged.CefrLevel);
    }

    [Fact]
    public void TheSameWordInAnotherTargetLanguageIsItsOwnEntry()
    {
        var history = FreshHistory();

        history.Add(Result(target: "ar"));
        history.Add(Result(target: "fr", translation: "bibliothèque"));

        Assert.Equal(2, history.Entries.Count);
    }

    [Fact]
    public void NewFieldsSurviveARoundTripToDisk()
    {
        var history = FreshHistory();
        history.Add(Result());
        history.Add(Result());
        history.Flush(TimeSpan.FromSeconds(5));

        var reloaded = new HistoryService();
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal(2, entry.LookupCount);
        Assert.Equal("ar", entry.TargetLanguage);
        Assert.Equal("en", entry.SourceLanguage);
        Assert.NotNull(entry.FirstLookedUp);
    }

    [Fact]
    public void AnUnreadableHistoryFileIsSetAsideRatherThanOverwritten()
    {
        FreshHistory();
        var folder = Path.GetDirectoryName(AppPaths.HistoryFile)!;
        foreach (var old in Directory.GetFiles(folder, "history.json.corrupt-*")) File.Delete(old);

        File.WriteAllText(AppPaths.HistoryFile, "{ not json");
        var history = new HistoryService();

        Assert.Empty(history.Entries);
        var backup = Assert.Single(Directory.GetFiles(folder, "history.json.corrupt-*"));
        Assert.Equal("{ not json", File.ReadAllText(backup));

        File.Delete(backup);
        history.Clear();
        history.Flush(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CachedResultsAreKeyedCaseInsensitively()
    {
        var history = FreshHistory();
        var result = Result();

        history.CacheResult("scope|Library", result);

        Assert.Same(result, history.GetCached("scope|LIBRARY"));
        Assert.Null(history.GetCached("other-scope|library"));
    }

    [Theory]
    [InlineData("ar", "مكتبة", FlowDirection.RightToLeft)]
    [InlineData("fr", "bibliothèque", FlowDirection.LeftToRight)]
    [InlineData("", "مكتبة", FlowDirection.RightToLeft)] // saved before the language was recorded
    [InlineData("", "bibliothèque", FlowDirection.LeftToRight)]
    public void TranslationDirectionFollowsTheLanguageNotAHardCodedRightToLeft(string target, string text, FlowDirection expected)
    {
        var entry = new HistoryEntry { TargetLanguage = target, ArabicTranslation = text };
        Assert.Equal(expected, entry.TranslationFlowDirection);
    }
}
