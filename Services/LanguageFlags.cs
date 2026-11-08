using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tarjem.Services;

/// <summary>One language as the pickers show it: a flag, a name, and the code everything else uses.</summary>
/// <param name="Detail">A muted note after the name - the language's own name for itself, or why
/// it may not work yet.</param>
public sealed record LanguageOption(string Code, string Name, ImageSource? Flag, string? Detail = null);

/// <summary>
/// The flag shown beside each language.
///
/// A language is not a country, so a few of these are editorial choices rather than lookups:
/// English is shown with the flag of the United States and Arabic with the flag of Tunisia. The
/// images are rasterised from the flag-icons set (MIT) and embedded, so nothing is fetched or
/// depends on what the machine has installed.
/// </summary>
public static class LanguageFlags
{
    private static readonly Dictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>The flag for a language code, or null when there is no image for it (in which case
    /// callers should simply show the name on its own).</summary>
    public static ImageSource? For(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return null;

        var key = languageCode.Replace('-', '_').ToLowerInvariant();

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            BitmapImage? image = null;
            try
            {
                image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri($"pack://application:,,,/Tarjem;component/Assets/Flags/{key}.png");
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
            }
            catch
            {
                // A missing flag is not worth failing a language picker over.
                image = null;
            }

            Cache[key] = image;
            return image;
        }
    }

    /// <summary>Every target language as a pickable option, flags included.</summary>
    public static LanguageOption[] Targets() =>
        Tarjem.Core.Language.LanguageCatalog.All
            .Select(l => new LanguageOption(l.Code, l.Name, For(l.Code), l.NativeName == l.Name ? null : l.NativeName))
            .ToArray();

    /// <summary>
    /// Every source language as a pickable option. Languages Windows can read on this machine come
    /// first; the rest follow, marked, because picking one of those means OCR falls back to another
    /// language until its pack is installed - better said in the list than discovered afterwards.
    /// </summary>
    /// <param name="includeAuto">Lead with "Auto-detect".</param>
    public static LanguageOption[] Sources(bool includeAuto = false)
    {
        var installed = new HashSet<string>(OcrService.InstalledLanguages(), StringComparer.OrdinalIgnoreCase);
        var all = Tarjem.Core.Language.LanguageCatalog.All;

        var options = all.Where(l => installed.Contains(l.Code))
            .Select(l => new LanguageOption(l.Code, l.Name, For(l.Code), l.NativeName == l.Name ? null : l.NativeName))
            .Concat(all.Where(l => !installed.Contains(l.Code))
                .Select(l => new LanguageOption(l.Code, l.Name, For(l.Code), installed.Count > 0 ? "no OCR pack" : null)));

        if (includeAuto)
            options = options.Prepend(new LanguageOption(Tarjem.Core.Language.LanguageCatalog.Auto, "Auto-detect", For("auto"), "recommended"));

        return options.ToArray();
    }
}
