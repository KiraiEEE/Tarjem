using System.Windows.Media;

namespace Tarjem.Models;

public record PopupTheme(
    Color Accent,
    Color AccentDark,
    Color AccentLight,
    bool IsFromGame)
{
    public static PopupTheme Default(bool isDark) => isDark ? DarkFallback : LightFallback;

    private static readonly PopupTheme DarkFallback = new(
        Accent: Color.FromRgb(0x7C, 0x3A, 0xED),
        AccentDark: Color.FromRgb(0x5B, 0x21, 0xB6),
        AccentLight: Color.FromArgb(30, 0x7C, 0x3A, 0xED),
        IsFromGame: false);

    private static readonly PopupTheme LightFallback = new(
        Accent: Color.FromRgb(0x7C, 0x3A, 0xED),
        AccentDark: Color.FromRgb(0x5B, 0x21, 0xB6),
        AccentLight: Color.FromArgb(25, 0x7C, 0x3A, 0xED),
        IsFromGame: false);
}
