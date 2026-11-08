using Tarjem.Core.Ocr;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// The two pieces Alt+W's in-place mode is built on: which on-screen lines form each block (and
/// where it is), and what colours that block was drawn in.
/// </summary>
public class InPlaceLayoutTests
{
    // ── Blocks ──

    private static RecognizedText Lines(params (string Text, double Y)[] lines)
    {
        var words = new List<RecognizedWord>();
        for (var li = 0; li < lines.Length; li++)
        {
            double x = 20;
            var parts = lines[li].Text.Split(' ');
            for (var wi = 0; wi < parts.Length; wi++)
            {
                var width = parts[wi].Length * 8;
                words.Add(new RecognizedWord(parts[wi], new TextRect(x, lines[li].Y, width, 16), li, wi));
                x += width + 6;
            }
        }
        return RecognizedText.FromWords(words);
    }

    [Fact]
    public void AWrappedSentenceIsOneBlockSpanningBothRows()
    {
        var text = Lines(
            ("Settings", 0),
            ("You can change the language of the whole", 40),
            ("interface from this page at any time.", 62));

        var blocks = TextReflow.LogicalBlocks(text);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("Settings", blocks[0].Text);
        Assert.Equal([0], blocks[0].LineIndices);

        Assert.Equal([1, 2], blocks[1].LineIndices);
        Assert.Equal(40, blocks[1].Bounds.Y);
        Assert.Equal(78, blocks[1].Bounds.Bottom);
        Assert.Equal(16, blocks[1].RowHeight);
    }

    [Fact]
    public void LogicalLinesMatchTheBlocks()
    {
        var text = Lines(("New Game", 0), ("Continue", 30), ("Options", 60));
        Assert.Equal(TextReflow.LogicalBlocks(text).Select(b => b.Text), TextReflow.LogicalLines(text));
    }

    [Fact]
    public void BlankEngineLinesKeepTheirIndicesOutOfTheBlocks()
    {
        var words = new List<RecognizedWord>
        {
            new("Start", new TextRect(0, 0, 40, 16), 0, 0),
            new(" ", new TextRect(0, 30, 4, 16), 1, 0),
            new("Quit", new TextRect(0, 60, 32, 16), 2, 0),
        };

        var blocks = TextReflow.LogicalBlocks(RecognizedText.FromWords(words));

        Assert.Equal(2, blocks.Count);
        Assert.Equal([0], blocks[0].LineIndices);
        Assert.Equal([2], blocks[1].LineIndices);
    }

    // ── Colours ──

    private const int W = 60, H = 30;

    /// <summary>A capture filled with <paramref name="background"/>, with a block of "ink"
    /// pixels inside the box from (20,10) to (40,20).</summary>
    private static byte[] Canvas(Rgb background, Rgb ink, double inkShare = 0.3)
    {
        var pixels = new byte[W * H * 4];
        var step = (int)Math.Round(1 / inkShare);
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var isInk = x is >= 20 and < 40 && y is >= 10 and < 20 && (x + y) % step == 0;
            var c = isInk ? ink : background;
            var i = (y * W + x) * 4;
            pixels[i] = c.B;
            pixels[i + 1] = c.G;
            pixels[i + 2] = c.R;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static readonly TextRect Box = new(20, 10, 20, 10);

    [Fact]
    public void WhiteTextOnABlueButtonStaysWhiteOnBlue()
    {
        var blue = new Rgb(0x1E, 0x5A, 0xC8);
        var palette = BlockColors.Sample(Canvas(blue, new Rgb(255, 255, 255)), W, H, W * 4, Box, ring: 3);

        Assert.Equal(blue, palette.Background);
        Assert.True(palette.Foreground.R > 240 && palette.Foreground.G > 240 && palette.Foreground.B > 240);
    }

    [Fact]
    public void DarkTextOnCreamStaysDarkOnCream()
    {
        var cream = new Rgb(0xF4, 0xEC, 0xD8);
        var ink = new Rgb(0x30, 0x24, 0x18);
        var palette = BlockColors.Sample(Canvas(cream, ink), W, H, W * 4, Box, ring: 3);

        Assert.Equal(cream, palette.Background);
        Assert.Equal(ink, palette.Foreground);
    }

    [Fact]
    public void TextTooCloseToItsBackgroundIsRedrawnReadably()
    {
        // Mid-grey "ink" on a slightly different grey: the sample is faithful but unreadable.
        var grey = new Rgb(0x80, 0x80, 0x80);
        var palette = BlockColors.Sample(Canvas(grey, new Rgb(0xB4, 0xB4, 0xB4)), W, H, W * 4, Box, ring: 3);

        Assert.True(Rgb.Contrast(palette.Foreground, palette.Background) >= BlockColors.MinimumContrast);
    }

    [Fact]
    public void ABoxWithNoRingStillGetsAPalette()
    {
        var black = new Rgb(0, 0, 0);
        var whole = new TextRect(0, 0, W, H);
        var palette = BlockColors.Sample(Canvas(black, new Rgb(250, 250, 250), 0.25), W, H, W * 4, whole, ring: 4);

        Assert.Equal(black, palette.Background);
        Assert.True(Rgb.Contrast(palette.Foreground, palette.Background) >= BlockColors.MinimumContrast);
    }
}
