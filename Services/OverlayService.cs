using System.Windows;
using Tarjem.Models;
using Tarjem.Views;

namespace Tarjem.Services;

public class OverlayService
{
    private OverlayWindow? _overlay;
    private PopupWindow? _popup;
    private ExplainWindow? _explain;

    /// <summary>Escape dismisses the word overlay. Its windows are WS_EX_NOACTIVATE so they never
    /// get keyboard input of their own, which left clicking as the only way out - and a dimmed
    /// screen that didn't seem to respond read as the app having frozen it.</summary>
    private EscapeWatcher? _escapeWatcher;

    /// <summary>Windows already fading out. A fade nulls its field first and closes later, so
    /// without this list an immediate dismissal could neither reach them nor report that
    /// anything was still on screen - which skipped the compositor wait and let the half-faded
    /// card be photographed straight into Alt+W's selection screenshot.</summary>
    private readonly List<Window> _closing = new();

    /// <summary>Raised with text Tarjem itself copied, so the clipboard watcher can ignore it.</summary>
    public event EventHandler<string>? TextCopied;

    private readonly SettingsService _settingsService;
    private readonly TranslationService _translationService;

    /// <summary>Read fresh on every use rather than captured once: "Clear all data" replaces the
    /// whole <see cref="AppSettings"/> instance, so a stored reference would silently keep
    /// serving the pre-reset values for the rest of the session.</summary>
    private AppSettings Settings => _settingsService.Current;

    public event EventHandler? Dismissed;
    public bool IsShowing => _popup != null;

    public OverlayService(SettingsService settingsService, TranslationService translationService)
    {
        _settingsService = settingsService;
        _translationService = translationService;
    }

    /// <param name="wordRectPhysical">The word's rectangle in absolute physical screen pixels.
    /// Physical rather than DIP because the overlay has to work out which monitor it belongs on
    /// before it can know what a DIP is worth there.</param>
    public void ShowHighlight(WpfRect wordRectPhysical)
    {
        if (_overlay == null)
        {
            _overlay = new OverlayWindow();
            _overlay.Clicked += (_, _) => DismissAll();
            _overlay.CoverMonitorFor(wordRectPhysical);
            _overlay.Show();

            _escapeWatcher?.Dispose();
            _escapeWatcher = new EscapeWatcher();
            _escapeWatcher.Pressed += (_, _) => DismissAll();
            _escapeWatcher.Start();
        }

        _overlay.SetHighlight(wordRectPhysical);
        _overlay.ShowDim(Settings.DimBehindOverlays);
    }

    public void ShowTranslationPopup(WpfRect wordRectPhysical, TranslationResult result)
    {
        HidePopup();

        // Taken before the popup exists, so the Glass blur can never photograph the popup itself.
        var backdrop = CaptureBackdropIfGlass(wordRectPhysical);

        var popup = new PopupWindow { Options = Settings };
        _popup = popup;

        popup.ApplyStyle(Settings.PopupVisualStyle, backdrop);
        popup.TextCopied += (_, text) => TextCopied?.Invoke(this, text);

        // Reflect the source that actually answered, which after a failover is not the one in
        // Settings at all - the dropdown must not credit a provider that was down at the time.
        var dictionaryDefaultId = !string.IsNullOrEmpty(result.DefinitionProvider)
            ? result.DefinitionProvider
            : Settings.DefaultDictionaryProvider;
        var translationDefaultId = !string.IsNullOrEmpty(result.TranslationProvider)
            ? result.TranslationProvider
            : Settings.DefaultTranslationProvider;

        popup.SetTranslationResult(result, dictionaryDefaultId, translationDefaultId);

        // The translated word, drawn over the word itself. Done here rather than inside the
        // popup because it belongs to the full-screen overlay, and only once a translation
        // actually exists - an empty box over the highlight would be worse than no box.
        if (Settings.ShowWordOverlayOnHighlight && _overlay != null && !string.IsNullOrWhiteSpace(result.ArabicWord))
            _overlay.ShowWordOverlay(result.ArabicWord, wordRectPhysical, TranslationService.IsRtlLanguage(result.TargetLanguage));

        popup.DictionarySourceRequested = providerId => _translationService.LookupDictionaryAsync(providerId, result.OriginalWord, result.SourceLanguage);
        popup.TranslationSourceRequested = providerId => _translationService.TranslateWithProviderAsync(providerId, result.OriginalWord, result.FullSentence, result.SourceLanguage);
        popup.GeminiRequested = () => _translationService.RequestGeminiAsync(result.OriginalWord, result.FullSentence, result.SourceLanguage);

        popup.Show();
        popup.UpdateLayout();

        PositionPopup(popup, wordRectPhysical);
        popup.RefreshBackdrop();
        popup.AnimateIn();
    }

    /// <summary>Only the Glass style has any use for a screen snapshot, and it costs a full-monitor
    /// capture, so the other two never pay for it.</summary>
    private ScreenBackdrop? CaptureBackdropIfGlass(WpfRect anchorPhysical) =>
        CardStyler.Normalize(Settings.PopupVisualStyle) == CardStyler.Glass
            ? ScreenBackdrop.CaptureMonitorFor(anchorPhysical)
            : null;

    /// <summary>
    /// Places the popup just above the word, flipping below it when there isn't room, and keeps it
    /// inside the work area of the monitor the word is on. Clamping against the *primary* monitor
    /// - which is what this did - dragged the popup back onto display 1 whenever the user was
    /// reading on display 2.
    /// </summary>
    private static void PositionPopup(PopupWindow popup, WpfRect wordRectPhysical) =>
        PositionNearWord(popup, wordRectPhysical);

    /// <summary>
    /// Places a result window just above the word, flipping below it when there isn't room, and
    /// keeps it inside the work area of the monitor the word is on. Shared by the word popup and
    /// the explain card, which want identical placement behaviour.
    /// </summary>
    private static void PositionNearWord(Window popup, WpfRect wordRectPhysical)
    {
        var monitor = DpiHelper.MonitorFor(wordRectPhysical);
        var area = monitor.WorkArea;

        // Word rect in the same DIP space the window manager places popups in.
        var word = new WpfRect(
            wordRectPhysical.Left / monitor.Scale,
            wordRectPhysical.Top / monitor.Scale,
            wordRectPhysical.Width / monitor.Scale,
            wordRectPhysical.Height / monitor.Scale);

        var popupWidth = popup.ActualWidth;
        var popupHeight = popup.ActualHeight;

        var popupX = word.X + word.Width / 2 - popupWidth / 2;
        var popupY = word.Top - popupHeight - 14;

        if (popupY < area.Top + 10)
            popupY = word.Bottom + 14;

        popupX = Clamp(popupX, area.Left + 10, area.Right - popupWidth - 10);
        popupY = Clamp(popupY, area.Top + 10, area.Bottom - popupHeight - 10);

        popup.Left = popupX;
        popup.Top = popupY;

        // A popup taller or wider than the work area makes max < min; pinning to the leading edge
        // at least keeps it on screen rather than flinging it off the far side.
        static double Clamp(double value, double min, double max) =>
            max < min ? min : Math.Clamp(value, min, max);
    }

    /// <summary>
    /// Shows the encyclopedia answer in its own window, immediately in a loading state so the
    /// keypress produces something on screen before the network round-trip finishes.
    /// </summary>
    public ExplainWindow ShowExplainLoading(WpfRect wordRectPhysical, string word)
    {
        HideExplain();

        var backdrop = CaptureBackdropIfGlass(wordRectPhysical);

        var explain = new ExplainWindow { Options = Settings };
        _explain = explain;

        explain.ApplyStyle(Settings.PopupVisualStyle, backdrop);
        explain.TextCopied += (_, text) => TextCopied?.Invoke(this, text);

        // The card's own source switcher. Deliberately no failover - the user picked that source,
        // and answering from a different one would make the switcher lie about where the answer
        // came from.
        explain.SourceRequested = providerId =>
            _translationService.ExplainWithProviderAsync(providerId, word, Settings.SourceLanguage);

        // The card changes size when the switcher swaps its content, so it has to be re-placed
        // and the Glass blur re-registered with whatever is now behind it.
        explain.Repositioned += (_, _) =>
        {
            PositionNearWord(explain, wordRectPhysical);
            explain.RefreshBackdrop();
        };

        explain.ShowLoading(word);
        explain.Show();
        explain.UpdateLayout();

        PositionNearWord(explain, wordRectPhysical);
        explain.RefreshBackdrop();
        explain.AnimateIn();

        return explain;
    }

    /// <summary>Fills in the finished answer, re-positioning because the card grows once it has
    /// real content in it.</summary>
    public void CompleteExplain(ExplainWindow explain, TranslationResult result, WpfRect wordRectPhysical)
    {
        if (!ReferenceEquals(explain, _explain)) return;

        explain.AnimateContentSwap(() =>
        {
            explain.SetResult(result);
            explain.UpdateLayout();
            PositionNearWord(explain, wordRectPhysical);

            // Last, because the card has just changed both size and position and the Glass blur is
            // only convincing while it stays registered with the screen underneath it.
            explain.RefreshBackdrop();
        });
    }

    public void HideExplain()
    {
        if (_explain == null) return;

        var explain = _explain;
        _explain = null;
        TrackClosing(explain);
        explain.CloseAnimated();
    }

    public void HidePopup()
    {
        if (_popup != null)
        {
            var popup = _popup;
            _popup = null;
            TrackClosing(popup);
            popup.CloseAnimated();
        }
    }

    /// <param name="immediate">Close everything at once instead of fading out. Used when another
    /// hotkey is about to photograph the screen: a fade takes 140 ms, and a capture taken inside
    /// it froze the half-faded dim and card into Alt+W's selection screenshot.</param>
    /// <returns>True if anything was on screen to take down.</returns>
    public bool DismissAll(bool immediate = false)
    {
        var hadSomething = _overlay != null || _popup != null || _explain != null || _closing.Count > 0;

        _escapeWatcher?.Dispose();
        _escapeWatcher = null;

        if (immediate)
        {
            CloseNow(ref _explain);
            CloseNow(ref _popup);
            CloseNow(ref _overlay);
            CloseFading();
        }
        else
        {
            HideExplain();
            HidePopup();
            if (_overlay != null)
            {
                var overlay = _overlay;
                _overlay = null;
                TrackClosing(overlay);
                overlay.HideDim(() => overlay.Close());
            }
        }

        Dismissed?.Invoke(this, EventArgs.Empty);
        return hadSomething;
    }

    private void TrackClosing(Window window)
    {
        if (_closing.Contains(window)) return;
        _closing.Add(window);
        window.Closed += (_, _) => _closing.Remove(window);
    }

    /// <summary>Force-closes everything that is mid-fade, so the screen really is clear by the
    /// time a capture runs.</summary>
    private void CloseFading()
    {
        if (_closing.Count == 0) return;

        foreach (var window in _closing.ToArray())
        {
            try { window.Close(); } catch { }
        }
        _closing.Clear();
    }

    private static void CloseNow<T>(ref T? window) where T : Window
    {
        var w = window;
        window = null;
        try { w?.Close(); } catch { }
    }
}
