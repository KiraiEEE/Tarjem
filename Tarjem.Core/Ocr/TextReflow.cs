using System.Text;

namespace Tarjem.Core.Ocr;

/// <summary>
/// Turns OCR's visual lines back into the text's real lines before it is translated.
///
/// OCR reports one line per row of pixels, so a single sentence that happened to wrap across three
/// rows on screen arrives as three separate lines. Sent that way, every translator - Google as much
/// as the AI providers - translates each row as its own fragment, and the result keeps the source
/// language's clause order because each fragment was cut at an arbitrary point in it. That is one
/// of the main reasons region translations read as literal: for a verb-first language like Arabic,
/// "translate the first half of this English sentence, then the second half" can only come out in
/// English order.
///
/// Genuine breaks - menu items, list entries, separate lines of dialogue, paragraphs - have to
/// survive, because the overlay draws the translation back over that same layout. So a break is
/// only removed on positive evidence that the line continues: no sentence-ending punctuation, and
/// a continuation signal such as the next line starting in lower case.
/// </summary>
public static class TextReflow
{
    /// <summary>Punctuation that ends a line on purpose. The colon is included because it ends
    /// labels and speaker names ("Health:", "GUARD:"), which are never continued mid-thought.</summary>
    private static readonly HashSet<char> Terminals = new()
    {
        '.', '!', '?', '…', ':', '。', '！', '？', '؟', '۔', '｡',
    };

    /// <summary>Closing marks that can sit after a terminal ("Stop!" / "(see below.)").</summary>
    private static readonly HashSet<char> Closers = new()
    {
        '"', '\'', '”', '’', ')', ']', '}', '»', '」', '』', '）',
    };

    /// <summary>A line ending on one of these is almost certainly mid-sentence.</summary>
    private static readonly HashSet<char> Continuers = new() { ',', '،', '、', '-', '–', '—', '/', '&', '+' };

    private static readonly HashSet<char> Bullets = new() { '•', '●', '▪', '‣', '◦', '·', '-', '–', '—', '*', '►', '▶', '>' };

    /// <summary>English function words that cannot end a sentence. A line ending on one continues
    /// even when the next line starts with a capital ("…talked to / John about it").</summary>
    private static readonly HashSet<string> DanglingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "of", "and", "or", "but", "nor", "in", "on", "at", "for", "with",
        "from", "by", "as", "into", "onto", "than", "that", "this", "these", "those", "your", "my",
        "his", "her", "their", "our", "its", "is", "are", "was", "were", "be", "been", "if", "when",
        "while", "because", "about", "over", "under", "between", "through", "not", "no", "very",
    };

    /// <summary>The recognized text with soft wraps joined, one entry per real line.</summary>
    public static IReadOnlyList<string> LogicalLines(RecognizedText text) =>
        LogicalBlocks(text).Select(b => b.Text).ToList();

    /// <summary>
    /// The same grouping as <see cref="LogicalLines"/>, keeping where each real line sits on
    /// screen: which engine lines it was rejoined from, the box around them, and the height of one
    /// of its rows. The region overlay draws each block's translation back into that box, so a
    /// heading stays a heading and a menu stays a menu instead of everything collapsing into one
    /// paragraph.
    /// </summary>
    public static IReadOnlyList<TextBlock> LogicalBlocks(RecognizedText text)
    {
        var indexed = text.Lines
            .Select((line, index) => (Line: line, Index: index))
            .Where(l => !string.IsNullOrWhiteSpace(l.Line.Text))
            .ToList();
        if (indexed.Count == 0) return Array.Empty<TextBlock>();

        var lines = indexed.Select(l => l.Line).ToList();
        var maxWidth = lines.Max(Width);
        var lineHeight = MedianHeight(lines);

        var result = new List<TextBlock>();
        var current = new StringBuilder(lines[0].Text.Trim());
        var members = new List<int> { 0 };

        for (var i = 1; i < lines.Count; i++)
        {
            var next = lines[i].Text.Trim();

            if (Continues(lines[i - 1], lines[i], maxWidth, lineHeight))
            {
                Append(current, next);
                members.Add(i);
            }
            else
            {
                result.Add(Block(current.ToString(), members, indexed));
                current.Clear().Append(next);
                members = new List<int> { i };
            }
        }

        result.Add(Block(current.ToString(), members, indexed));
        return result;
    }

    private static TextBlock Block(string text, List<int> members, List<(RecognizedLine Line, int Index)> indexed)
    {
        var words = members.SelectMany(m => indexed[m].Line.Words).ToList();
        var bounds = words.Count == 0
            ? new TextRect(0, 0, 0, 0)
            : new TextRect(
                words.Min(w => w.Bounds.X),
                words.Min(w => w.Bounds.Y),
                words.Max(w => w.Bounds.Right) - words.Min(w => w.Bounds.X),
                words.Max(w => w.Bounds.Bottom) - words.Min(w => w.Bounds.Y));

        var rowHeights = members
            .Select(m => indexed[m].Line.Words)
            .Where(ws => ws.Count > 0)
            .Select(ws => ws.Max(w => w.Bounds.Bottom) - ws.Min(w => w.Bounds.Y))
            .Where(h => h > 0)
            .OrderBy(h => h)
            .ToList();

        return new TextBlock(
            text,
            members.Select(m => indexed[m].Index).ToList(),
            bounds,
            rowHeights.Count == 0 ? 0 : rowHeights[rowHeights.Count / 2]);
    }

    /// <summary>Convenience for callers that want the block as one string, real lines separated by
    /// newlines - the shape every translator is sent.</summary>
    public static string Reflow(RecognizedText text) => string.Join("\n", LogicalLines(text));

    private static bool Continues(RecognizedLine prev, RecognizedLine next, double maxWidth, double lineHeight)
    {
        var prevText = prev.Text.TrimEnd();
        var nextText = next.Text.TrimStart();
        if (prevText.Length == 0 || nextText.Length == 0) return false;

        if (EndsSentence(prevText)) return false;
        if (StartsListItem(nextText)) return false;

        // A visibly larger gap than ordinary line spacing is a paragraph break, whatever the
        // punctuation says.
        if (lineHeight > 0 && prev.Words.Count > 0 && next.Words.Count > 0)
        {
            var gap = next.Words.Min(w => w.Bounds.Y) - prev.Words.Max(w => w.Bounds.Bottom);
            if (gap > lineHeight * 0.9) return false;
        }

        var last = prevText[^1];
        if (Continuers.Contains(last)) return true;

        var first = FirstLetter(nextText);
        if (first is { } c)
        {
            if (char.IsLower(c)) return true;

            // Scripts without case (Arabic, Hebrew, CJK, Thai...) have no lower-case signal, so
            // lean on shape instead: a long line that runs nearly the full width of the block
            // stopped because it hit the edge, not because it ended. The length floor keeps a block
            // of short menu entries - where the longest one is by definition "full width" - apart.
            if (!char.IsUpper(c) && IsWide(prev, prevText, maxWidth))
                return true;
        }

        return DanglingWords.Contains(LastWord(prevText)) && IsWide(prev, prevText, maxWidth);
    }

    private static bool EndsSentence(string line)
    {
        var i = line.Length - 1;
        while (i > 0 && Closers.Contains(line[i])) i--;
        return Terminals.Contains(line[i]);
    }

    private static bool StartsListItem(string line)
    {
        if (Bullets.Contains(line[0]) && (line.Length == 1 || char.IsWhiteSpace(line[1])))
            return true;

        // "1. Start", "2) Options"
        var digits = 0;
        while (digits < line.Length && char.IsDigit(line[digits])) digits++;
        return digits is > 0 and <= 3 && digits < line.Length && line[digits] is '.' or ')';
    }

    private static bool IsWide(RecognizedLine line, string text, double maxWidth)
    {
        var width = Width(line);
        if (maxWidth <= 0 || width < maxWidth * 0.8) return false;

        // Space-separated scripts: a real wrapped line has a handful of words in it. Scripts
        // written without spaces: fall back to character count.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words >= 4 || (words == 1 && text.Length >= 12);
    }

    private static double Width(RecognizedLine line) =>
        line.Words.Count == 0 ? line.Text.Length : line.Words.Max(w => w.Bounds.Right) - line.Words.Min(w => w.Bounds.X);

    private static double MedianHeight(List<RecognizedLine> lines)
    {
        var heights = lines
            .Where(l => l.Words.Count > 0)
            .Select(l => l.Words.Max(w => w.Bounds.Bottom) - l.Words.Min(w => w.Bounds.Y))
            .Where(h => h > 0)
            .OrderBy(h => h)
            .ToList();

        return heights.Count == 0 ? 0 : heights[heights.Count / 2];
    }

    private static char? FirstLetter(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetter(c)) return c;
            if (char.IsDigit(c)) return null;
        }
        return null;
    }

    private static string LastWord(string text)
    {
        var trimmed = text.TrimEnd(',', ';', ' ');
        var space = trimmed.LastIndexOf(' ');
        return space < 0 ? trimmed : trimmed[(space + 1)..];
    }

    private static void Append(StringBuilder current, string next)
    {
        var endsWithHyphen = current.Length > 1 && current[^1] == '-' && char.IsLetter(current[^2]);

        // Chinese and Japanese are written without spaces, so a wrap inside them is joined directly;
        // a word hyphenated across the break is rejoined with its hyphen kept, since "well-known"
        // and "transla-tion" can't be told apart here and a translator copes with either.
        if (!endsWithHyphen && !(OcrLineText.IsUnspaced(current[^1]) && OcrLineText.IsUnspaced(next[0])))
            current.Append(' ');

        current.Append(next);
    }
}

/// <summary>One real line of text and where it was on screen.</summary>
/// <param name="Text">The line, with soft wraps rejoined.</param>
/// <param name="LineIndices">Indices into <see cref="RecognizedText.Lines"/> it was built from.</param>
/// <param name="Bounds">The box around every word in it, in capture coordinates.</param>
/// <param name="RowHeight">Ink height of one of its rows (median), the basis for its font size.</param>
public sealed record TextBlock(string Text, IReadOnlyList<int> LineIndices, TextRect Bounds, double RowHeight);
