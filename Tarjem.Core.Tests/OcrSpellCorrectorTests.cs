using Tarjem.Core.Language;

namespace Tarjem.Core.Tests;

public class EnglishWordListTests
{
    [Fact]
    public void EmbeddedWordList_Loads()
    {
        // Guards the resource name: a rename in the csproj turns every correction off silently,
        // because a missing list makes IsKnownWord answer "no" to the entire language.
        Assert.True(EnglishWordList.Count > 40_000, $"expected a full word list, loaded {EnglishWordList.Count}");
    }

    [Theory]
    [InlineData("the")]
    [InlineData("heart")]
    [InlineData("resilient")]
    [InlineData("ephemeral")]
    [InlineData("Meaning")]
    [InlineData("heart,")]
    public void RealWords_AreRecognized(string word) => Assert.True(EnglishWordList.Contains(word));

    [Theory]
    [InlineData("zzzqx")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonWords_AreNot(string word) => Assert.False(EnglishWordList.Contains(word));

    [Fact]
    public void CommonWords_RankAheadOfRareOnes()
    {
        Assert.True(EnglishWordList.Rank("the") < EnglishWordList.Rank("resilient"));
        Assert.True(EnglishWordList.Rank("heart") < EnglishWordList.Rank("ephemeral"));
    }

    [Theory]
    [InlineData("the", "A1")]
    [InlineData("ephemeral", "C2")]
    public void CefrLevel_TracksFrequency(string word, string expected) =>
        Assert.Equal(expected, EnglishWordList.CefrFor(word));

    [Fact]
    public void CefrLevel_IsEmpty_ForUnknownWords() =>
        Assert.Equal("", EnglishWordList.CefrFor("zzzqx"));

    [Fact]
    public void Normalize_StripsPunctuationAndCase() =>
        Assert.Equal("heart", EnglishWordList.Normalize("Heart,"));

    [Fact]
    public void Normalize_KeepsInternalApostrophes() =>
        Assert.Equal("don't", EnglishWordList.Normalize("Don't."));
}

public class OcrSpellCorrectorTests
{
    [Theory]
    [InlineData("heart")]
    [InlineData("the")]
    [InlineData("resilient")]
    public void CorrectlyReadWords_AreLeftAlone(string word) =>
        Assert.Null(OcrSpellCorrector.Correct(word));

    [Fact]
    public void GlyphConfusions_CostLessThanArbitrarySwaps()
    {
        // "rn" -> "m" is the classic OCR misread and must score closer than an unrelated edit.
        Assert.True(OcrSpellCorrector.Distance("rnodern", "modern") < OcrSpellCorrector.Distance("bodern", "modern"));
        Assert.True(OcrSpellCorrector.Distance("l1ght", "light") < OcrSpellCorrector.Distance("bught", "light"));
    }

    [Fact]
    public void Transposition_CountsAsOneEdit() =>
        Assert.Equal(1.0, OcrSpellCorrector.Distance("hte", "the"), 3);

    [Theory]
    [InlineData("rnodern", "modern")]
    [InlineData("hte", "the")]
    public void LookAlikeMisreads_AreRepaired(string misread, string expected) =>
        Assert.Equal(expected, OcrSpellCorrector.Correct(misread));

    [Fact]
    public void ShortTokens_AreNeverRewritten()
    {
        // At two or three letters almost every word is one edit from several others, so a
        // "correction" here is a coin flip presented as an answer.
        Assert.Null(OcrSpellCorrector.Correct("ot"));
        Assert.Null(OcrSpellCorrector.Correct("xq"));
    }

    [Fact]
    public void ContextWords_WinOverTheGlobalList()
    {
        // A word visible on the same line is a far better hypothesis than a similarly-close word
        // that merely exists in English.
        var corrected = OcrSpellCorrector.Correct("resiIient", new[] { "The", "team", "proved", "resilient" });
        Assert.Equal("resilient", corrected);
    }

    [Fact]
    public void UnrelatedGarbage_IsNotForcedIntoAWord()
    {
        // The failure the user reported as "it invents new words": a token nothing resembles must
        // come back untouched rather than snapping to the nearest dictionary entry.
        Assert.Null(OcrSpellCorrector.Correct("xqzptvw"));
    }

    [Fact]
    public void MergedWords_AreSplitIntoTheirParts()
    {
        var split = OcrSpellCorrector.SplitMerged("myheart");
        Assert.NotNull(split);
        Assert.Equal("my", split!.Value.Left);
        Assert.Equal("heart", split.Value.Right);
    }

    [Fact]
    public void RealWords_AreNotSplit() =>
        Assert.Null(OcrSpellCorrector.SplitMerged("together"));

    [Fact]
    public void MaxDistance_GrowsWithLength_ButStaysZeroForTinyTokens()
    {
        Assert.Equal(0, OcrSpellCorrector.MaxDistanceFor(2));
        Assert.True(OcrSpellCorrector.MaxDistanceFor(10) > OcrSpellCorrector.MaxDistanceFor(4));
    }
}

public class CefrEstimatorTests
{
    [Fact]
    public void FrequentWords_GetABeginnerLevel() => Assert.Equal("A1", CefrEstimator.Estimate("the"));

    [Fact]
    public void UnknownButLongWords_FallBackToAnAdvancedLevel() =>
        Assert.Equal("C2", CefrEstimator.Estimate("zzzqxwvutsrq"));

    [Fact]
    public void ArchaicLabels_PushToC2() =>
        Assert.Equal("C2", CefrEstimator.Estimate("zzzqx", ["An archaic term for something"]));
}

public class HtmlTextTests
{
    [Fact]
    public void StripsTagsAndDecodesEntities() =>
        Assert.Equal("a large feline & friend", HtmlText.Strip("a <i>large</i> feline &amp; friend"));

    [Fact]
    public void CollapsesWhitespaceLeftBehindByTags() =>
        Assert.Equal("one two", HtmlText.Strip("one <span>  </span>  two"));

    [Fact]
    public void EmptyInput_IsSafe() => Assert.Equal("", HtmlText.Strip(""));
}
