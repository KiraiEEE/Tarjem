using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Tarjem.Models;

namespace Tarjem.Services;

public static class ColorExtractionService
{
    private const int BucketBits = 5;
    private const int BucketCount = 1 << BucketBits; // 32
    private const int BucketMask = BucketCount - 1;

    /// <summary>
    /// Extracts a dominant accent color from the outer region of a screen capture.
    /// Returns a fallback theme if the capture is unsuitable.
    /// </summary>
    public static PopupTheme ExtractFromBitmap(Bitmap bitmap)
    {
        var buckets = new int[BucketCount * BucketCount * BucketCount];
        int totalSampled = 0;

        int w = bitmap.Width;
        int h = bitmap.Height;

        // Define center exclusion zone (where text/cursor sits)
        int cxMin = w / 2 - 100;
        int cxMax = w / 2 + 100;
        int cyMin = h / 2 - 50;
        int cyMax = h / 2 + 50;

        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int bytesPerPixel = 4;
            var scan0 = data.Scan0;

            // Sample every 4th pixel for speed
            for (int y = 0; y < h; y += 4)
            {
                var rowPtr = scan0 + y * stride;
                for (int x = 0; x < w; x += 4)
                {
                    // Skip center exclusion zone
                    if (x >= cxMin && x <= cxMax && y >= cyMin && y <= cyMax)
                        continue;

                    int offset = x * bytesPerPixel;
                    byte b = Marshal.ReadByte(rowPtr, offset);
                    byte g = Marshal.ReadByte(rowPtr, offset + 1);
                    byte r = Marshal.ReadByte(rowPtr, offset + 2);

                    int ri = r >> (8 - BucketBits);
                    int gi = g >> (8 - BucketBits);
                    int bi = b >> (8 - BucketBits);

                    buckets[(ri << (BucketBits * 2)) + (gi << BucketBits) + bi]++;
                    totalSampled++;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        if (totalSampled == 0)
            return PopupTheme.Default(true);

        // Sort buckets by count, take top candidates
        var candidates = new List<(int R, int G, int B, int Count)>();
        for (int ri = 0; ri < BucketCount; ri++)
        {
            for (int gi = 0; gi < BucketCount; gi++)
            {
                for (int bi = 0; bi < BucketCount; bi++)
                {
                    int count = buckets[(ri << (BucketBits * 2)) + (gi << BucketBits) + bi];
                    if (count > 0)
                    {
                        // Convert bucket index back to center-of-bucket value (0-255)
                        int r = (ri << (8 - BucketBits)) + (1 << (7 - BucketBits));
                        int g = (gi << (8 - BucketBits)) + (1 << (7 - BucketBits));
                        int b = (bi << (8 - BucketBits)) + (1 << (7 - BucketBits));
                        candidates.Add((r, g, b, count));
                    }
                }
            }
        }

        candidates.Sort((a, b) => b.Count.CompareTo(a.Count));

        // Try top candidates, apply safety filters
        var fallback = PopupTheme.Default(IsDominantlyDark(candidates));
        var accent = FindSafeAccent(candidates, totalSampled, fallback);

        return accent;
    }

    private static PopupTheme FindSafeAccent(List<(int R, int G, int B, int Count)> candidates, int total, PopupTheme fallback)
    {
        for (int i = 0; i < Math.Min(candidates.Count, 10); i++)
        {
            var (r, g, b, count) = candidates[i];

            // Skip near-grays (saturation too low)
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            if (max - min < 20) continue;

            // Skip too dark or too bright
            double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
            if (luminance < 25 || luminance > 230) continue;

            // Skip very common grays (black, white, near-white backgrounds)
            if (r > 230 && g > 230 && b > 230) continue;
            if (r < 25 && g < 25 && b < 25) continue;

            // This color passes safety checks
            var accent = System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b);

            // Desaturate slightly if too vivid
            accent = Desaturate(accent, 0.75);

            var accentDark = ShiftLightness(accent, -0.20);
            var accentLight = ShiftLightness(accent, 0.35);
            accentLight.A = 30; // very subtle

            return new PopupTheme(accent, accentDark, accentLight, IsFromGame: true);
        }

        return fallback;
    }

    private static System.Windows.Media.Color Desaturate(System.Windows.Media.Color c, double amount)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double sat = max == 0 ? 0 : (max - min) / max;

        if (sat > 0.6)
        {
            double gray = (r + g + b) / 3.0;
            double blend = amount;
            r = r + (gray - r) * blend;
            g = g + (gray - g) * blend;
            b = b + (gray - b) * blend;
        }

        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
    }

    private static System.Windows.Media.Color ShiftLightness(System.Windows.Media.Color c, double shift)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;

        // Convert to HSL
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;

        if (max == min)
            return c; // gray, no shift meaningful

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h = 0;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6.0;

        l = Math.Clamp(l + shift, 0, 1);

        // HSL to RGB
        if (s == 0)
        {
            byte v = (byte)Math.Clamp(l * 255, 0, 255);
            return System.Windows.Media.Color.FromRgb(v, v, v);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3);
        g = HueToRgb(p, q, h);
        b = HueToRgb(p, q, h - 1.0 / 3);

        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static bool IsDominantlyDark(List<(int R, int G, int B, int Count)> candidates)
    {
        long totalLum = 0, totalCount = 0;
        foreach (var (r, g, b, count) in candidates.Take(20))
        {
            totalLum += (long)(0.299 * r + 0.587 * g + 0.114 * b) * count;
            totalCount += count;
        }
        return totalCount == 0 || totalLum / totalCount < 128;
    }
}
