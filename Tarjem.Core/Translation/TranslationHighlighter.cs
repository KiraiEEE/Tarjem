using System.Globalization;
using System.Text;

namespace Tarjem.Core.Translation;

/// <summary>
/// Finds the span inside a translated sentence that corresponds to the looked-up word, so the
/// popup can highlight it.
///
/// Gemini marks the span itself, but every other provider translates the word and the sentence
/// as two independent requests, and the standalone word almost never appears verbatim in the
/// sentence: the target language attaches an article or preposition to it ("كتاب" -> "الكتاب",
/// "بالكتاب"), inflects it for case/gender/number, or simply picks a different synonym in
/// context. A plain IndexOf therefore failed most of the time and nothing got highlighted.
///
/// So the search widens in stages, each one accepting less exact evidence than the last, and
/// stops at the first stage that finds something.
/// </summary>
public static class TranslationHighlighter
{
    /// <summary>Below this similarity a token is not considered the same word - better to
    /// highlight nothing than to confidently highlight the wrong word.</summary>
    private const double MinimumSimilarity = 0.62;

    /// <summary>
    /// Locates <paramref name="word"/> inside <paramref name="sentence"/>.
    /// <paramref name="markedStart"/>/<paramref name="markedLength"/> are the provider-supplied
    /// span (Gemini's), used as-is when valid. Returns (-1, 0) when there is no defensible match.
    /// </summary>
    public static (int Start, int Length) Locate(string sentence, string word, int markedStart = -1, int markedLength = 0)
    {
        if (string.IsNullOrEmpty(sentence))
            return (-1, 0);

        if (markedStart >= 0 && markedLength > 0 && markedStart + markedLength <= sentence.Length)
            return SnapToWordBoundaries(sentence, markedStart, markedLength);

        word = word.Trim();
        if (word.Length == 0)
            return (-1, 0);

        var normalizedWord = Normalize(word);

        // A multi-word translation can't match a single token, so those go straight to the
        // substring passes. For a single word the token pass runs first: a raw IndexOf would
        // happily match inside a longer, unrelated word ("كتاب" sits inside "الكتابة"), and
        // snapping that to word boundaries then highlights the wrong word entirely.
        var isPhrase = ContainsWhitespace(word);

        if (isPhrase)
        {
            var substringHit = FindSubstring(sentence, word, normalizedWord);
            if (substringHit.Start >= 0) return substringHit;
        }

        var tokenHit = BestMatchingToken(sentence, normalizedWord);
        if (tokenHit.Start >= 0) return tokenHit;

        return isPhrase ? (-1, 0) : FindSubstring(sentence, word, normalizedWord);
    }

    /// <summary>Verbatim match first, then a match on the normalized forms (Arabic diacritics
    /// and alef/hamza variants, Latin accents, casing) mapped back to real indices.</summary>
    private static (int Start, int Length) FindSubstring(string sentence, string word, string normalizedWord)
    {
        var exact = sentence.IndexOf(word, StringComparison.Ordinal);
        if (exact >= 0)
            return SnapToWordBoundaries(sentence, exact, word.Length);

        if (normalizedWord.Length == 0)
            return (-1, 0);

        var (normalizedSentence, indexMap) = NormalizeWithMap(sentence);
        var hit = normalizedSentence.IndexOf(normalizedWord, StringComparison.Ordinal);
        if (hit < 0 || hit >= indexMap.Count)
            return (-1, 0);

        var start = indexMap[hit];
        var endIndex = hit + normalizedWord.Length - 1;
        var end = endIndex < indexMap.Count ? indexMap[endIndex] : sentence.Length - 1;
        return SnapToWordBoundaries(sentence, start, end - start + 1);
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c)) return true;
        }
        return false;
    }

    /// <summary>Expands a span outwards to the nearest whitespace on each side. Runs are shaped
    /// independently when rendered, so a span that cuts through the middle of an Arabic word
    /// breaks its letter joining - every span this class returns sits on word boundaries.</summary>
    public static (int Start, int Length) SnapToWordBoundaries(string text, int start, int length)
    {
        if (start < 0 || length <= 0 || start + length > text.Length)
            return (start, length);

        var end = start + length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return (start, end - start);
    }

    private static (int Start, int Length) BestMatchingToken(string sentence, string normalizedWord)
    {
        if (normalizedWord.Length == 0)
            return (-1, 0);

        var wordStem = StripAffixes(normalizedWord);

        var bestStart = -1;
        var bestLength = 0;
        var bestScore = 0.0;

        foreach (var (start, length) in Tokenize(sentence))
        {
            var token = Normalize(sentence.Substring(start, length));
            if (token.Length == 0) continue;

            var score = Score(token, normalizedWord, wordStem);
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = start;
                bestLength = length;
            }
        }

        return bestScore >= MinimumSimilarity ? (bestStart, bestLength) : (-1, 0);
    }

    private static double Score(string token, string normalizedWord, string wordStem)
    {
        if (token == normalizedWord) return 1.0;

        var tokenStem = StripAffixes(token);
        if (tokenStem.Length >= 3 && tokenStem == wordStem) return 0.98;

        // One is contained in the other: "الكتاب" vs "كتاب", "libraries" vs "library".
        if (wordStem.Length >= 3 && tokenStem.Length >= 3 &&
            (tokenStem.Contains(wordStem, StringComparison.Ordinal) || wordStem.Contains(tokenStem, StringComparison.Ordinal)))
        {
            var shorter = Math.Min(tokenStem.Length, wordStem.Length);
            var longer = Math.Max(tokenStem.Length, wordStem.Length);
            return 0.75 + 0.2 * shorter / longer;
        }

        return Dice(tokenStem, wordStem);
    }

    /// <summary>Sørensen-Dice coefficient over character bigrams - cheap, order-aware enough
    /// for morphological variants, and doesn't need a stemmer per language.</summary>
    private static double Dice(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2)
            return a == b ? 1.0 : 0.0;

        var pairs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < a.Length - 1; i++)
        {
            var bigram = a.Substring(i, 2);
            pairs[bigram] = pairs.TryGetValue(bigram, out var n) ? n + 1 : 1;
        }

        var matches = 0;
        for (int i = 0; i < b.Length - 1; i++)
        {
            var bigram = b.Substring(i, 2);
            if (pairs.TryGetValue(bigram, out var n) && n > 0)
            {
                pairs[bigram] = n - 1;
                matches++;
            }
        }

        return 2.0 * matches / (a.Length - 1 + b.Length - 1);
    }

    private static IEnumerable<(int Start, int Length)> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > start) yield return (start, i - start);
        }
    }

    /// <summary>Drops Arabic clitics that attach to the front of a word (the definite article
    /// and the single-letter conjunction/preposition prefixes) plus the commonest suffixes, so
    /// "وبالكتاب" and "كتاب" reduce to the same stem. Only applied while enough of the word
    /// survives to still be meaningful.</summary>
    private static string StripAffixes(string word)
    {
        var result = word;

        foreach (var prefix in ArabicPrefixes)
        {
            if (result.Length - prefix.Length >= 3 && result.StartsWith(prefix, StringComparison.Ordinal))
            {
                result = result[prefix.Length..];
                break;
            }
        }

        foreach (var suffix in Suffixes)
        {
            if (result.Length - suffix.Length >= 3 && result.EndsWith(suffix, StringComparison.Ordinal))
            {
                result = result[..^suffix.Length];
                break;
            }
        }

        return result;
    }

    // Longest first: "وبال" has to be tried before "و".
    private static readonly string[] ArabicPrefixes =
    {
        "وبال", "فبال", "وكال", "بال", "كال", "فال", "وال", "لل", "ال",
        "و", "ف", "ب", "ك", "ل",
    };

    // Longest first, same as the prefixes: the loop below stops at its first match, so listing
    // "s" before "es"/"ed"/"ing" made those three unreachable and stemmed "libraries" to
    // "librarie" instead of "librari".
    private static readonly string[] Suffixes =
    {
        "هما", "هم", "هن", "ها", "كم", "كن", "نا", "ين", "ون", "ات", "ان", "ه", "ي",
        "ing", "es", "ed", "s",
    };

    /// <summary>Case-folds, strips combining marks (Latin accents and Arabic tashkeel alike),
    /// unifies the Arabic letter forms that carry no semantic difference, and drops
    /// punctuation.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        AppendNormalized(text, builder, null);
        return builder.ToString();
    }

    /// <summary>Same as <see cref="Normalize"/>, but also returns, for each character of the
    /// normalized string, the index it came from in the original - so a match found in
    /// normalized space can be reported as a span of the original text.</summary>
    private static (string Normalized, List<int> IndexMap) NormalizeWithMap(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        AppendNormalized(text, builder, map);
        return (builder.ToString(), map);
    }

    private static void AppendNormalized(string text, StringBuilder builder, List<int>? map)
    {
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (IsIgnorable(c))
                continue;

            var folded = Fold(c);
            if (folded == '\0')
                continue;

            builder.Append(folded);
            map?.Add(i);
        }
    }

    private static bool IsIgnorable(char c)
    {
        // Arabic tashkeel, tatweel and the zero-width joiners, plus anything that is neither a
        // letter nor a digit (punctuation, quotes, brackets).
        if (c is >= 'ً' and <= 'ْ') return true;
        if (c is 'ـ' or 'ٰ' or '‌' or '‍' or '‎' or '‏') return true;
        if (char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) return true;
        return !char.IsLetterOrDigit(c);
    }

    private static char Fold(char c) => c switch
    {
        'أ' or 'إ' or 'آ' or 'ٱ' or 'ا' => 'ا',
        'ى' => 'ي',
        'ئ' => 'ي',
        'ؤ' => 'و',
        'ة' => 'ه',
        _ => char.ToLowerInvariant(StripLatinAccent(c)),
    };

    private static char StripLatinAccent(char c)
    {
        if (c < 'À') return c;

        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        foreach (var ch in decomposed)
        {
            if (char.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                return ch;
        }
        return c;
    }
}
