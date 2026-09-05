using Tarjem.Core.Translation;
using Xunit;

namespace Tarjem.Core.Tests;

public class TranslationHighlighterTests
{
    private static string Highlighted(string sentence, string word, int markedStart = -1, int markedLength = 0)
    {
        var (start, length) = TranslationHighlighter.Locate(sentence, word, markedStart, markedLength);
        return start < 0 ? "" : sentence.Substring(start, length);
    }

    [Fact]
    public void UsesProviderMarkedSpanWhenPresent()
    {
        const string sentence = "احتوت المكتبة القديمة على أسرار.";
        var start = sentence.IndexOf("المكتبة", StringComparison.Ordinal);

        Assert.Equal("المكتبة", Highlighted(sentence, "شيء آخر", start, "المكتبة".Length));
    }

    [Fact]
    public void WidensProviderSpanToWholeWords()
    {
        // A span cutting through the middle of a word would break Arabic letter joining.
        const string sentence = "احتوت المكتبة القديمة على أسرار.";
        var start = sentence.IndexOf("المكتبة", StringComparison.Ordinal);

        Assert.Equal("المكتبة", Highlighted(sentence, "المكتبة", start + 2, 3));
    }

    [Fact]
    public void FindsExactWord()
    {
        Assert.Equal("كتاب", Highlighted("هذا كتاب جميل", "كتاب"));
    }

    [Fact]
    public void FindsWordCarryingTheDefiniteArticle()
    {
        // The standalone translation of "library" comes back bare; the sentence uses "the library".
        Assert.Equal("المكتبة", Highlighted("احتوت المكتبة على أسرار", "مكتبة"));
    }

    [Fact]
    public void FindsWordCarryingAttachedPreposition()
    {
        Assert.Equal("بالكتاب", Highlighted("احتفظ بالكتاب في حقيبته", "كتاب"));
    }

    [Fact]
    public void IgnoresDiacriticsOnEitherSide()
    {
        Assert.Equal("كِتَاب", Highlighted("هذا كِتَاب جميل", "كتاب"));
        Assert.Equal("كتاب", Highlighted("هذا كتاب جميل", "كِتَاب"));
    }

    [Fact]
    public void TreatsAlefAndHamzaVariantsAsTheSameLetter()
    {
        Assert.Equal("أسرار", Highlighted("تحمل أسرار كثيرة", "اسرار"));
    }

    [Fact]
    public void MatchesTaaMarbutaVariant()
    {
        Assert.Equal("مدينه", Highlighted("زرنا مدينه قديمة", "مدينة"));
    }

    [Fact]
    public void StripsPunctuationAroundTheMatch()
    {
        Assert.Equal("الكتاب،", Highlighted("قرأت الكتاب، ثم نمت", "كتاب"));
    }

    [Fact]
    public void FindsInflectedLatinWord()
    {
        Assert.Equal("bibliotecas", Highlighted("Las bibliotecas antiguas guardaban secretos", "biblioteca"));
    }

    [Fact]
    public void IgnoresLatinAccentDifferences()
    {
        Assert.Equal("Ancienne", Highlighted("La bibliotheque Ancienne", "ancienné"));
    }

    [Fact]
    public void ReturnsNoMatchWhenNothingIsCloseEnough()
    {
        var (start, _) = TranslationHighlighter.Locate("الطقس جميل اليوم", "حاسوب");
        Assert.Equal(-1, start);
    }

    [Fact]
    public void ReturnsNoMatchForEmptyInput()
    {
        Assert.Equal(-1, TranslationHighlighter.Locate("", "كتاب").Start);
        Assert.Equal(-1, TranslationHighlighter.Locate("جملة كاملة", "").Start);
        Assert.Equal(-1, TranslationHighlighter.Locate("جملة كاملة", "   ").Start);
    }

    [Fact]
    public void IgnoresOutOfRangeProviderSpan()
    {
        // A stale/garbled marker must not throw or produce a bogus span - it falls through
        // to the search instead.
        Assert.Equal("كتاب", Highlighted("هذا كتاب جميل", "كتاب", 500, 20));
    }

    [Fact]
    public void PicksTheClosestTokenNotMerelyTheFirstOne()
    {
        // "الكتابة" (writing) shares a root with "كتاب" (book) but the exact word is present.
        Assert.Equal("كتاب", Highlighted("الكتابة عن كتاب صعبة", "كتاب"));
    }

    [Fact]
    public void SpanIsAlwaysWithinTheSentence()
    {
        const string sentence = "احتوت المكتبة القديمة على أسرار لا تحصى";
        foreach (var word in new[] { "مكتبة", "اسرار", "قديم", "unrelated" })
        {
            var (start, length) = TranslationHighlighter.Locate(sentence, word);
            if (start < 0) continue;

            Assert.InRange(start, 0, sentence.Length - 1);
            Assert.InRange(start + length, 0, sentence.Length);
        }
    }
}
