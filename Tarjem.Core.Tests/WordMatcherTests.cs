using Tarjem.Core.Ocr;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// Covers the part of the app that decides <em>which word you meant</em>. This logic used to live
/// in the WPF project welded to the Windows OCR types, so none of it could be tested - and every
/// bug found in it was of the same shape: a confidently wrong answer, with nothing crashing and no
/// error shown.
/// </summary>
public class WordMatcherTests
{
    /// <summary>Builds a line of evenly-spaced words at a given vertical position, the way OCR
    /// would report them.</summary>
    private static IEnumerable<RecognizedWord> Line(int lineIndex, double y, params string[] words)
    {
        double x = 10;
        for (var i = 0; i < words.Length; i++)
        {
            var width = words[i].Length * 10;
            yield return new RecognizedWord(words[i], new TextRect(x, y, width, 16), lineIndex, i);
            x += width + 8;
        }
    }

    private static RecognizedText Page(params IEnumerable<RecognizedWord>[] lines) =>
        RecognizedText.FromWords(lines.SelectMany(l => l));

    // ── Which word ──

    [Fact]
    public void PicksTheWordUnderTheCursor()
    {
        var page = Page(Line(0, 100, "the", "quiet", "library"));

        // "quiet" starts at x=46 (10 + 3*10 + 8) and is 50 wide.
        var match = WordMatcher.FindBestWord(page, cursorX: 60, cursorY: 108);

        Assert.True(match.Found);
        Assert.Equal("quiet", match.Target);
    }

    [Fact]
    public void ReturnsNothingWhenTheCursorIsNowhereNearAnyWord()
    {
        var page = Page(Line(0, 100, "the", "quiet", "library"));

        var match = WordMatcher.FindBestWord(page, cursorX: 600, cursorY: 600);

        Assert.False(match.Found);
        Assert.Null(match.Word);
    }

    [Fact]
    public void PrefersTheNearerOfTwoOverlappingWords()
    {
        // A short word overlapping a longer neighbour: the nearest centre should win, not the
        // smallest box, which used to systematically favour "a" and "I".
        var page = RecognizedText.FromWords(
        [
            new RecognizedWord("a", new TextRect(100, 100, 10, 16), 0, 0),
            new RecognizedWord("magnificent", new TextRect(105, 100, 110, 16), 0, 1),
        ]);

        var match = WordMatcher.FindBestWord(page, cursorX: 180, cursorY: 108);

        Assert.Equal("magnificent", match.Target);
    }

    // ── Context ──

    [Fact]
    public void ContextComesFromTheLineTheWordIsOn()
    {
        // Three lines, so that "took context from the top of the capture" is distinguishable from
        // "took context from the right line plus its neighbour", which is the intended behaviour.
        var page = Page(
            Line(0, 40, "totally", "unrelated", "heading"),
            Line(1, 200, "some", "middle", "text"),
            Line(2, 360, "the", "quiet", "library", "closed"));

        var match = WordMatcher.FindBestWord(page, cursorX: 60, cursorY: 368);

        Assert.Equal("quiet", match.Target);
        Assert.Contains("library", match.Context);
        Assert.DoesNotContain("unrelated", match.Context);
    }

    [Fact]
    public void AdjacentProseLineIsIncludedInContext()
    {
        var page = Page(
            Line(0, 200, "she", "opened", "the", "door"),
            Line(1, 240, "and", "the", "library", "was", "quiet"));

        var match = WordMatcher.FindBestWord(page, cursorX: 100, cursorY: 248);

        Assert.Contains("opened", match.Context);
    }

    [Fact]
    public void InterfaceChromeIsKeptOutOfContext()
    {
        var page = Page(
            Line(0, 200, "UID:", "700036681"),
            Line(1, 240, "the", "library", "was", "quiet"));

        var match = WordMatcher.FindBestWord(page, cursorX: 60, cursorY: 248);

        Assert.DoesNotContain("700036681", match.Context);
    }

    [Fact]
    public void ContextIsStillCorrectWhenSplitWordMergingFires()
    {
        // "envo" + "ys" merges into "envoys". The merge produces a synthesized word that is not in
        // the page, and the visual-line lookup matches by identity - so this used to find nothing,
        // fall back to visual line 0, and take context from the top of the capture instead.
        var page = Page(
            Line(0, 40, "totally", "unrelated", "heading"),
            Line(1, 200, "some", "middle", "text"),
            Line(2, 360, "the", "envo", "ys", "arrived"));

        // Cursor right on the boundary between "envo" and "ys".
        var match = WordMatcher.FindBestWord(page, cursorX: 88, cursorY: 368);

        Assert.Equal("envoys", match.Target);
        Assert.Contains("arrived", match.Context);
        Assert.DoesNotContain("unrelated", match.Context);
    }

    // ── Interface-label filtering ──

    [Theory]
    [InlineData("UID: 700036681")]
    [InlineData("Lv.90")]
    [InlineData("Paimon:")]
    [InlineData("12:34")]
    public void InterfaceChromeIsRecognized(string line) =>
        Assert.True(WordMatcher.IsInterfaceLabel(line));

    [Theory]
    [InlineData("Are you coming with us?")]
    [InlineData("Idea after idea came to nothing.")]
    [InlineData("Identity is a strange thing.")]
    [InlineData("Defend the gate at all costs.")]
    [InlineData("Around the corner it waited.")]
    public void ProseStartingWithALabelPrefixIsNotChrome(string line)
    {
        // "Are"/"Around" start with "AR", "Idea"/"Identity" with "ID", "Defend" with "DEF".
        // A bare StartsWith threw all of these away.
        Assert.False(WordMatcher.IsInterfaceLabel(line));
    }

    // ── Correction ──

    [Fact]
    public void AWordReadCorrectlyIsLeftAlone()
    {
        var page = Page(Line(0, 100, "the", "library", "closed"));

        var match = WordMatcher.FindBestWord(page, cursorX: 60, cursorY: 108);

        Assert.Equal("library", match.Target);
    }

    [Fact]
    public void NonEnglishTextIsNotRunThroughTheEnglishCorrector()
    {
        var page = Page(Line(0, 100, "図書館", "は", "静か"));

        var match = WordMatcher.FindBestWord(page, cursorX: 15, cursorY: 108, languageTag: "ja");

        Assert.Equal("図書館", match.Target);
    }

    [Fact]
    public void PunctuationIsStrippedFromTheLookedUpWord()
    {
        var page = Page(Line(0, 100, "the", "library,", "closed"));

        var match = WordMatcher.FindBestWord(page, cursorX: 60, cursorY: 108);

        Assert.Equal("library", match.Target);
    }

    [Fact]
    public void EmptyPageYieldsNoMatch() =>
        Assert.False(WordMatcher.FindBestWord(RecognizedText.FromWords([]), 10, 10).Found);
}
