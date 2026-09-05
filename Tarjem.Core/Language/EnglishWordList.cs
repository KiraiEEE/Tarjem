using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace Tarjem.Core.Language;

/// <summary>
/// A frequency-ordered list of ~47,000 English words, embedded gzipped (~177 KB) and expanded
/// once on first use.
///
/// It answers two questions that look unrelated but come from the same data. "Is this a real
/// word?" is what stops OCR correction from mangling text it read correctly - the previous
/// oracle was a curated 1,494-word definitions file, so the overwhelming majority of ordinary
/// English looked unknown to it and got "corrected" into whatever happened to be nearby. And
/// "how hard is this word?" falls straight out of its position in the list, which is what lets
/// the keyless dictionaries show a CEFR level without an AI request.
/// </summary>
public static class EnglishWordList
{
    private static readonly Lazy<Data> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record Data(Dictionary<string, int> RankByWord, string[] ByRank);

    /// <summary>Number of words loaded. Zero means the embedded resource is missing, in which
    /// case every caller degrades to "don't know" rather than to "nothing is a word".</summary>
    public static int Count => Loaded.Value.ByRank.Length;

    /// <summary>True if this is a real English word. Case- and punctuation-insensitive.</summary>
    public static bool Contains(string? word) => Rank(word) >= 0;

    /// <summary>
    /// Apostrophe-stripped contractions. OCR in stylized game fonts frequently loses the
    /// apostrophe, so "doesn't" arrives as "doesnt" - a form no frequency list carries, but one
    /// that has to count as a real word or the corrector will try to repair it into something else.
    /// </summary>
    private static readonly HashSet<string> StrippedContractions = new(StringComparer.OrdinalIgnoreCase)
    {
        "doesnt", "dont", "cant", "wont", "isnt", "arent", "wasnt", "werent",
        "hasnt", "havent", "hadnt", "wouldnt", "couldnt", "shouldnt", "didnt",
        "aint", "thats", "whats", "lets", "hes", "shes", "theyre", "youre",
        "weve", "ive", "youve", "theyve", "ill", "hell", "shell", "theyll",
        "youll", "hed", "shed", "theyd", "youd", "wed", "im", "id",
    };

    /// <summary>True for anything that should be treated as correctly-read English - the frequency
    /// list plus the contraction forms above. This, rather than <see cref="Contains"/>, is what
    /// spelling correction should test against, since "dont" is a real reading of real text.</summary>
    public static bool IsRealWord(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        var key = Normalize(word);
        return key.Length > 0 && (Contains(key) || StrippedContractions.Contains(key));
    }

    /// <summary>Position in the frequency list, 0 being the single most common English word, or
    /// -1 if unknown.</summary>
    public static int Rank(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return -1;
        var key = Normalize(word);
        return key.Length > 0 && Loaded.Value.RankByWord.TryGetValue(key, out var rank) ? rank : -1;
    }

    /// <summary>Words in frequency order, most common first - the candidate pool for spelling
    /// correction. Ordered so a tie between two equally-close candidates can be broken toward the
    /// one a person is actually more likely to have been reading.</summary>
    public static IReadOnlyList<string> ByFrequency => Loaded.Value.ByRank;

    /// <summary>
    /// CEFR band estimated from how common the word is. Word frequency is the strongest single
    /// predictor of the level a word is taught at, so the bands below track the standard vocabulary
    /// sizes: roughly the first 750 words for A1, the first 3,000 by B1, and so on. It is an
    /// estimate and labelled as one in the UI - an unknown word returns an empty string rather
    /// than guessing C2, because absence from a 47k list means "rare or not English at all", and
    /// those two deserve different answers.
    /// </summary>
    public static string CefrFor(string? word)
    {
        var rank = Rank(word);
        return rank switch
        {
            < 0 => "",
            < 750 => "A1",
            < 1500 => "A2",
            < 3000 => "B1",
            < 6000 => "B2",
            < 12000 => "C1",
            _ => "C2",
        };
    }

    /// <summary>Lowercases and drops anything that isn't a letter or an internal apostrophe, so
    /// "Heart," and "heart" hit the same entry.</summary>
    public static string Normalize(string word)
    {
        Span<char> buffer = word.Length <= 64 ? stackalloc char[word.Length] : new char[word.Length];
        var length = 0;

        foreach (var c in word)
        {
            if (char.IsLetter(c)) buffer[length++] = char.ToLowerInvariant(c);
            else if (c == '\'' && length > 0) buffer[length++] = c;
        }

        while (length > 0 && buffer[length - 1] == '\'') length--;
        return new string(buffer[..length]);
    }

    private static Data Load()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Tarjem.Core.data.wordlist-en.txt.gz");
            if (stream == null)
                return new Data(new Dictionary<string, int>(), []);

            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);

            var byRank = new List<string>(48_000);
            var rankByWord = new Dictionary<string, int>(48_000, StringComparer.Ordinal);

            while (reader.ReadLine() is { } line)
            {
                var word = line.Trim();
                if (word.Length == 0 || rankByWord.ContainsKey(word)) continue;
                rankByWord[word] = byRank.Count;
                byRank.Add(word);
            }

            return new Data(rankByWord, byRank.ToArray());
        }
        catch
        {
            // A missing or corrupt resource must not take the app down - OCR correction simply
            // stops second-guessing what it read, which is the safe direction to fail in.
            return new Data(new Dictionary<string, int>(), []);
        }
    }
}
