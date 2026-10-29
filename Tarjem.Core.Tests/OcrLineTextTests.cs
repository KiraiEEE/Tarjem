using Tarjem.Core.Ocr;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// Covers rebuilding a line's text from OCR's word boxes. The inputs are in the left-to-right
/// order Windows OCR actually reports them in - the Arabic and Japanese cases are taken from a
/// real run of the engines.
/// </summary>
public class OcrLineTextTests
{
    [Fact]
    public void LeavesLeftToRightTextAlone() =>
        Assert.Equal("Press any key", OcrLineText.Compose(new[] { "Press", "any", "key" }));

    [Fact]
    public void PutsArabicBackIntoReadingOrder() =>
        // On screen, left to right: اللعبة في بك مرحبا. Read right to left: "welcome to the game".
        Assert.Equal("مرحبا بك في اللعبة", OcrLineText.Compose(new[] { "اللعبة", "في", "بك", "مرحبا" }));

    [Fact]
    public void KeepsANumberInItsPlaceInAnArabicSentence() =>
        // "You have 5 messages".
        Assert.Equal("لديك 5 رسائل", OcrLineText.Compose(new[] { "رسائل", "5", "لديك" }));

    [Fact]
    public void KeepsEmbeddedEnglishReadingLeftToRight() =>
        // "Press Start to continue", with the button name left in English.
        Assert.Equal("اضغط Press Start للمتابعة", OcrLineText.Compose(new[] { "للمتابعة", "Press", "Start", "اضغط" }));

    [Fact]
    public void AnEnglishLineQuotingOneArabicWordStaysLeftToRight() =>
        Assert.Equal("The word مرحبا means hello", OcrLineText.Compose(new[] { "The", "word", "مرحبا", "means", "hello" }));

    [Fact]
    public void JoinsJapaneseWithoutSpaces() =>
        Assert.Equal("設定を保存しますか", OcrLineText.Compose(new[] { "設", "定", "を", "保", "存", "し", "ま", "す", "か" }));

    [Fact]
    public void KeepsTheSpaceBetweenJapaneseAndLatin() =>
        Assert.Equal("HP を回復", OcrLineText.Compose(new[] { "HP", "を", "回", "復" }));

    [Fact]
    public void KoreanKeepsItsSpaces() =>
        Assert.Equal("게임 시작", OcrLineText.Compose(new[] { "게임", "시작" }));

    [Fact]
    public void FeedsTheWordMatchersContextSentence()
    {
        // The Alt+Q context is built from the same word boxes; it must read in the same order.
        var words = new[] { "اللعبة", "في", "بك", "مرحبا" }
            .Select((w, i) => new RecognizedWord(w, new TextRect(i * 60, 0, 50, 16), 0, i))
            .ToList();

        var match = WordMatcher.FindBestWord(RecognizedText.FromWords(words), 205, 8, "ar");

        Assert.Equal("مرحبا", match.Target);
        Assert.Equal("مرحبا بك في اللعبة", match.Context);
    }
}
