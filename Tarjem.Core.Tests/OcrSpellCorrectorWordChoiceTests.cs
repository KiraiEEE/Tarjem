using Tarjem.Core.Language;
using Tarjem.Core.Ocr;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// The corrector's worst failure mode: replacing a word that was read perfectly well with a
/// different, real word.
///
/// It is the worst because it is invisible. A garbled token that stays garbled is obviously
/// unhelpful; "governorate" quietly becoming "governor" produces a real headword, a confident
/// definition and a translation of something the reader never pointed at, with nothing on screen
/// to suggest anything went wrong.
///
/// The corrector alone cannot settle these, and this file no longer pretends it can. It answers
/// "is this a real word?" from a 47,000-word frequency list that is confidently wrong about every
/// freely-derived form English builds - so what it produces is a <em>proposal</em>, and the
/// dictionary decides. What is tested here is that the proposal stays a proposal: the reading OCR
/// actually produced survives alongside it, all the way to the service that can check it.
/// </summary>
public class OcrSpellCorrectorWordChoiceTests
{
    /// <summary>One line of recognized words, laid out left to right at 10px per character.</summary>
    private static RecognizedText Line(string text)
    {
        var words = new List<RecognizedWord>();
        double x = 0;
        var index = 0;

        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var width = token.Length * 10;
            words.Add(new RecognizedWord(token, new TextRect(x, 0, width, 20), 0, index++));
            x += width + 10;
        }

        return RecognizedText.FromWords(words);
    }

    /// <summary>
    /// The reported case. The corrector still proposes "governor" - it has no way not to - but the
    /// reading is carried forward so the dictionary can overrule it, which it will, because
    /// "governorate" has an entry and every dictionary in the chain knows it.
    /// </summary>
    [Fact]
    public void KeepsWhatOcrReadAlongsideTheProposedCorrection()
    {
        var match = WordMatcher.FindBestWord(Line("which governorate r u in"), cursorX: 130, cursorY: 10);

        Assert.Equal("governorate", match.Raw);
        Assert.True(match.WasCorrected, "the corrector should have proposed something different");
        Assert.NotEqual(match.Raw, match.Target);
    }

    /// <summary>A word the list does know is never second-guessed, so there is nothing to
    /// arbitrate and no extra request to pay for.</summary>
    [Fact]
    public void DoesNotArbitrateAWordTheListAlreadyKnows()
    {
        var match = WordMatcher.FindBestWord(Line("the modern world"), cursorX: 55, cursorY: 10);

        Assert.Equal("modern", match.Target);
        Assert.False(match.WasCorrected);
    }

    /// <summary>Misreadings must still be repaired - the reading is carried, not preferred.</summary>
    [Theory]
    [InlineData("rnodern", "modern")]
    [InlineData("governnent", "government")]
    [InlineData("tirne", "time")]
    [InlineData("sornething", "something")]
    [InlineData("hte", "the")]
    public void StillProposesRepairsForGenuineMisreadings(string token, string expected)
    {
        Assert.Equal(expected, OcrSpellCorrector.Correct(token));
    }

    /// <summary>
    /// A correction may not change the word's length much. Unlike a word list this is a property of
    /// the failure being modelled: OCR substitutes look-alike glyphs, it does not cleanly delete a
    /// word's last three characters. It costs nothing at runtime and it generalizes to any
    /// language, so it stays even though the dictionary now has the final say.
    /// </summary>
    [Fact]
    public void WillNotTruncateAWordIntoAShorterOne()
    {
        Assert.Equal(1, OcrSpellCorrector.MaxLengthDifferenceFor(5));
        Assert.Equal(2, OcrSpellCorrector.MaxLengthDifferenceFor(11));

        // Exactly the shape of the reported bug: distance 3, limit 3, three characters shorter.
        Assert.Equal(3, OcrSpellCorrector.Distance("governorate", "governor"));
        Assert.Equal(3.0, OcrSpellCorrector.MaxDistanceFor("governorate".Length));
    }

    /// <summary>
    /// Letters OCR rendered as digits. This is a glyph-confusion table, not a word list - the same
    /// kind of thing as the rn/m and cl/d pairs the distance function already models - and it has
    /// to run before normalization, which strips digits and so turned "wor1d" into the real,
    /// listed, entirely wrong word "word" before anything downstream could object.
    /// </summary>
    [Theory]
    [InlineData("wor1d", "world")]
    [InlineData("l1ght", "light")]
    [InlineData("c0uld", "could")]
    [InlineData("b00k", "book")]
    [InlineData("5tone", "stone")]
    [InlineData("1ove", "love")]
    public void ReadsDigitsBackAsTheLettersTheyWere(string token, string expected)
    {
        Assert.Equal(expected, OcrSpellCorrector.Correct(token));
    }

    /// <summary>Real numbers, ordinals and versioned names must survive untouched - they are the
    /// reason the digit rule is restricted rather than applied everywhere.</summary>
    [Theory]
    [InlineData("3d")]
    [InlineData("2nd")]
    [InlineData("1st")]
    [InlineData("8th")]
    [InlineData("9am")]
    [InlineData("covid19")]
    [InlineData("version2")]
    [InlineData("h20")]
    [InlineData("1080p")]
    [InlineData("2026")]
    [InlineData("10")]
    public void LeavesGenuineDigitsAlone(string token)
    {
        Assert.Null(OcrSpellCorrector.Correct(token));
    }
}
