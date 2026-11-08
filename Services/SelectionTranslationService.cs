using System.Windows;
using Serilog;
using Tarjem.Core.Language;
using Tarjem.Views;

namespace Tarjem.Services;

/// <summary>
/// The copy-to-translate flow: copy text anywhere, a small Tarjem button appears near the cursor,
/// and clicking it translates what was copied.
///
/// This is the cheapest good path in the app. Where the OCR hotkeys have to recognize glyphs and
/// then guess which word was meant, copied text is already exact - so none of the merged-glyph,
/// split-word or wrong-line failure modes apply.
///
/// Triggered by the copy rather than by the selection: detecting a selection meant guessing from
/// mouse movement, which fired constantly over games and missed keyboard selections entirely. A
/// copy is an unambiguous statement that the user wants that text.
///
/// Off unless <see cref="Models.AppSettings.SelectionPopupEnabled"/> says otherwise.
/// </summary>
public sealed class SelectionTranslationService : IDisposable
{
    /// <summary>Longer than this is a select-all rather than a phrase someone wants translated,
    /// and would be a large, slow request.</summary>
    private const int MaxSelectionLength = 2000;

    private readonly TranslationService _translation;
    private readonly SettingsService _settings;
    private readonly IntPtr _windowHandle;

    private ClipboardWatcher? _watcher;
    private SelectionButtonWindow? _button;
    private RegionOverlayWindow? _overlay;
    private CancellationTokenSource? _cts;

    private string _pendingText = "";
    private System.Drawing.Point _buttonAnchor;

    public SelectionTranslationService(TranslationService translation, SettingsService settings, IntPtr windowHandle)
    {
        _translation = translation;
        _settings = settings;
        _windowHandle = windowHandle;
    }

    /// <summary>Starts or stops the watcher to match the current setting. Safe to call repeatedly -
    /// Settings calls it on every toggle.</summary>
    public void ApplySettings()
    {
        if (_settings.Current.SelectionPopupEnabled)
            Start();
        else
            Stop();
    }

    /// <summary>Tells the watcher that the next clipboard change is Tarjem's own copy button, so
    /// copying a translation doesn't immediately offer to translate it again.</summary>
    public void IgnoreOwnCopy(string text) => _watcher?.IgnoreNext(text);

    private void Start()
    {
        if (_watcher != null) return;

        _watcher = new ClipboardWatcher(_windowHandle);
        _watcher.TextCopied += OnTextCopied;
        _watcher.Start();
    }

    private void Stop()
    {
        if (_watcher != null)
        {
            _watcher.TextCopied -= OnTextCopied;
            _watcher.Dispose();
            _watcher = null;
        }

        _button?.Hide();
        CloseOverlay();
    }

    private void OnTextCopied(object? sender, string text)
    {
        _pendingText = text.Length > MaxSelectionLength ? text[..MaxSelectionLength] : text;
        _buttonAnchor = System.Windows.Forms.Cursor.Position;

        _button ??= CreateButton();
        _button.ShowNear(_buttonAnchor);
    }

    private SelectionButtonWindow CreateButton()
    {
        var button = new SelectionButtonWindow();
        button.Clicked += (_, _) => _ = TranslateAsync();
        return button;
    }

    private async Task TranslateAsync()
    {
        if (string.IsNullOrWhiteSpace(_pendingText)) return;

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        var text = _pendingText;

        // A new copy is a new piece of text; a source fixed for the last one says nothing about it.
        _userFixedSource = false;

        try
        {
            ShowOverlay(text);
            await TranslateIntoOverlayAsync(text, _settings.Current.DefaultTranslationProvider, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another copy.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Copied-text translation failed");
            CloseOverlay();
        }
    }

    /// <summary>The language the copied text was found to be in, for the chip and for swapping.</summary>
    private string _detectedSource = "en";

    /// <summary>
    /// Copied text has no OCR behind it, so there is nothing to have guessed the language from -
    /// it used to be sent as whatever language OCR was set to read, which made copying a French
    /// sentence while reading English games come back untranslated. Unless the user has fixed a
    /// source language, the translator detects it.
    /// </summary>
    private async Task TranslateIntoOverlayAsync(string text, string provider, CancellationToken ct)
    {
        var settings = _settings.Current;
        var source = settings.AutoDetectSourceLanguage ? _detectedSource : settings.SourceLanguage;
        var detect = settings.AutoDetectSourceLanguage || !_userFixedSource;

        var translated = await _translation.TranslateBlockAsync(provider, text, source, detect, ct);
        if (ct.IsCancellationRequested || _overlay == null) return;

        if (translated?.DetectedLanguage is { } detected) _detectedSource = detected;
        else if (!detect) _detectedSource = source;

        var target = settings.TargetLanguage;
        _overlay.SetProviders(TranslationService.TranslationProviders, translated?.ProviderId ?? provider);
        _overlay.SetLanguages(_detectedSource, detect, target,
            OcrService.InstalledLanguages(), LanguageChoice.RecentTargets(settings));
        _overlay.SetTranslation(
            text,
            translated?.Text ?? text,
            TranslationService.IsRtlLanguage(target),
            ocrLineCount: 1,
            sourceFontSizePhysical: 0,
            sourceIsRtl: TranslationService.IsRtlLanguage(_detectedSource));
    }

    /// <summary>True once the user picked a specific source on this overlay; until then copied
    /// text is always detected, whatever OCR is set to read.</summary>
    private bool _userFixedSource;

    private async void OnLanguagePicked(object? sender, LanguagePick pick)
    {
        var overlay = _overlay;
        if (overlay == null) return;

        if (pick.Source is { } source)
            _userFixedSource = source != LanguageCatalog.Auto;

        LanguageChoice.Apply(_settings, pick);

        try
        {
            overlay.ShowLoading();
            await TranslateIntoOverlayAsync(overlay.CurrentOriginalText, _settings.Current.DefaultTranslationProvider,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Copied-text re-translation after a language change failed");
        }
    }

    /// <summary>
    /// Reuses the region overlay for the result rather than introducing another result window: it
    /// already handles right-to-left layout, font fitting, the provider switcher, copy and
    /// click-to-dismiss. All it needs is a rectangle to sit in, which copied text doesn't have -
    /// so one is synthesized near the cursor.
    /// </summary>
    private void ShowOverlay(string original)
    {
        CloseOverlay();

        var monitor = DpiHelper.MonitorFor(_buttonAnchor.X, _buttonAnchor.Y);

        // A comfortable reading box in physical pixels, kept inside the monitor.
        var width = 520 * monitor.Scale;
        var height = Math.Clamp(120 + original.Length / 4.0, 120, 320) * monitor.Scale;

        var boundsLeft = monitor.Bounds.Left * monitor.Scale;
        var boundsRight = monitor.Bounds.Right * monitor.Scale;
        var boundsBottom = monitor.Bounds.Bottom * monitor.Scale;

        var left = Math.Clamp(_buttonAnchor.X - width / 2, boundsLeft, Math.Max(boundsLeft, boundsRight - width));
        var top = Math.Clamp(_buttonAnchor.Y + 36 * monitor.Scale, monitor.Bounds.Top * monitor.Scale, Math.Max(0, boundsBottom - height));

        var overlay = new RegionOverlayWindow();
        overlay.Dismissed += (_, _) =>
        {
            _cts?.Cancel();
            CloseOverlay();
        };
        overlay.ApiChanged += OnProviderChanged;
        overlay.LanguagePicked += OnLanguagePicked;
        overlay.TextCopied += (_, copied) => IgnoreOwnCopy(copied);

        overlay.ApplyOptions(
            dimBehind: _settings.Current.DimBehindOverlays,
            showOriginal: true,
            matchSourceFontSize: false);

        overlay.PositionOverRegion(new Rect(left, top, width, height));
        overlay.ShowLoading();
        overlay.Show();

        _overlay = overlay;
    }

    private async void OnProviderChanged(object? sender, string providerId)
    {
        var overlay = _overlay;
        if (overlay == null) return;

        try
        {
            var original = overlay.CurrentOriginalText;
            if (string.IsNullOrWhiteSpace(original)) return;

            await TranslateIntoOverlayAsync(original, providerId, _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Copied-text provider switch to {Provider} failed", providerId);
        }
    }

    private void CloseOverlay()
    {
        if (_overlay == null) return;

        try { _overlay.Close(); } catch { /* already closing */ }
        _overlay = null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Cancel();
        _cts?.Dispose();

        if (_button != null)
        {
            try { _button.Close(); } catch { /* already closing */ }
            _button = null;
        }
    }
}
