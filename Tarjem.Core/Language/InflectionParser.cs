using System.Text.RegularExpressions;

namespace Tarjem.Core.Language;

/// <summary>An inflected word and the dictionary word it points at.</summary>
/// <param name="BaseWord">The lemma - "revelation" for "revelations".</param>
/// <param name="Label">How the two are related, as the dictionary phrased it ("plural").</param>
public sealed record InflectedForm(string BaseWord, string Label);

/// <summary>
/// Recognizes a dictionary entry that is a cross-reference rather than a definition.
///
/// Wiktionary-derived sources - which includes Tarjem's default dictionary - give inflected words
/// their own entries whose entire content is a pointer: look up "revelations" and the answer is
/// "plural of revelation". That is correct lexicography and a useless popup. The reader hovered a
/// word in a sentence because they did not know what it meant, and telling them it is the plural
/// of a word they also do not know answers a question nobody asked.
///
/// So these are detected and followed: the popup shows the base word's real definition under the
/// word that was actually hovered.
/// </summary>
public static class InflectionParser
{
    /// <summary>
    /// Words that make "&lt;something&gt; of &lt;word&gt;" a grammatical cross-reference rather
    /// than the opening of a real definition.
    ///
    /// "form" and "spelling" are deliberately absent on their own - "A form of energy", "Form of
    /// address" are ordinary definitions that would otherwise be followed to the wrong word. They
    /// appear only in the qualified pairs below.
    /// </summary>
    private static readonly string[] InflectionWords =
    [
        "plural", "singular", "participle", "gerund", "infinitive", "comparative", "superlative",
        "inflection", "inflected", "conjugation", "declension", "preterite", "past", "present",
        "tense", "third-person", "first-person", "second-person", "feminine", "masculine",
        "neuter", "genitive", "dative", "accusative", "nominative", "vocative", "oblique",
        "imperative", "subjunctive", "diminutive", "augmentative", "misspelling", "abbreviation",
        "initialism", "acronym", "contraction",
        // Qualified "form"/"spelling" only.
        "alternative form", "alternate form", "alternative spelling", "alternate spelling",
        "obsolete form", "obsolete spelling", "archaic form", "archaic spelling",
        "dated form", "dated spelling", "eye dialect", "informal form", "standard form",
        "nonstandard form", "common misspelling",
    ];

    /// <summary>
    /// The whole definition must be the cross-reference: a label, "of", and a single word. A
    /// definition that carries on past the word it names ("A form of energy that can be converted
    /// into heat") is a real definition that happens to contain "of", and following it would send
    /// the reader somewhere the dictionary never pointed.
    /// </summary>
    private static readonly Regex FormOf = new(
        @"^(?<label>[\p{L}\p{M}\s\-,]{1,60}?)\s+of\s+(?<base>[\p{L}\p{M}'’\-]+)\s*[.;]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Leading qualifiers Wiktionary puts in front of the reference: "(informal) plural
    /// of ...", "[archaic] plural of ...".</summary>
    private static readonly Regex LeadingQualifier = new(
        @"^\s*(\([^)]*\)|\[[^\]]*\])\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Articles no grammatical cross-reference ever starts with, and every "a form of X"
    /// style real definition does. This one test is what keeps the pattern from eating ordinary
    /// definitions.</summary>
    private static readonly string[] LeadingArticles = ["a ", "an ", "the "];

    /// <summary>The base word this definition points at, or null if it is a real definition.</summary>
    public static InflectedForm? Parse(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return null;

        var text = definition.Trim();

        // Strip any number of leading qualifiers, then re-test.
        for (var previous = ""; previous != text;)
        {
            previous = text;
            text = LeadingQualifier.Replace(text, "").TrimStart();
        }

        if (text.Length == 0) return null;

        foreach (var article in LeadingArticles)
            if (text.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                return null;

        var match = FormOf.Match(text);
        if (!match.Success) return null;

        var label = match.Groups["label"].Value.Trim();
        if (!IsGrammaticalLabel(label)) return null;

        var baseWord = match.Groups["base"].Value.Trim().Trim('\'', '’', '-');
        return baseWord.Length < 2 ? null : new InflectedForm(baseWord, label);
    }

    private static bool IsGrammaticalLabel(string label)
    {
        foreach (var word in InflectionWords)
            if (label.Contains(word, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Splits a source's answer into the definitions that actually define something and the ones
    /// that only point elsewhere.
    ///
    /// Order matters more than it looks: "mice" and "running" have both kinds, and the pointer
    /// comes first. Taking the first three definitions - which is all the popup has room for -
    /// therefore threw away every real sense the source had.
    /// </summary>
    public static (List<string> Real, List<string> References) Partition(IEnumerable<string> definitions)
    {
        var real = new List<string>();
        var references = new List<string>();

        foreach (var definition in definitions)
            (Parse(definition) == null ? real : references).Add(definition);

        return (real, references);
    }

    /// <summary>
    /// The word to look up instead, when <paramref name="definitions"/> contains nothing but
    /// pointers. Null when there is a real definition among them (nothing to follow), when they
    /// disagree about where to go, or when the answer points back at the word we already asked
    /// about - which is how a redirect loop starts.
    /// </summary>
    public static InflectedForm? RedirectFor(IReadOnlyList<string> definitions, string word)
    {
        if (definitions.Count == 0) return null;

        InflectedForm? first = null;

        foreach (var definition in definitions)
        {
            var form = Parse(definition);
            if (form == null) return null;

            first ??= form;

            // "past tense of X" and "plural of Y" in one entry is a homograph, not an inflection
            // with one answer; sending the reader to whichever came first would be a coin toss.
            if (!string.Equals(form.BaseWord, first.BaseWord, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return first != null && !string.Equals(first.BaseWord, word.Trim(), StringComparison.OrdinalIgnoreCase)
            ? first
            : null;
    }
}
