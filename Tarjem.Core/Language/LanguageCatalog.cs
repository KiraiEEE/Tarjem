namespace Tarjem.Core.Language;

/// <summary>One language Tarjem can read from or write into.</summary>
/// <param name="Code">The code stored in settings and history. Never changes once shipped:
/// existing settings.json files and history entries refer to languages by it.</param>
/// <param name="Name">English name, as the pickers show it.</param>
/// <param name="NativeName">The language's name for itself - shown beside the English name, so
/// someone who reads Ukrainian but not English still finds it.</param>
/// <param name="GoogleCode">What Google Translate and Lingva call it, when that differs.</param>
/// <param name="WikiCode">The Wikipedia/Wikidata edition, when that differs from the base tag.</param>
public sealed record LanguageInfo(
    string Code,
    string Name,
    string NativeName,
    bool IsRtl = false,
    string? GoogleCode = null,
    string? WikiCode = null)
{
    public string Google => GoogleCode ?? Code;

    public string Wiki => WikiCode ?? LanguageCatalog.BaseTag(Code);
}

/// <summary>
/// Every language the app offers, in one place.
///
/// This used to be a tuple array inside TranslationService, with the Wikipedia edition, Google's
/// code and the script each derived separately wherever they were needed - which is how Norwegian
/// would have asked for an "nb" Wikipedia that doesn't exist. Adding a language is now one line
/// here plus, if it reads in a new script, a case in <see cref="LanguageGuesser"/>.
///
/// The list is deliberately not "everything Google supports". Each entry is a language Windows can
/// OCR, or one a meaningful number of people translate <em>into</em> from screen text, and each has
/// a font on a stock Windows install that renders it.
/// </summary>
public static class LanguageCatalog
{
    /// <summary>The <c>SourceLanguage</c>-style value that means "work it out per lookup".</summary>
    public const string Auto = "auto";

    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        new("en", "English", "English"),
        new("ar", "Arabic", "العربية", IsRtl: true),
        new("es", "Spanish", "Español"),
        new("fr", "French", "Français"),
        new("de", "German", "Deutsch"),
        new("it", "Italian", "Italiano"),
        new("pt", "Portuguese", "Português"),
        new("ru", "Russian", "Русский"),
        new("uk", "Ukrainian", "Українська"),
        new("tr", "Turkish", "Türkçe"),
        new("nl", "Dutch", "Nederlands"),
        new("pl", "Polish", "Polski"),
        new("cs", "Czech", "Čeština"),
        new("sk", "Slovak", "Slovenčina"),
        new("ro", "Romanian", "Română"),
        new("hu", "Hungarian", "Magyar"),
        new("el", "Greek", "Ελληνικά"),
        new("bg", "Bulgarian", "Български"),
        new("hr", "Croatian", "Hrvatski"),
        new("sv", "Swedish", "Svenska"),
        new("da", "Danish", "Dansk"),
        new("nb", "Norwegian", "Norsk", GoogleCode: "no", WikiCode: "no"),
        new("fi", "Finnish", "Suomi"),
        new("ja", "Japanese", "日本語"),
        new("ko", "Korean", "한국어"),
        new("zh-CN", "Chinese (Simplified)", "简体中文"),
        new("zh-TW", "Chinese (Traditional)", "繁體中文"),
        new("vi", "Vietnamese", "Tiếng Việt"),
        new("id", "Indonesian", "Bahasa Indonesia"),
        new("ms", "Malay", "Bahasa Melayu"),
        new("th", "Thai", "ไทย"),
        new("hi", "Hindi", "हिन्दी"),
        new("bn", "Bengali", "বাংলা"),
        new("he", "Hebrew", "עברית", IsRtl: true, GoogleCode: "iw"),
        new("fa", "Persian", "فارسی", IsRtl: true),
        new("ur", "Urdu", "اردو", IsRtl: true),
    ];

    private static readonly Dictionary<string, LanguageInfo> ByCode =
        All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>The language for <paramref name="code"/>, matching case-insensitively and then on
    /// the base tag ("en-US" finds English, "zh-Hans" finds Simplified Chinese), or null.</summary>
    public static LanguageInfo? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (ByCode.TryGetValue(code, out var exact)) return exact;

        var lower = code.ToLowerInvariant();

        // Script and region subtags Windows uses for Chinese, which our codes spell by region.
        if (lower.StartsWith("zh"))
            return lower.Contains("hant") || lower.EndsWith("-tw") || lower.EndsWith("-hk") || lower.EndsWith("-mo")
                ? ByCode["zh-TW"]
                : ByCode["zh-CN"];

        // Google still answers with the retired codes for Hebrew and Norwegian.
        if (lower is "iw" or "iw-il") return ByCode["he"];
        if (lower is "no" or "nn" || lower.StartsWith("no-") || lower.StartsWith("nb-")) return ByCode["nb"];

        return ByCode.GetValueOrDefault(BaseTag(code));
    }

    public static bool IsKnown(string? code) => Find(code) != null;

    public static bool IsRtl(string? code) => Find(code)?.IsRtl ?? false;

    public static string NameOf(string? code) =>
        code == Auto ? "Auto-detect" : Find(code)?.Name ?? code ?? "";

    /// <summary>Short label for the language chips: "EN", "ZH", "AR".</summary>
    public static string ShortLabel(string? code) =>
        code == Auto ? "Auto" : BaseTag(Find(code)?.Code ?? code ?? "").ToUpperInvariant();

    public static string BaseTag(string languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag)) return "";
        var dash = languageTag.IndexOf('-');
        return (dash > 0 ? languageTag[..dash] : languageTag).ToLowerInvariant();
    }
}
