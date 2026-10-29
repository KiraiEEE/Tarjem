using Tarjem.Core.Language;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// Covers source-language auto-detection. The label it produces is sent to the translator as the
/// source language, so a wrong one doesn't crash anything - it just quietly makes every
/// translation worse. Both halves are tested: picking the right script from competing OCR
/// readings, and picking the right language among those that share one.
/// </summary>
public class LanguageGuesserTests
{
    private static readonly string[] Latin = { "en", "es", "fr", "de", "it", "pt", "tr", "nl", "pl" };
    private static readonly string[] ArabicScript = { "ar", "fa", "ur" };
    private static readonly string[] Cjk = { "ja", "zh-CN", "zh-TW" };

    [Theory]
    [InlineData("en", LanguageGuesser.Script.Latin)]
    [InlineData("pl", LanguageGuesser.Script.Latin)]
    [InlineData("ar", LanguageGuesser.Script.Arabic)]
    [InlineData("ur", LanguageGuesser.Script.Arabic)]
    [InlineData("ru", LanguageGuesser.Script.Cyrillic)]
    [InlineData("zh-TW", LanguageGuesser.Script.Cjk)]
    [InlineData("ja", LanguageGuesser.Script.Cjk)]
    [InlineData("ko", LanguageGuesser.Script.Hangul)]
    [InlineData("hi", LanguageGuesser.Script.Devanagari)]
    public void MapsEachLanguageToTheScriptItsEngineReads(string tag, LanguageGuesser.Script expected) =>
        Assert.Equal(expected, LanguageGuesser.ScriptOf(tag));

    [Fact]
    public void ArabicReadingBeatsALatinEngineGuessingAtTheSamePixels()
    {
        // What the two engines plausibly make of an Arabic line: the right one reads Arabic, the
        // wrong one forces Latin shapes onto it.
        var arabic = LanguageGuesser.ReadingScore("مرحبا بك في اللعبة", "ar");
        var latinGarbage = LanguageGuesser.ReadingScore("rjl ,lJl ui, ci", "en");

        Assert.True(arabic > latinGarbage);
    }

    [Fact]
    public void ShortCorrectJapaneseBeatsALongerLatinMisreading()
    {
        // The old "longest text wins" rule picked the Latin reading here: it is longer in
        // characters, but one kana or kanji carries what takes several Latin letters.
        var japanese = LanguageGuesser.ReadingScore("ゲームを始める", "ja");
        var latinGarbage = LanguageGuesser.ReadingScore("tf-Ad%Jsbb3 IJ", "en");

        Assert.True(japanese > latinGarbage);
    }

    [Fact]
    public void AnEngineReadingTheWrongScriptScoresBelowZero()
    {
        // The Japanese engine reads Latin text too; when it does, it must lose to the Latin engine.
        Assert.True(LanguageGuesser.ReadingScore("Press any key to continue", "ja") < 0);
        Assert.True(LanguageGuesser.ReadingScore("Press any key to continue", "en") > 0);
    }

    [Theory]
    [InlineData("Press any key to continue the game", "en")]
    [InlineData("Nie mogę tego zrobić, jest już za późno.", "pl")]
    [InlineData("Wczytaj grę", "pl")]
    [InlineData("Je ne peux pas faire ça maintenant, c'est trop tard.", "fr")]
    [InlineData("¿Dónde está la llave del castillo?", "es")]
    [InlineData("Ich kann das jetzt nicht machen, es ist zu spät.", "de")]
    [InlineData("Não posso fazer isso agora, já é tarde.", "pt")]
    [InlineData("Bu oyunu çok seviyorum ama daha iyi olabilir.", "tr")]
    [InlineData("Ik kan dat niet doen, het is te laat.", "nl")]
    [InlineData("Non posso farlo adesso, è troppo tardi.", "it")]
    public void TellsLatinLanguagesApart(string text, string expected) =>
        Assert.Equal(expected, LanguageGuesser.GuessWithinScript(text, Latin, "en"));

    [Fact]
    public void PolishTextWinsEvenWhenTheUserPrefersEnglish() =>
        Assert.Equal("pl", LanguageGuesser.GuessWithinScript("Zapisz grę i wyjdź do menu", new[] { "en", "fr", "pl" }, "en"));

    [Theory]
    [InlineData("Opcje")]       // Polish, but nothing in it says so
    [InlineData("OK")]
    [InlineData("12:45")]
    [InlineData("")]
    public void NoEvidenceKeepsTheFallback(string text)
    {
        Assert.Equal("pl", LanguageGuesser.GuessWithinScript(text, Latin, "pl"));
        Assert.Equal("en", LanguageGuesser.GuessWithinScript(text, Latin, "en"));
    }

    [Fact]
    public void ACommonEnglishWordIsEnoughWhenNothingElseCompetes() =>
        Assert.Equal("en", LanguageGuesser.GuessWithinScript("Continue", new[] { "pl", "en" }, "pl"));

    [Fact]
    public void OnlyCandidatesThatAreInstalledCanBeChosen() =>
        // German text, but German isn't installed: the answer must be one of the candidates.
        Assert.Contains(LanguageGuesser.GuessWithinScript("Ich weiß es nicht", new[] { "en", "pl" }, "en"), new[] { "en", "pl" });

    [Theory]
    [InlineData("هذا هو الطريق إلى المدينة", "ar")]
    [InlineData("این کتاب برای من است", "fa")]
    [InlineData("یہ میری کتاب ہے اور وہ تمہاری ہے", "ur")]
    public void TellsArabicScriptLanguagesApart(string text, string expected) =>
        Assert.Equal(expected, LanguageGuesser.GuessWithinScript(text, ArabicScript, "ar"));

    [Theory]
    [InlineData("ゲームを始める", "ja")]
    [InlineData("设置 继续游戏 这是我们的", "zh-CN")]
    [InlineData("設置 繼續遊戲 這是我們的", "zh-TW")]
    public void TellsCjkLanguagesApart(string text, string expected) =>
        Assert.Equal(expected, LanguageGuesser.GuessWithinScript(text, Cjk, "ja"));

    [Fact]
    public void AllHanTextLeansChineseOverJapanese() =>
        Assert.Equal("zh-CN", LanguageGuesser.GuessWithinScript("开始游戏", new[] { "ja", "zh-CN" }, "ja"));
}
