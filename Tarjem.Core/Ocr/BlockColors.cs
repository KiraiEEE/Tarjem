namespace Tarjem.Core.Ocr;

/// <summary>An opaque colour, independent of any UI framework.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public double Luminance
    {
        get
        {
            static double Channel(byte v)
            {
                var c = v / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
        }
    }

    public static double Contrast(Rgb a, Rgb b)
    {
        var (hi, lo) = a.Luminance >= b.Luminance ? (a.Luminance, b.Luminance) : (b.Luminance, a.Luminance);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static int DistanceSquared(Rgb a, Rgb b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    public static readonly Rgb Black = new(0x12, 0x12, 0x12);
    public static readonly Rgb White = new(0xF5, 0xF5, 0xF5);
}

/// <summary>The colours to redraw one block of text in: what it sat on, and what it was written in.</summary>
public readonly record struct BlockPalette(Rgb Background, Rgb Foreground);

/// <summary>
/// Works out what a block of screen text looked like, so its translation can be drawn back in the
/// same colours - white on a blue button stays white on blue, black on a cream page stays black on
/// cream - instead of every region becoming the same dark card.
///
/// The background is read from a thin ring just outside the text's box, where there is no ink,
/// taking the most common colour there rather than the average: a ring that clips a neighbouring
/// icon is still mostly background, and an average would come out as a muddy blend of both. The
/// text colour is then the average of the pixels inside the box that differ most from that
/// background - the glyph cores, not their anti-aliased edges.
/// </summary>
public static class BlockColors
{
    /// <summary>Below this the redrawn text is not reliably readable; the sampled colour is then
    /// replaced with black or white, whichever reads better on the background.</summary>
    public const double MinimumContrast = 3.0;

    /// <param name="bgra">32-bit BGRA pixels, as System.Drawing hands them out.</param>
    /// <param name="box">The text's box in pixel coordinates.</param>
    /// <param name="ring">How far outside the box to sample the background.</param>
    public static BlockPalette Sample(byte[] bgra, int width, int height, int stride, TextRect box, int ring)
    {
        ring = Math.Max(1, ring);

        int left = Clamp((int)Math.Floor(box.X), 0, width - 1);
        int top = Clamp((int)Math.Floor(box.Y), 0, height - 1);
        int right = Clamp((int)Math.Ceiling(box.Right), left + 1, width);
        int bottom = Clamp((int)Math.Ceiling(box.Bottom), top + 1, height);

        int outerLeft = Math.Max(0, left - ring), outerTop = Math.Max(0, top - ring);
        int outerRight = Math.Min(width, right + ring), outerBottom = Math.Min(height, bottom + ring);

        var ringPixels = new List<Rgb>();
        for (var y = outerTop; y < outerBottom; y++)
        {
            var inRows = y >= top && y < bottom;
            for (var x = outerLeft; x < outerRight; x++)
            {
                if (inRows && x >= left && x < right) continue;
                ringPixels.Add(At(bgra, stride, x, y));
            }
        }

        var inside = new List<Rgb>((right - left) * (bottom - top));
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
                inside.Add(At(bgra, stride, x, y));

        // A block pressed right up against every edge of the capture has no ring; its own most
        // common colour is then the best guess at the background, since text is mostly gaps.
        var background = Dominant(ringPixels.Count >= 8 ? ringPixels : inside);
        var ink = Ink(inside, background);
        var foreground = ink ?? Readable(background);


        if (Rgb.Contrast(foreground, background) < MinimumContrast)
            foreground = Readable(background);

        return new BlockPalette(background, foreground);
    }

    /// <summary>Black or white, whichever has more contrast against <paramref name="background"/>.</summary>
    public static Rgb Readable(Rgb background) =>
        Rgb.Contrast(Rgb.White, background) >= Rgb.Contrast(Rgb.Black, background) ? Rgb.White : Rgb.Black;

    /// <summary>The most common colour, at 4 bits per channel, averaged within that bucket so
    /// the result is the real colour rather than a bucket centre.</summary>
    private static Rgb Dominant(List<Rgb> pixels)
    {
        if (pixels.Count == 0) return new Rgb(0x20, 0x20, 0x20);

        var counts = new Dictionary<int, (int Count, long R, long G, long B)>();
        foreach (var p in pixels)
        {
            var key = (p.R >> 4) << 8 | (p.G >> 4) << 4 | (p.B >> 4);
            counts.TryGetValue(key, out var c);
            counts[key] = (c.Count + 1, c.R + p.R, c.G + p.G, c.B + p.B);
        }

        var best = counts.Values.MaxBy(c => c.Count);
        return new Rgb((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count));
    }

    /// <summary>The glyph colour: the most distant sixth of the pixels that differ visibly from the
    /// background at all. Null when nothing does - an empty box, or text the same colour as its
    /// background.</summary>
    private static Rgb? Ink(List<Rgb> inside, Rgb background)
    {
        const int VisiblyDifferent = 48 * 48;

        var different = inside
            .Select(p => (Pixel: p, Distance: Rgb.DistanceSquared(p, background)))
            .Where(p => p.Distance >= VisiblyDifferent)
            .OrderByDescending(p => p.Distance)
            .ToList();

        if (different.Count == 0) return null;

        var take = Math.Max(1, different.Count / 6);
        long r = 0, g = 0, b = 0;
        for (var i = 0; i < take; i++)
        {
            r += different[i].Pixel.R;
            g += different[i].Pixel.G;
            b += different[i].Pixel.B;
        }
        return new Rgb((byte)(r / take), (byte)(g / take), (byte)(b / take));
    }

    private static Rgb At(byte[] bgra, int stride, int x, int y)
    {
        var i = y * stride + x * 4;
        return new Rgb(bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}

/// <summary>
/// Turns what OCR measured about a block - the height of its ink - into the font size it was
/// drawn at, which is what the translation has to be drawn at to look like it belongs there.
///
/// One fixed ratio can't do this. A row's ink runs from the top of its tallest letter to the
/// bottom of its lowest: "Continue" has no descender and its ink is about 0.78 of the font size,
/// "Display settings" has p, y and g and its ink is the whole em. Measured with Windows OCR on
/// Segoe UI at 12-22 px, the two cases are stable at those two values, so the letters themselves
/// say which one applies.
/// </summary>
public static class InkMetrics
{
    private const double WithDescenders = 0.98;
    private const double WithoutDescenders = 0.77;

    /// <summary>Ideographs and kana fill most of the em square, and have no descenders to vary.</summary>
    private const double Cjk = 0.88;

    /// <summary>Scripts with no descender letters to look for here (Arabic, Hebrew, Devanagari, Thai...):
    /// a middle value, which errs by about a tenth either way.</summary>
    private const double Other = 0.9;

    private const string LatinDescenders = "gjpqyçţşęąμρφχψγηζξβ";
    private const string CyrillicDescenders = "рудфцщзДЦЩ";

    /// <summary>The font size, in the same units as <paramref name="rowInkHeight"/>.</summary>
    public static double FontSizeFromInk(double rowInkHeight, string text)
    {
        if (rowInkHeight <= 0) return 0;

        var cjk = 0;
        var otherScript = 0;
        var descender = false;
        foreach (var c in text)
        {
            if (LatinDescenders.Contains(c) || CyrillicDescenders.Contains(c) || c == ',' || c == ';') descender = true;
            else if (c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '가' and <= '힯') cjk++;
            else if (char.IsLetter(c) && c > 'ԯ' && !(c is >= 'Ḁ' and <= 'ỿ')) otherScript++;
        }

        var letters = Math.Max(1, text.Count(char.IsLetter));
        var ratio = cjk * 2 > letters ? Cjk
            : otherScript * 2 > letters ? Other
            : descender ? WithDescenders
            : WithoutDescenders;

        return rowInkHeight / ratio;
    }
}
