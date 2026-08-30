using Tarjem.Core.Language;

namespace Tarjem.Core.Ocr;

/// <summary>What the user was pointing at, and the text around it.</summary>
/// <param name="Word">The matched word, or null when nothing was close enough to the cursor.</param>
/// <param name="Target">The word to actually look up - spell-corrected for English.</param>
/// <param name="Context">The sentence the word sits in, for context-aware translation.</param>
/// <param name="Raw">What OCR actually read, when correction changed it - otherwise empty.
///
/// Spelling correction has to decide "is this a real word?" against a 47,000-word frequency list,
/// and that list is wrong about a great many real words: English derives freely, so "governorate"
/// is absent from it while "governor" sits three edits away, and the correction looked like a
/// repair. A list can only ever be extended; the dictionary the app is about to query already
/// knows every real word, in every language it supports. So both spellings travel forward and the
/// dictionary arbitrates - if it has an entry for what OCR read, OCR was right.</param>
public sealed record WordMatch(RecognizedWord? Word, string Target, string Context, string Raw = "")
{
    public static readonly WordMatch None = new(null, string.Empty, string.Empty);
    public bool Found => Word != null && Target.Length > 0;

    /// <summary>True when correction proposed a different word, so there is something to arbitrate.</summary>
    public bool WasCorrected => Raw.Length > 0 && !Raw.Equals(Target, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turns "the cursor is at (x, y)" into "the user is asking about this word, in this sentence".
///
/// This used to live in the WPF project, wired directly to <c>Windows.Media.Ocr</c> types, which
/// meant the hardest logic in the app - the part that decides <em>which word you meant</em> - had
/// no tests at all. It works against <see cref="RecognizedText"/> now so it can be driven from
/// hand-written fixtures.
/// </summary>
public static class WordMatcher
{
    /// <summary>
    /// Finds the word under the cursor and the sentence around it.
    /// </summary>
    /// <param name="text">The recognized capture.</param>
    /// <param name="cursorX">Cursor X within the capture bitmap.</param>
    /// <param name="cursorY">Cursor Y within the capture bitmap.</param>
    /// <param name="languageTag">Source language. Spelling correction only runs for English -
    /// there is no equivalent word list for the others, so correcting them would just be
    /// comparing foreign text against English words and "fixing" it wrong.</param>
    public static WordMatch FindBestWord(RecognizedText text, double cursorX, double cursorY, string languageTag = "en")
    {
        if (text.IsEmpty) return WordMatch.None;

        var hit = WordUnderCursor(text.Words, cursorX, cursorY) ?? NearestAcceptableWord(text.Words, cursorX, cursorY);
        if (hit == null) return WordMatch.None;

        // Split-word merging can produce a synthesized word that isn't in text.Words. The visual
        // line lookup below matches by identity, so it has to be given the original.
        var anchor = hit;
        var (word, raw) = MergeSplitNeighbours(text, hit, cursorX);

        var context = BuildContext(text, anchor);
        var target = CorrectIfEnglish(raw, text.LineTextAt(word.LineIndex), word, cursorX, languageTag);

        // The uncorrected reading rides along so the dictionary can overrule the correction.
        var asRead = LettersOnly(raw);

        return new WordMatch(word, target, context.Trim(), asRead);
    }

    // ── Which word ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A word whose box actually contains the cursor. The hit margin scales with the word's height
    /// so small game fonts (12-14px) still register while large ones don't grab a neighbour. Among
    /// overlapping candidates (tight kerning) the one whose horizontal centre is nearest wins -
    /// smallest-area used to win instead, which systematically preferred short words like "a"/"I"
    /// whenever they overlapped a longer neighbour.
    /// </summary>
    private static RecognizedWord? WordUnderCursor(IReadOnlyList<RecognizedWord> words, double cursorX, double cursorY) =>
        words
            .Where(w =>
            {
                var r = w.Bounds;
                var margin = Math.Max(r.Height * 0.25, 4);
                return cursorX >= r.X - margin && cursorX <= r.Right + margin &&
                       cursorY >= r.Y - margin && cursorY <= r.Bottom + margin;
            })
            .OrderBy(w => Math.Abs(w.Bounds.CenterX - cursorX))
            .FirstOrDefault();

    /// <summary>
    /// No box contains the cursor. This used to still pick <em>some</em> word - the globally
    /// nearest, however far away - so clicking empty space confidently "translated" a word
    /// hundreds of pixels off with no indication anything was wrong. Only accept the nearest word
    /// if it is actually close, scaled to its own glyph height.
    /// </summary>
    private static RecognizedWord? NearestAcceptableWord(IReadOnlyList<RecognizedWord> words, double cursorX, double cursorY)
    {
        RecognizedWord? nearest = null;
        var nearestDistance = double.MaxValue;

        foreach (var word in words)
        {
            var dx = word.Bounds.CenterX - cursorX;
            var dy = word.Bounds.CenterY - cursorY;
            var distance = Math.Sqrt(dx * dx + dy * dy);

            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = word;
        }

        if (nearest == null) return null;

        var maxDistance = Math.Max(nearest.Bounds.Height, 12) * 1.5;
        return nearestDistance > maxDistance ? null : nearest;
    }

    /// <summary>
    /// OCR sometimes splits one word into two adjacent tokens ("Envo" + "ys"). When the matched
    /// token joins with an immediate neighbour to form a real word that neither half is on its
    /// own, and the cursor is near their shared edge, prefer the combined word.
    /// </summary>
    private static (RecognizedWord Word, string Raw) MergeSplitNeighbours(RecognizedText text, RecognizedWord hit, double cursorX)
    {
        var line = text.Lines[hit.LineIndex].Words;

        foreach (var offset in new[] { 1, -1 })
        {
            var neighbourIndex = hit.WordIndex + offset;
            if (neighbourIndex < 0 || neighbourIndex >= line.Count) continue;

            var neighbour = line[neighbourIndex];
            var combined = LettersOnly(offset > 0 ? hit.Text + neighbour.Text : neighbour.Text + hit.Text);

            if (!EnglishWordList.IsRealWord(combined) || LooksAlreadyCorrect(hit.Text)) continue;

            // The edge between the two tokens - only merge if that is where the user pointed.
            var boundary = offset > 0 ? hit.Bounds.Right : hit.Bounds.X;
            if (Math.Abs(cursorX - boundary) >= Math.Max(hit.Bounds.Width, 10) * 0.8) continue;

            var left = offset > 0 ? hit : neighbour;
            var right = offset > 0 ? neighbour : hit;

            var merged = new RecognizedWord(
                combined,
                new TextRect(
                    left.Bounds.X,
                    Math.Min(left.Bounds.Y, right.Bounds.Y),
                    right.Bounds.Right - left.Bounds.X,
                    Math.Max(left.Bounds.Height, right.Bounds.Height)),
                hit.LineIndex,
                Math.Min(hit.WordIndex, neighbourIndex));

            return (merged, combined);
        }

        return (hit, hit.Text);
    }

    // ── Context ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sentence around the word, built from visual lines rather than engine lines, plus the
    /// neighbouring lines when they look like prose.
    /// </summary>
    private static string BuildContext(RecognizedText text, RecognizedWord anchor)
    {
        var (index, lines) = BuildVisualLines(text.Words, anchor);
        if (index < 0 || index >= lines.Count)
            return text.LineTextAt(anchor.LineIndex);

        var context = lines[index];

        if (index > 0 && !IsInterfaceLabel(lines[index - 1]))
            context = lines[index - 1] + " " + context;

        if (index < lines.Count - 1 && !IsInterfaceLabel(lines[index + 1]))
            context += " " + lines[index + 1];

        return context;
    }

    /// <summary>
    /// Groups words by vertical proximity to recover the lines a reader sees, independent of how
    /// the engine chose to break them up. Returns the visual line containing
    /// <paramref name="target"/> plus every line's text.
    /// </summary>
    private static (int Index, List<string> Lines) BuildVisualLines(IReadOnlyList<RecognizedWord> words, RecognizedWord target)
    {
        if (words.Count == 0) return (0, [""]);

        var sorted = words.OrderBy(w => w.Bounds.CenterY).ThenBy(w => w.Bounds.X).ToList();

        var averageHeight = sorted.Average(w => w.Bounds.Height);
        var threshold = Math.Max(averageHeight * 0.4, 6);

        var grouped = new List<List<RecognizedWord>>();
        var current = new List<RecognizedWord> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].Bounds.CenterY - sorted[i - 1].Bounds.CenterY) <= threshold)
            {
                current.Add(sorted[i]);
                continue;
            }

            grouped.Add(current);
            current = [sorted[i]];
        }
        grouped.Add(current);

        var index = grouped.FindIndex(line => line.Contains(target));

        var texts = grouped
            .Select(line => string.Join(" ", line.OrderBy(w => w.Bounds.X).Select(w => w.Text)))
            .ToList();

        return (index < 0 ? 0 : index, texts);
    }

    /// <summary>
    /// Whether a line is interface chrome rather than prose - a UID, a level readout, a speaker
    /// label - so it can be kept out of the sentence sent for translation.
    ///
    /// <b>These heuristics are tuned for game HUDs</b> (the labels below are Genshin-style stat
    /// readouts) and are a best-effort filter, not a general one. They are deliberately
    /// conservative: wrongly dropping a line costs a little context, wrongly keeping one pollutes
    /// the translation with numbers.
    /// </summary>
    public static bool IsInterfaceLabel(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;

        var trimmed = line.Trim();

        // Pure numbers with common separators - UIDs, coordinates, timers.
        if (trimmed.All(c => char.IsDigit(c) || c is ':' or '.' or ',' or '-' or ' '))
            return true;

        if (trimmed.Length <= 2)
            return true;

        // A speaker label rather than what they said: "Paimon:", "Moon Envoy:".
        if (trimmed.EndsWith(':') && trimmed.Length <= 30)
            return true;

        foreach (var label in InterfaceLabels)
            if (StartsWithLabel(trimmed, label))
                return true;

        // Mostly digits with a few letters: "Lv.90", "700036681".
        if (trimmed.Count(char.IsDigit) > trimmed.Length * 0.6 && trimmed.Length <= 15)
            return true;

        return false;
    }

    private static readonly string[] InterfaceLabels = ["UID", "ID", "Lv", "AR", "WL", "HP", "ATK", "DEF"];

    /// <summary>
    /// True when the line opens with the label as a standalone token - the label alone, or
    /// followed by a non-letter ("UID: 700", "Lv.90", "HP 240"). A bare StartsWith threw away
    /// every line beginning "Are", "Idea", "Identity" or "Defend".
    /// </summary>
    private static bool StartsWithLabel(string line, string label) =>
        line.StartsWith(label, StringComparison.OrdinalIgnoreCase) &&
        (line.Length == label.Length || !char.IsLetter(line[label.Length]));

    // ── Correction ──────────────────────────────────────────────────────────────────────

    private static string CorrectIfEnglish(string raw, string fullLine, RecognizedWord word, double cursorX, string languageTag)
    {
        if (LooksAlreadyCorrect(raw))
            return LettersOnly(raw) is { Length: > 0 } clean ? clean : raw;

        if (!languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return new string(raw.Where(c => char.IsLetter(c) || char.IsWhiteSpace(c)).ToArray()).Trim();

        return Correct(raw, fullLine, word, cursorX);
    }

    /// <summary>
    /// Repairs the token OCR produced. Order matters: merged words are split first, because a
    /// merged token matches itself perfectly in the line text and would otherwise be confirmed
    /// rather than fixed.
    /// </summary>
    private static string Correct(string raw, string fullLine, RecognizedWord word, double cursorX)
    {
        if (word.Bounds.Width > 0)
        {
            var relative = (cursorX - word.Bounds.X) / word.Bounds.Width;
            if (OcrSpellCorrector.SplitMergedAt(raw, relative) is { } half)
                return half;
        }

        var clean = LettersOnly(raw);
        if (clean.Length == 0) return raw;
        if (EnglishWordList.IsRealWord(clean)) return clean;

        var lineWords = fullLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (BestLineWord(clean, lineWords) is { } fromLine)
            return fromLine;

        // The token is a fragment of a longer word on the same line (OCR split it). Keep the
        // fragment rather than "correcting" it into something unrelated - this is what preserves
        // proper nouns and game terms that aren't in any dictionary.
        foreach (var candidate in lineWords.Select(LettersOnly))
        {
            if (candidate.Length <= clean.Length) continue;
            if (candidate.StartsWith(clean, StringComparison.OrdinalIgnoreCase) ||
                candidate.EndsWith(clean, StringComparison.OrdinalIgnoreCase))
                return clean;
        }

        return MergeWithAdjacentWord(clean, lineWords, word.WordIndex)
               ?? OcrSpellCorrector.Correct(clean, lineWords)
               ?? clean;
    }

    /// <summary>
    /// The closest word on the same line, if one is close enough <em>and</em> unambiguous. A tie
    /// means two different words are equally plausible, and guessing between them produces exactly
    /// the confidently-wrong result this whole pipeline exists to avoid.
    /// </summary>
    private static string? BestLineWord(string token, string[] lineWords)
    {
        var limit = OcrSpellCorrector.MaxDistanceFor(token.Length);

        string? best = null;
        var bestDistance = double.MaxValue;
        var tied = false;

        foreach (var candidate in lineWords.Select(LettersOnly))
        {
            if (candidate.Length == 0) continue;

            var distance = OcrSpellCorrector.Distance(token, candidate);

            if (distance < bestDistance - 0.0001)
            {
                bestDistance = distance;
                best = candidate;
                tied = false;
            }
            else if (Math.Abs(distance - bestDistance) <= 0.0001 &&
                     !string.Equals(candidate, best, StringComparison.OrdinalIgnoreCase))
            {
                tied = true;
            }
        }

        if (best == null) return null;

        // An exact match is always accepted; anything else has to be both close and unambiguous.
        return bestDistance <= 0.0001 || (!tied && bestDistance <= limit) ? best : null;
    }

    /// <summary>
    /// OCR sometimes splits one word across two tokens ("trans" + "lation"). Only the token
    /// immediately before or after is considered - an earlier version accepted any line word
    /// merely <em>containing</em> the token, so a stray "a" happily merged into "cat".
    /// </summary>
    private static string? MergeWithAdjacentWord(string token, string[] lineWords, int wordIndex)
    {
        if (token.Length == 0 || wordIndex < 0 || wordIndex >= lineWords.Length) return null;

        foreach (var neighbourIndex in new[] { wordIndex - 1, wordIndex + 1 })
        {
            if (neighbourIndex < 0 || neighbourIndex >= lineWords.Length) continue;

            var neighbour = LettersOnly(lineWords[neighbourIndex]);
            if (neighbour.Length == 0 || EnglishWordList.IsRealWord(neighbour)) continue;

            var combined = neighbourIndex < wordIndex ? neighbour + token : token + neighbour;
            if (EnglishWordList.IsRealWord(combined)) return combined;
        }

        return null;
    }

    /// <summary>True when there is nothing to correct: the token is a real word, or it carries no
    /// letters at all (punctuation, digits) and so has no spelling to fix.</summary>
    private static bool LooksAlreadyCorrect(string word)
    {
        var clean = EnglishWordList.Normalize(word);
        return clean.Length == 0 || EnglishWordList.IsRealWord(clean);
    }

    private static string LettersOnly(string value) => new(value.Where(char.IsLetter).ToArray());
}
