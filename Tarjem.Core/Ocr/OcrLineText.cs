using Tarjem.Core.Language;

namespace Tarjem.Core.Ocr;

/// <summary>
/// Turns one row of OCR words, given in the left-to-right order they sit on screen, into the text a
/// reader would actually read there.
///
/// Windows OCR lists a line's words by position, and its line text is just those words joined with
/// spaces. That is right for English and wrong for two families of languages this app supports:
/// <list type="bullet">
/// <item>Arabic, Persian and Urdu are read right to left, so a left-to-right join reverses the
/// sentence - "مرحبا بك في اللعبة" came back as "اللعبة في بك مرحبا", and that reversed word salad is
/// what was sent to the translator.</item>
/// <item>Chinese and Japanese are written without spaces, and the engine reports every character as
/// its own word, so "設定を保存しますか" came back as "設 定 を 保 存 し ま す か".</item>
/// </list>
/// Each word's own letters are already in reading order; only the order of, and gaps between, the
/// words needs fixing.
/// </summary>
public static class OcrLineText
{
    public static string Compose(IEnumerable<string> wordsLeftToRight)
    {
        var words = wordsLeftToRight.Where(w => !string.IsNullOrEmpty(w)).ToList();
        if (words.Count == 0) return string.Empty;

        if (IsRightToLeft(words))
            words = ToReadingOrder(words);

        var builder = new System.Text.StringBuilder(words[0]);
        for (var i = 1; i < words.Count; i++)
        {
            if (!(IsUnspaced(words[i - 1][^1]) && IsUnspaced(words[i][0])))
                builder.Append(' ');
            builder.Append(words[i]);
        }
        return builder.ToString();
    }

    /// <summary>A line is right-to-left when most of its letters are in a right-to-left script. An
    /// English line quoting one Arabic word stays left-to-right.</summary>
    private static bool IsRightToLeft(IEnumerable<string> words)
    {
        int rtl = 0, ltr = 0;
        foreach (var c in words.SelectMany(w => w))
        {
            if (IsRtlLetter(c)) rtl++;
            else if (char.IsLetter(c)) ltr++;
        }
        return rtl > 0 && rtl >= ltr;
    }

    /// <summary>
    /// Reverses the word order of a right-to-left line - except within runs of left-to-right words
    /// embedded in it. "Press Start" quoted inside an Arabic sentence is still displayed, and so
    /// still read, left to right; only its position relative to the Arabic words is flipped.
    /// </summary>
    private static List<string> ToReadingOrder(List<string> words)
    {
        var runs = new List<List<string>>();
        foreach (var word in words)
        {
            var isRtl = word.Any(IsRtlLetter);
            if (!isRtl && runs.Count > 0 && !runs[^1][0].Any(IsRtlLetter))
                runs[^1].Add(word);
            else
                runs.Add([word]);
        }

        runs.Reverse();
        return runs.SelectMany(r => r).ToList();
    }

    private static bool IsRtlLetter(char c) =>
        LanguageGuesser.ScriptOfChar(c) == LanguageGuesser.Script.Arabic || c is >= '֐' and <= '׿';

    /// <summary>Han, Hiragana, Katakana and their full-width punctuation - the characters written
    /// with no space between them. Korean is deliberately absent: it spaces its words.</summary>
    internal static bool IsUnspaced(char c) =>
        c is >= '　' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '＀' and <= '￯';
}
