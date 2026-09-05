namespace Tarjem.Core.Language;

/// <summary>
/// Supplies the CEFR difficulty badge for the keyless dictionaries, which - unlike the AI
/// providers - return no level of their own. Without this, choosing the fast free dictionary
/// silently cost you the level badge, so the badge only ever appeared for people spending model
/// requests on every lookup.
/// </summary>
public static class CefrEstimator
{
    /// <summary>
    /// Best estimate of a word's level. Frequency does the work (see
    /// <see cref="EnglishWordList.CefrFor"/>); the definition text is a tiebreaker for words the
    /// frequency list has never seen, where the only available signal is how the dictionary
    /// itself talks about them.
    /// </summary>
    public static string Estimate(string word, IReadOnlyList<string>? definitions = null)
    {
        var byFrequency = EnglishWordList.CefrFor(word);
        if (byFrequency.Length > 0) return byFrequency;

        // Not in a 47k frequency list at all. Dictionaries mark exactly this kind of word with
        // usage labels, and those labels are a better guide than pretending we know nothing.
        if (definitions != null)
        {
            foreach (var definition in definitions)
            {
                if (definition.Contains("archaic", StringComparison.OrdinalIgnoreCase) ||
                    definition.Contains("obsolete", StringComparison.OrdinalIgnoreCase) ||
                    definition.Contains("rare", StringComparison.OrdinalIgnoreCase))
                    return "C2";
            }
        }

        // Long words are, on average, harder ones - a weak signal, but the alternative is showing
        // no badge at all for every word outside the list.
        var letters = EnglishWordList.Normalize(word);
        return letters.Length >= 12 ? "C2" : letters.Length >= 9 ? "C1" : "";
    }
}
