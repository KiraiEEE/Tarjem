using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tarjem.Services;

/// <summary>One language as the pickers show it: a flag, a name, and the code everything else uses.</summary>
public sealed record LanguageOption(string Code, string Name, ImageSource? Flag);

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
        TranslationService.TargetLanguages
            .Select(l => new LanguageOption(l.Code, l.Name, For(l.Code)))
            .ToArray();

    /// <summary>Every source language as a pickable option, flags included.</summary>
    public static LanguageOption[] Sources() =>
        TranslationService.SourceLanguages
            .Select(l => new LanguageOption(l.Code, l.Name, For(l.Code)))
            .ToArray();
}
