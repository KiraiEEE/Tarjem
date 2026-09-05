using Tarjem.Core.Translation;

namespace Tarjem.Models;

public class AppSettings
{
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

    /// <summary>ISO code OCR reads from the screen - see
    /// <see cref="Tarjem.Services.TranslationService.TargetLanguages"/> (same list reused as the
    /// source-language options). Only "en" gets the full free-dictionary-API lineup; any other
    /// source forces the dictionary provider to Gemini, since the rest are English-only.</summary>
    public string SourceLanguage { get; set; } = "en";

    /// <summary>When true, OCR races every supported language that actually has a Windows OCR
    /// pack installed in parallel and keeps whichever recognized the most text, instead of
    /// assuming <see cref="SourceLanguage"/>. Slower per-lookup since it's running multiple OCR
    /// passes instead of one.</summary>
    public bool AutoDetectSourceLanguage { get; set; }

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
