using Tarjem.Core.Ocr;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// Covers the step that decides which of OCR's line breaks are real. Getting it wrong in one
/// direction sends a sentence to the translator in pieces (literal output); getting it wrong in
/// the other runs a menu's entries together into one nonsense sentence. Both are exercised.
/// </summary>
public class TextReflowTests
{
    private const double LineHeight = 16;
    private const double Leading = 6;

    /// <summary>Lays out lines the way OCR reports them: one per row, left-aligned, words spaced
    /// evenly. <paramref name="gapBefore"/> adds extra space above a line to mimic a paragraph.</summary>
    private static RecognizedText Block(params (string Text, double GapBefore)[] lines)
    {
        var words = new List<RecognizedWord>();
        double y = 0;

        for (var li = 0; li < lines.Length; li++)
        {
            y += lines[li].GapBefore;
            double x = 0;
            var parts = lines[li].Text.Split(' ');
            for (var wi = 0; wi < parts.Length; wi++)
            {
                var width = parts[wi].Length * 8;
                words.Add(new RecognizedWord(parts[wi], new TextRect(x, y, width, LineHeight), li, wi));
                x += width + 6;
            }
            y += LineHeight + Leading;
        }

        return RecognizedText.FromWords(words);
    }

    private static RecognizedText Block(params string[] lines) =>
        Block(lines.Select(l => (l, 0.0)).ToArray());

    [Fact]
    public void JoinsASentenceThatWrappedAcrossLines()
    {
        var text = Block(
            "You should never have come back here after",
            "everything that happened in the old mill,",
            "but I suppose you had your reasons.");

        Assert.Equal(
            new[] { "You should never have come back here after everything that happened in the old mill, but I suppose you had your reasons." },
            TextReflow.LogicalLines(text));
    }

    [Fact]
    public void KeepsMenuEntriesOnSeparateLines()
    {
        var text = Block("New Game", "Load Game", "Options", "Quit to Desktop");

        Assert.Equal(new[] { "New Game", "Load Game", "Options", "Quit to Desktop" }, TextReflow.LogicalLines(text));
    }

    [Fact]
    public void KeepsSeparateSentencesOfDialogueApart()
    {
        var text = Block("Stay behind me.", "Something is watching us!", "Did you hear that?");

        Assert.Equal(3, TextReflow.LogicalLines(text).Count);
    }

    [Fact]
    public void KeepsListItemsApartEvenWhenTheyContinueInLowerCase()
    {
        var text = Block("Requirements for this quest are the following", "- level ten or higher", "- a sharpened sword");

        var lines = TextReflow.LogicalLines(text);

        Assert.Equal(3, lines.Count);
        Assert.Equal("- a sharpened sword", lines[2]);
    }

    [Fact]
    public void AParagraphGapIsABreakEvenMidSentence()
    {
        var text = Block(
            ("The storm had finally passed over the valley", 0),
            ("and the villagers came out to see the damage", LineHeight * 1.5));

        Assert.Equal(2, TextReflow.LogicalLines(text).Count);
    }

    [Fact]
    public void ALineEndingOnAFunctionWordContinuesIntoACapital()
    {
        var text = Block("I was going to go and talk to", "Marcus about the missing shipment.");

        Assert.Single(TextReflow.LogicalLines(text));
    }

    [Fact]
    public void LabelsEndingInAColonStayOnTheirOwnLine()
    {
        var text = Block("GUARD:", "halt, who goes there");

        Assert.Equal(2, TextReflow.LogicalLines(text).Count);
    }

    [Fact]
    public void RejoinsAWordHyphenatedAcrossTheBreakWithoutASpace()
    {
        var text = Block("This is a very well-", "known story among the people of the north.");

        Assert.Equal("This is a very well-known story among the people of the north.", Assert.Single(TextReflow.LogicalLines(text)));
    }

    [Fact]
    public void ASingleLineIsReturnedUnchanged()
    {
        Assert.Equal(new[] { "Press any key" }, TextReflow.LogicalLines(Block("Press any key")));
    }

    [Fact]
    public void ReflowSeparatesRealLinesWithNewlines()
    {
        var text = Block("Continue", "Settings");

        Assert.Equal("Continue\nSettings", TextReflow.Reflow(text));
    }
}
