using System.Windows.Media;
using Microsoft.Win32;
using Serilog;

namespace Tarjem.Services;

/// <summary>
/// The colour the user picked in Windows Settings > Personalization > Colors.
///
/// The Fluent popup style is supposed to be "the app, wearing your accent" - so it has to ask
/// Windows what that is rather than shipping a purple of its own. Read on demand and cached only
/// briefly, because changing the accent colour is something a user does and then immediately looks
/// for the effect of.
/// </summary>
public static class AccentColorService
{
    /// <summary>The fallback is Windows' own default accent blue, not Tarjem's old purple: if the
    /// read fails the popup should still look like a Windows accent, just not this machine's.</summary>
    private static readonly Color Fallback = Color.FromRgb(0x00, 0x78, 0xD4);

    private static Color? _cached;
    private static DateTime _cachedAt;

    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>The system accent, as-is.</summary>
    public static Color Accent
    {
        get
        {
            if (_cached is { } cached && DateTime.UtcNow - _cachedAt < CacheFor)
                return cached;

            var accent = Read();
            _cached = accent;
            _cachedAt = DateTime.UtcNow;
            return accent;
        }
    }

    /// <summary>The accent, nudged towards readability on the surface it will sit on. Several
    /// perfectly normal Windows accents (the darker blues, and anything near-black) vanish against
    /// a dark card, and the lightest ones do the same on a light one.</summary>
    public static Color AccentFor(bool isDark)
    {
        var accent = Accent;
        var luminance = Luminance(accent);

        if (isDark && luminance < 0.34) return Lighten(accent, 0.42);
        if (!isDark && luminance > 0.72) return Darken(accent, 0.32);
        return accent;
    }

    /// <summary>Reads HKCU\...\DWM\AccentColor, which is where Windows keeps the resolved accent
    /// for the current theme. It is stored ABGR, not ARGB - reading it as ARGB is what turns a
    /// blue accent into an orange one.</summary>
    private static Color Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");

            if (key?.GetValue("AccentColor") is int abgr)
            {
                var bytes = BitConverter.GetBytes(abgr);
                return Color.FromRgb(bytes[0], bytes[1], bytes[2]);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the Windows accent colour");
        }

        return Fallback;
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));
}
