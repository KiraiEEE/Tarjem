namespace Tarjem.Core.Ocr;

/// <summary>
/// A rectangle in capture-bitmap coordinates. Deliberately not System.Windows.Rect: this library
/// targets plain net8.0 so that the word-matching and correction logic can be tested without a
/// desktop, a screen, or the Windows OCR engine.
/// </summary>
public readonly record struct TextRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <param name="LineIndex">Index of the engine line this word came from.</param>
/// <param name="WordIndex">Position within that line.</param>
public sealed record RecognizedWord(string Text, TextRect Bounds, int LineIndex, int WordIndex);

public sealed record RecognizedLine(string Text, IReadOnlyList<RecognizedWord> Words);

/// <summary>
/// The engine-agnostic result of recognizing a captured bitmap. <see cref="Tarjem.Services"/>
/// builds one of these from a <c>Windows.Media.Ocr.OcrResult</c>; everything downstream works
/// against this instead, which is what lets <see cref="WordMatcher"/> live here and be tested
/// against hand-written fixtures.
/// </summary>
public sealed class RecognizedText
{
    public IReadOnlyList<RecognizedLine> Lines { get; }

    /// <summary>Every word across every line, in reading order.</summary>
    public IReadOnlyList<RecognizedWord> Words { get; }

    public RecognizedText(IReadOnlyList<RecognizedLine> lines)
    {
        Lines = lines;
        Words = lines.SelectMany(l => l.Words).ToList();
    }

    /// <summary>Full text of one engine line, or empty for an out-of-range index.</summary>
    public string LineTextAt(int lineIndex) =>
        lineIndex >= 0 && lineIndex < Lines.Count ? Lines[lineIndex].Text : string.Empty;

    /// <summary>Concatenation of every line, one per row - what the region translator sends.</summary>
    public string AllText => string.Join("\n", Lines.Select(l => l.Text)).Trim();

    public bool IsEmpty => Words.Count == 0;

    /// <summary>Convenience builder for tests and for callers that already have flat word data.
    /// Words are grouped into lines by their <see cref="RecognizedWord.LineIndex"/>.</summary>
    public static RecognizedText FromWords(IEnumerable<RecognizedWord> words)
    {
        var lines = words
            .GroupBy(w => w.LineIndex)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(w => w.WordIndex).ToList();
                return new RecognizedLine(string.Join(" ", ordered.Select(w => w.Text)), ordered);
            })
            .ToList();

        return new RecognizedText(lines);
    }
}
