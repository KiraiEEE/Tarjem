using Tarjem.Core.Translation;

namespace Tarjem.Models;

public class AppSettings
{
    /// <summary>
    /// Repairs whatever a settings file can say that the app can't use: a null where a list or a
    /// hotkey belongs (JSON written by hand, or by a version that didn't have the property), a
    /// language code the catalog doesn't know, a style name that no longer exists, a window size of
    /// zero. Each bad value falls back to its default rather than taking the app down later, in
    /// some unrelated place, the first time it's read.
    /// </summary>
    public AppSettings Sanitized()
    {
        var defaults = new AppSettings();

        RecentTargetLanguages = (RecentTargetLanguages ?? new())
            .Where(Tarjem.Core.Language.LanguageCatalog.IsKnown).Distinct().Take(4).ToList();
        ModelHealth ??= new();
        WordHotkey ??= HotkeyBinding.DefaultWord();
        RegionHotkey ??= HotkeyBinding.DefaultRegion();
        ExplainHotkey ??= HotkeyBinding.DefaultExplain();

        if (!Tarjem.Core.Language.LanguageCatalog.IsKnown(SourceLanguage)) SourceLanguage = defaults.SourceLanguage;
        else SourceLanguage = Tarjem.Core.Language.LanguageCatalog.Find(SourceLanguage)!.Code;
        if (!Tarjem.Core.Language.LanguageCatalog.IsKnown(TargetLanguage)) TargetLanguage = defaults.TargetLanguage;
        else TargetLanguage = Tarjem.Core.Language.LanguageCatalog.Find(TargetLanguage)!.Code;

        if (RegionDisplayMode is not ("InPlace" or "Panel")) RegionDisplayMode = defaults.RegionDisplayMode;
        if (RegionFontSizeMode is not ("MatchOriginal" or "FitRegion")) RegionFontSizeMode = defaults.RegionFontSizeMode;
        if (Theme is not ("System" or "Light" or "Dark")) Theme = defaults.Theme;
        if (ExplainAnswerLength is not ("Short" or "Standard" or "Full")) ExplainAnswerLength = defaults.ExplainAnswerLength;
        if (string.IsNullOrWhiteSpace(PopupVisualStyle)) PopupVisualStyle = defaults.PopupVisualStyle;
        if (string.IsNullOrWhiteSpace(DefaultTranslationProvider)) DefaultTranslationProvider = defaults.DefaultTranslationProvider;
        if (string.IsNullOrWhiteSpace(DefaultDictionaryProvider)) DefaultDictionaryProvider = defaults.DefaultDictionaryProvider;
        if (string.IsNullOrWhiteSpace(DefaultEncyclopediaProvider)) DefaultEncyclopediaProvider = defaults.DefaultEncyclopediaProvider;
        if (string.IsNullOrWhiteSpace(RegionTranslationProvider)) RegionTranslationProvider = defaults.RegionTranslationProvider;

        if (!(MainWindowWidth is >= 400 and <= 10000)) MainWindowWidth = defaults.MainWindowWidth;
        if (!(MainWindowHeight is >= 300 and <= 10000)) MainWindowHeight = defaults.MainWindowHeight;

        if (!ShowDictionarySection && !ShowTranslationSection) ShowDictionarySection = true;

        return this;
    }

    // ── API keys (legacy - migration only) ──
    // User-supplied keys now live DPAPI-encrypted in keys.dat alongside the shipped ones (see
    // SecureKeyService.SaveUserKey). settings.json is plaintext, so it is not somewhere an API
    // key belongs. These four properties remain solely so a pre-0.4 settings.json can be read
    // once: SettingsService moves any value it finds into the encrypted store and blanks it here.
    // Nothing writes them any more - do not add new reads.

    public string? GeminiApiKey { get; set; }
    public string? MerriamWebsterApiKey { get; set; }
    public string? GroqApiKey { get; set; }
    public string? CerebrasApiKey { get; set; }

    /// <summary>Which source the automatic OCR lookup uses by default for each section - see
    /// <see cref="Tarjem.Services.TranslationService.DictionaryProviders"/> /
    /// <see cref="Tarjem.Services.TranslationService.TranslationProviders"/> for the valid ids.
    /// Defaults to the free no-key APIs rather than Gemini so the automatic popup stays fast
    /// and doesn't spend Gemini quota on every lookup; Gemini is still one switch away in the
    /// popup, or can be set as the default here.</summary>
    public string DefaultDictionaryProvider { get; set; } = "freedictionaryapi.com";
    public string DefaultTranslationProvider { get; set; } = "google";

    /// <summary>After the quick keyless translation is on screen, ask an AI source (when one has a
    /// key) for a natural one and swap it in. The popup is as fast as before; the sentence just
    /// gets better a moment later. Costs one AI request per lookup.</summary>
    public bool RefineTranslationsWithAi { get; set; } = true;

    /// <summary>
    /// Which source Alt+W translates a region with. Separate from
    /// <see cref="DefaultTranslationProvider"/> because the reason that one defaults to Google -
    /// not spending a model request on every hover - doesn't apply here: a region is a deliberate
    /// drag, a few times a session, and it is the text where Google's word-for-word output shows
    /// worst. <see cref="Tarjem.Services.TranslationService.AutoRegionProvider"/> means "the best
    /// AI source that has a key, otherwise the default above".
    /// </summary>
    public string RegionTranslationProvider { get; set; } = Tarjem.Services.TranslationService.AutoRegionProvider;

    /// <summary>Which source Alt+E asks first. Wikipedia by default rather than one of the AI
    /// sources, because a wiki that answers can be linked to and an AI that answers cannot - the
    /// models are there for the long tail neither wiki carries, and the failover reaches them on
    /// its own without anyone having to opt in.</summary>
    public string DefaultEncyclopediaProvider { get; set; } = "wikipedia";

    // ── Word popup (Alt+Q) sections ──

    /// <summary>Show the definition block in the word popup. Turning both this and
    /// <see cref="ShowTranslationSection"/> off would leave an empty popup, so the UI keeps at
    /// least one enabled.</summary>
    public bool ShowDictionarySection { get; set; } = true;

    /// <summary>Show the translated-sentence block in the word popup.</summary>
    public bool ShowTranslationSection { get; set; } = true;

    /// <summary>Show the single translated word beside the headword, in the dictionary block -
    /// the quick "what does this one word mean" answer, without reading the sentence.</summary>
    public bool ShowInlineWordTranslation { get; set; } = true;

    /// <summary>Draw the translated word directly over the highlighted word on screen, the way
    /// the region overlay draws over the text it replaces, instead of only inside the popup.</summary>
    public bool ShowWordOverlayOnHighlight { get; set; }

    /// <summary>Look up proper nouns (company names, products, people) that no dictionary has an
    /// entry for, and show a one-line "what this is" instead of "definition not found". This is
    /// the *automatic* fallback only - <see cref="ExplainHotkey"/> asks Wikipedia directly and
    /// ignores this setting, because pressing it is already the user saying they want it.</summary>
    public bool EncyclopediaFallbackEnabled { get; set; } = true;

    /// <summary>Let the region overlay borrow the colors of whatever is behind it so translated
    /// text sits inside a game's own UI instead of on a foreign-looking card.</summary>
    public bool RegionMatchesSceneTheme { get; set; } = true;

    /// <summary>ISO code the translation section targets - see
    /// <see cref="Tarjem.Services.TranslationService.TargetLanguages"/>.</summary>
    public string TargetLanguage { get; set; } = "ar";

    /// <summary>Language OCR reads from the screen - a <see cref="Tarjem.Core.Language.LanguageCatalog"/>
    /// code. With <see cref="AutoDetectSourceLanguage"/> on, this is the preferred language: it
    /// represents its script during detection and wins every undecided call.</summary>
    public string SourceLanguage { get; set; } = "en";

    /// <summary>When true, the source language is worked out per lookup: one OCR pass per
    /// installed script picks the script, the text itself picks the language within it, and
    /// translators that can detect the language are asked to confirm it.</summary>
    /// <remarks>On by default from 0.7, for fresh installs only - every existing settings.json
    /// stores this explicitly, so nobody's choice is changed. It costs one OCR pass per installed
    /// script, which for most machines is one or two.</remarks>
    public bool AutoDetectSourceLanguage { get; set; } = true;

    /// <summary>When false, lookups are never written to history, the History page and its tray
    /// menu entry are hidden, and the app keeps no record of what was looked up.</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>Global shortcut for the word lookup (Alt+Q by default).</summary>
    public HotkeyBinding WordHotkey { get; set; } = HotkeyBinding.DefaultWord();

    /// <summary>Global shortcut for the region translation (Alt+W by default).</summary>
    public HotkeyBinding RegionHotkey { get; set; } = HotkeyBinding.DefaultRegion();

    /// <summary>Global shortcut for the encyclopedia lookup (Alt+E by default) - "what *is* this
    /// word", for names no dictionary carries.</summary>
    public HotkeyBinding ExplainHotkey { get; set; } = HotkeyBinding.DefaultExplain();

    /// <summary>Shows a small Tarjem button next to text the user selects anywhere on the screen;
    /// clicking it translates that selection. Off by default and offered during onboarding,
    /// because a watcher that reacts to every selection is exactly the kind of thing that should
    /// be opted into rather than discovered.</summary>
    public bool SelectionPopupEnabled { get; set; }

    /// <summary>Appearance: "System" follows Windows, "Light"/"Dark" pin it.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Checks GitHub once per launch for a newer release and shows a note in Settings.
    /// Never downloads or installs anything on its own.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public bool StartMinimized { get; set; } = true;

    // Roomy enough that the Settings cards and the History list/detail split both breathe at the
    // default size, rather than every user having to resize on first run.
    public double MainWindowWidth { get; set; } = 1150;
    public double MainWindowHeight { get; set; } = 780;

    /// <summary>Whether the window was maximized when it was last closed. Size alone isn't
    /// enough to restore it - a maximized window reports its restore size, so without this it
    /// always came back windowed.</summary>
    public bool MainWindowMaximized { get; set; }

    /// <summary>Visual style shared by both result cards - the word popup (Alt+Q) and the
    /// "what is this?" card (Alt+E). "Minimal" (the default - WinUI-flavoured, plain and quiet),
    /// "Fluent" (carries your Windows accent colour), or "Glass" (translucent, over a blur of
    /// whatever is behind it).</summary>
    public string PopupVisualStyle { get; set; } = "Minimal";

    /// <summary>True once the first-launch welcome flow has been completed (or skipped), so it
    /// never shows again after the first run.</summary>
    public bool HasCompletedOnboarding { get; set; }

    /// <summary>Per-model health (retired/rate-limited/etc.), keyed by Gemini model id.
    /// Persisted so a dead model stays skipped across restarts instead of being
    /// retried - and paying its latency - on every single lookup.</summary>
    public Dictionary<string, ModelHealth> ModelHealth { get; set; } = new();

    /// <summary>Salted hash of the API key <see cref="ModelHealth"/> was recorded against, so a
    /// changed key discards verdicts that were only ever true of the old one. Never the key
    /// itself - settings.json is plaintext (every real key lives DPAPI-encrypted in keys.dat).</summary>
    public string? ModelHealthKeyFingerprint { get; set; }

    // ── "What is this?" card (Alt+E) ──

    /// <summary>How much of the encyclopedia article the card shows: "Short" (a line or two),
    /// "Standard" (the default - roughly the opening sentences) or "Full" (the whole opening
    /// paragraph). Only ever a trim of the summary that was already fetched, so a longer answer
    /// costs nothing but height - except that a longer answer is also a longer translation.</summary>
    public string ExplainAnswerLength { get; set; } = "Standard";

    /// <summary>Translate the answer into <see cref="TargetLanguage"/>. Turning this off skips the
    /// translation request entirely, which is most of what Alt+E spends its time on - worth having
    /// for anyone who only wants to know what the name refers to.</summary>
    public bool ExplainTranslateAnswer { get; set; } = true;

    /// <summary>Keep the original-language summary above the translation. With this off, a
    /// translated answer replaces the original instead of sitting under it - the same choice the
    /// region overlay offers with <see cref="RegionShowOriginal"/>, in the opposite default,
    /// because here the original is the article you asked for rather than OCR output.</summary>
    public bool ExplainShowOriginal { get; set; } = true;

    /// <summary>Show the "Read more" button that opens the Wikipedia/Wikidata page.</summary>
    public bool ExplainShowSourceLink { get; set; } = true;

    // ── Overlays ──

    /// <summary>How Alt+W shows its translation. "InPlace" (the default) redraws each block of
    /// text where it was, in its own colours and size, so the screen reads as if it had been
    /// written in your language. "Panel" puts the whole translation on one dark plate over the
    /// selection - plainer, and easier on very busy or animated backgrounds.</summary>
    public string RegionDisplayMode { get; set; } = "InPlace";

    /// <summary>Target languages picked recently, most recent first. Shown at the top of the
    /// language pickers on the result windows.</summary>
    public List<string> RecentTargetLanguages { get; set; } = new();

    /// <summary>When true, the region overlay shows the recognized text (smaller, muted)
    /// above the translation. When false, only the translation is shown.</summary>
    public bool RegionShowOriginal { get; set; }

    /// <summary>How the region overlay sizes its translated text:
    /// "MatchOriginal" renders it at the size of the text it replaced (estimated from the OCR
    /// glyph metrics), "FitRegion" makes it as large as the selection allows.</summary>
    public string RegionFontSizeMode { get; set; } = "MatchOriginal";

    /// <summary>When true, both overlays darken the rest of the screen slightly while they're
    /// up. Besides focusing attention, it's the visual cue that clicking anywhere dismisses
    /// them.</summary>
    public bool DimBehindOverlays { get; set; } = true;
}
