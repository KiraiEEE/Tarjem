using System.Windows.Media;

namespace Tarjem.Services;

/// <summary>
/// The fonts the translation overlays render with, in one place so the Alt+Q popup and the
/// Alt+W region overlay always look the same.
/// </summary>
public static class UiFonts
{
    /// <summary>
    /// Right-to-left text, in IBM Plex Sans Arabic - bundled with the app rather than borrowed
    /// from Windows.
    ///
    /// This replaced Times New Roman, which was a Latin serif carrying a secondary Arabic face:
    /// technically it drew the glyphs, but it drew them as an afterthought, with the cramped
    /// joins and inconsistent stroke weight that Arabic readers notice immediately. Plex Sans
    /// Arabic was drawn as an Arabic typeface first, matches the Latin UI font in weight and
    /// rhythm, and is open-licensed. It is bundled because a font this central to how the app
    /// reads cannot depend on what a given machine happens to have installed - the fallbacks
    /// after it exist only for the case where loading the packed font fails outright.
    /// </summary>
    public static readonly FontFamily Rtl = new(
        new Uri("pack://application:,,,/Tarjem;component/Assets/Fonts/"),
        "./#IBM Plex Sans Arabic, Segoe UI, Tahoma");

    /// <summary>Left-to-right text. Segoe UI Variable is Windows 11's own text face - sharper at
    /// small sizes than Segoe UI and already on every machine the app targets, so there is
    /// nothing to bundle; Segoe UI catches Windows 10.</summary>
    public static readonly FontFamily Ltr = new("Segoe UI Variable Text, Segoe UI, sans-serif");

    /// <summary>
    /// Plex Sans Arabic sits very close to Segoe UI's apparent size, unlike the Times New Roman
    /// it replaced (which needed an 18% boost to stop every Arabic translation rendering visibly
    /// smaller than the English beside it). The small remaining nudge accounts for Arabic's
    /// shorter x-height relative to its em box.
    /// </summary>
    public const double RtlSizeCompensation = 1.06;

    public static FontFamily For(bool isRtl) => isRtl ? Rtl : Ltr;

    /// <summary>Font size that renders at the same apparent size as <paramref name="size"/> in
    /// the left-to-right font.</summary>
    public static double SizeFor(double size, bool isRtl) => isRtl ? size * RtlSizeCompensation : size;
}
