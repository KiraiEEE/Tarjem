using System.Windows;
using Serilog;
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

        try
        {
            ShowOverlay(text);

            var provider = _settings.Current.DefaultTranslationProvider;
            var translated = await _translation.TranslateTextWithFailoverAsync(
                provider, text, _settings.Current.SourceLanguage, cts.Token);

            if (cts.IsCancellationRequested || _overlay == null) return;

            var targetCode = _settings.Current.TargetLanguage;
            _overlay.SetProviders(TranslationService.TranslationProviders, provider);
            _overlay.SetTranslation(
                text,
                string.IsNullOrWhiteSpace(translated) ? text : translated,
                TranslationService.IsRtlLanguage(targetCode),
                ocrLineCount: 1,
                sourceFontSizePhysical: 0,
                sourceIsRtl: TranslationService.IsRtlLanguage(_settings.Current.SourceLanguage));
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

            var translated = await _translation.TranslateTextWithFailoverAsync(
                providerId, original, _settings.Current.SourceLanguage, _cts?.Token ?? CancellationToken.None);

            if (!ReferenceEquals(overlay, _overlay)) return;

            overlay.SetTranslationDirect(string.IsNullOrWhiteSpace(translated) ? original : translated);
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
