using Tarjem.Core.Language;
using Tarjem.Models;
using Tarjem.Views;

namespace Tarjem.Services;

/// <summary>
/// Applies a language picked from one of the result windows. Every picker in the app - the word
/// popup, the "what is this?" card, the region overlay, the tray menu, Settings - writes through
/// here, so they all mean the same thing by "Auto-detect" and all keep the recent list in step.
/// </summary>
public static class LanguageChoice
{
    private const int MaxRecent = 4;

    /// <summary>Saves <paramref name="pick"/> as the new default. Returns false when it changed
    /// nothing, so the caller can skip re-translating.</summary>
    public static bool Apply(SettingsService settings, LanguagePick pick)
    {
        var s = settings.Current;
        var changed = false;

        if (pick.Source is { } source)
        {
            if (source == LanguageCatalog.Auto)
            {
                changed |= !s.AutoDetectSourceLanguage;
                s.AutoDetectSourceLanguage = true;
            }
            else if (LanguageCatalog.Find(source) is { } language)
            {
                changed |= s.AutoDetectSourceLanguage || s.SourceLanguage != language.Code;
                s.AutoDetectSourceLanguage = false;
                s.SourceLanguage = language.Code;
            }
        }

        if (pick.Target is { } target && LanguageCatalog.Find(target) is { } targetLanguage)
        {
            if (s.TargetLanguage != targetLanguage.Code)
            {
                Remember(s, s.TargetLanguage);
                s.TargetLanguage = targetLanguage.Code;
                changed = true;
            }
            Remember(s, targetLanguage.Code);
        }

        if (changed)
            settings.Save();

        return changed;
    }

    /// <summary>The recent targets to offer first, most recent first.</summary>
    public static IReadOnlyList<string> RecentTargets(AppSettings s) =>
        s.RecentTargetLanguages.Count > 0 ? s.RecentTargetLanguages : [s.TargetLanguage];

    private static void Remember(AppSettings s, string code)
    {
        if (!LanguageCatalog.IsKnown(code)) return;
        s.RecentTargetLanguages.RemoveAll(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        s.RecentTargetLanguages.Insert(0, code);
        if (s.RecentTargetLanguages.Count > MaxRecent)
            s.RecentTargetLanguages.RemoveRange(MaxRecent, s.RecentTargetLanguages.Count - MaxRecent);
    }
}
