using Tarjem.Core.Language;
using Tarjem.Core.Ocr;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// The 0.7 language work: the catalog every picker and provider reads, telling neighbouring
/// languages apart, and the word picker for scripts other than Latin.
/// </summary>
public class MultilingualTests
{
    // ── Catalog ──

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("EN", "en")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("iw", "he")]
    [InlineData("no", "nb")]
    [InlineData("nb-NO", "nb")]
    [InlineData("ro-RO", "ro")]
    public void WindowsAndProviderTagsResolveToCatalogCodes(string tag, string expected) =>
        Assert.Equal(expected, LanguageCatalog.Find(tag)?.Code);

    [Fact]
    public void UnknownTagsResolveToNothingRatherThanEnglish()
    {
        Assert.Null(LanguageCatalog.Find("xx"));
        Assert.Null(LanguageCatalog.Find(""));
        Assert.Null(LanguageCatalog.Find(LanguageCatalog.Auto));
    }

    [Fact]
    public void NorwegianAsksForTheWikipediaAndGoogleCodesThatExist()
    {
        var norwegian = LanguageCatalog.Find("nb")!;
        Assert.Equal("no", norwegian.Wiki);
        Assert.Equal("no", norwegian.Google);
        Assert.Equal("iw", LanguageCatalog.Find("he")!.Google);
        Assert.Equal("zh", LanguageCatalog.Find("zh-TW")!.Wiki);
    }

    [Fact]
    public void RightToLeftLanguagesAreMarked()
    {
        foreach (var code in new[] { "ar", "fa", "ur", "he" })
            Assert.True(LanguageCatalog.IsRtl(code), code);
        Assert.False(LanguageCatalog.IsRtl("uk"));
    }

    [Fact]
    public void EveryLanguageHasAUniqueCodeAndANativeName()
    {
        Assert.Equal(LanguageCatalog.All.Count, LanguageCatalog.All.Select(l => l.Code).Distinct().Count());
        Assert.All(LanguageCatalog.All, l => Assert.False(string.IsNullOrWhiteSpace(l.NativeName)));
    }

    [Fact]
    public void ShortLabelsAreTheBaseTag()
    {
        Assert.Equal("ZH", LanguageCatalog.ShortLabel("zh-CN"));
        Assert.Equal("Auto", LanguageCatalog.ShortLabel(LanguageCatalog.Auto));
    }

    // ── Scripts ──

    [Theory]
    [InlineData("el", LanguageGuesser.Script.Greek)]
    [InlineData("he", LanguageGuesser.Script.Hebrew)]
    [InlineData("th", LanguageGuesser.Script.Thai)]
    [InlineData("bn", LanguageGuesser.Script.Bengali)]
    [InlineData("uk", LanguageGuesser.Script.Cyrillic)]
    [InlineData("vi", LanguageGuesser.Script.Latin)]
    public void NewLanguagesMapToTheirScript(string code, LanguageGuesser.Script expected) =>
        Assert.Equal(expected, LanguageGuesser.ScriptOf(code));

    [Fact]
    public void AGreekEngineReadingGreekScoresAboveALatinOne()
    {
        const string greek = "Καλημέρα κόσμε";
        Assert.True(LanguageGuesser.ReadingScore(greek, "el") > LanguageGuesser.ReadingScore(greek, "en"));
    }

    // ── Within a script ──

    [Fact]
    public void UkrainianIsToldApartFromRussian()
    {
        var candidates = new[] { "ru", "uk", "bg" };
        Assert.Equal("uk", LanguageGuesser.GuessWithinScript("Це її книга, і вона дуже гарна", candidates, "ru"));
        Assert.Equal("ru", LanguageGuesser.GuessWithinScript("Это была очень хорошая книга, мы её читали", candidates, "uk"));
    }

    [Fact]
    public void CzechIsToldApartFromSlovak()
    {
        var candidates = new[] { "cs", "sk", "pl" };
        Assert.Equal("cs", LanguageGuesser.GuessWithinScript("Přečetl jsem tu knihu, je opravdu skvělá", candidates, "pl"));
        Assert.Equal("sk", LanguageGuesser.GuessWithinScript("Ľudia sú tu veľmi milí a pôjdem znova", candidates, "pl"));
    }

    [Fact]
    public void RomanianAndVietnameseAreRecognisedByTheirLetters()
    {
        var candidates = new[] { "en", "ro", "vi", "fr" };
        Assert.Equal("ro", LanguageGuesser.GuessWithinScript("Aceasta este o țară frumoasă și liniștită", candidates, "en"));
        Assert.Equal("vi", LanguageGuesser.GuessWithinScript("Tôi không biết người đó là ai", candidates, "en"));
    }

    // ── Word picking outside English ──

    private static RecognizedText OneLine(params (string Text, double X, double Width)[] words) =>
        RecognizedText.FromWords(words.Select((w, i) => new RecognizedWord(w.Text, new TextRect(w.X, 10, w.Width, 20), 0, i)));

    [Fact]
    public void HindiKeepsItsVowelSigns()
    {
        // हिंदी: ि and ी are spacing marks, ं a non-spacing one. All three used to be stripped.
        var text = OneLine(("हिंदी", 10, 60), ("भाषा", 80, 50));
        var match = WordMatcher.FindBestWord(text, 30, 20, "hi");

        Assert.Equal("हिंदी", match.Target);
    }

    [Fact]
    public void ArabicKeepsItsMarks()
    {
        var text = OneLine(("مُدَرِّس", 10, 70));
        Assert.Equal("مُدَرِّس", WordMatcher.FindBestWord(text, 40, 20, "ar").Target);
    }

    [Fact]
    public void FrenchWordsAreNotMergedIntoEnglishOnes()
    {
        // "ont" + "o" would be nonsense in English too, but "car" + "pet" spells an English word.
        // In French they are two words: "car" (because) and "pet".
        var text = OneLine(("car", 10, 30), ("pet", 42, 30));
        var match = WordMatcher.FindBestWord(text, 38, 20, "fr");

        Assert.Equal("car", match.Target);
    }

    [Fact]
    public void JapaneseIsRegroupedIntoTheSegmentersWord()
    {
        // OCR reports one character per word; the segmenter says 天気 is one word.
        var text = OneLine(("今", 10, 20), ("日", 30, 20), ("は", 50, 20), ("天", 70, 20), ("気", 90, 20), ("が", 110, 20));

        WordSegmenter segmenter = (s, _) =>
        {
            Assert.Equal("今日は天気が", s);
            return [(0, 3), (3, 2), (5, 1)];
        };

        var match = WordMatcher.FindBestWord(text, 95, 20, "ja", segmenter);

        Assert.Equal("天気", match.Target);
        Assert.Equal(70, match.Word!.Bounds.X, 3);
        Assert.Equal(40, match.Word.Bounds.Width, 3);
    }

    [Fact]
    public void AWordStartingPartwayThroughAnOcrTokenGetsATrimmedBox()
    {
        // OCR grouped 今日は into one token; the segmenter splits off は.
        var text = OneLine(("今日は", 10, 60), ("天気", 70, 40));
        WordSegmenter segmenter = (_, _) => [(0, 2), (2, 1), (3, 2)];

        var match = WordMatcher.FindBestWord(text, 25, 20, "ja", segmenter);

        Assert.Equal("今日", match.Target);
        Assert.Equal(10, match.Word!.Bounds.X, 3);
        Assert.Equal(40, match.Word.Bounds.Width, 3);
    }

    [Fact]
    public void ASegmenterThatThrowsLeavesTheOcrWordAlone()
    {
        var text = OneLine(("天", 10, 20), ("気", 30, 20));
        WordSegmenter broken = (_, _) => throw new InvalidOperationException();

        Assert.Equal("天", WordMatcher.FindBestWord(text, 15, 20, "ja", broken).Target);
    }

    [Fact]
    public void WithoutASegmenterJapaneseFallsBackToTheOcrWord()
    {
        var text = OneLine(("天", 10, 20), ("気", 30, 20));
        Assert.Equal("気", WordMatcher.FindBestWord(text, 35, 20, "ja").Target);
    }
}
