using System.Drawing;
using System.Windows;
using Serilog;
using Tarjem.Core.Ocr;
using Tarjem.Models;
using Tarjem.Views;

namespace Tarjem.Services;

/// <summary>
/// Orchestrates the region translation pipeline: select region → capture → OCR → translate → overlay.
/// </summary>
public class RegionTranslationService : IDisposable
{
    private readonly OcrService _ocrService;
    private readonly TranslationService _translationService;
    private readonly SettingsService _settingsService;

    private RegionSelectionWindow? _selectionWindow;
    private RegionOverlayWindow? _overlayWindow;
    private CancellationTokenSource? _cts;

    /// <summary>Language OCR actually read the region in - a provider switch has to re-translate
    /// from the same source language, not from a hardcoded "en".</summary>
    private string _lastSourceLanguage = "en";
    private int _lastOcrLineCount = 1;
    private int _apiSwitchGeneration;
    private EscapeWatcher? _escapeWatcher;

    /// <summary>Fires when a region translation completes and is added to history.</summary>
    public event EventHandler<TranslationResult>? TranslationCompleted;

    /// <remarks>Deliberately takes no HistoryService: region translations are whole paragraphs of
    /// screen text, and writing those into what is meant to be a vocabulary list of looked-up
    /// words buries the words. See step 6 of <see cref="ProcessRegionAsync"/>.</remarks>
    public RegionTranslationService(
        OcrService ocrService,
        TranslationService translationService,
        SettingsService settingsService)
    {
        _ocrService = ocrService;
        _translationService = translationService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Starts the region translation flow: shows the selection window,
    /// then captures, OCRs, and translates the selected region.
    /// </summary>
    public void StartRegionTranslation()
    {
        // Cancel any in-flight translation
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Close any existing windows - at once, not with a fade, and make sure they are gone
        // from the screen before it is photographed. The new selection freezes a screenshot, and
        // one taken with the last overlay still on screen froze that overlay's dim into it.
        var closedSomething = _selectionWindow != null || _overlayWindow != null;
        CloseSelectionWindow();
        CloseOverlayWindow();
        if (closedSomething)
            ScreenCaptureGuard.WaitForCompositor();

        var selection = new RegionSelectionWindow();
        _selectionWindow = selection;
        selection.RegionSelected += async (_, rect) =>
        {
            if (!ReferenceEquals(selection, _selectionWindow)) return;

            // Crop from the frozen screenshot before the selection window goes away, so what
            // gets OCR'd is the desktop as it looked when the user drew the box.
            var regionBitmap = selection.CropRegion(rect);
            CloseSelectionWindow();
            await ProcessRegionAsync(rect, regionBitmap, ct);
        };
        selection.SelectionCancelled += (_, _) =>
        {
            if (!ReferenceEquals(selection, _selectionWindow)) return;
            StopEscapeWatcher();
            CloseSelectionWindow();
        };

        // Escape cancels. The selection window is WS_EX_NOACTIVATE so it never gets keyboard
        // input of its own - the watcher reads the key state globally instead.
        StartEscapeWatcher(() => Cancel());

        selection.ShowWithScreenshot(_settingsService.Current.DimBehindOverlays);
    }

    /// <summary>Cancels whatever part of the region flow is currently on screen.</summary>
    /// <param name="immediate">Close at once instead of fading out - for when another hotkey is
    /// about to capture the screen, and a fading overlay would end up in its screenshot.</param>
    /// <returns>True if anything was on screen to take down.</returns>
    public bool Cancel(bool immediate = false)
    {
        var hadSomething = _selectionWindow != null || _overlayWindow != null;

        StopEscapeWatcher();
        _cts?.Cancel();
        CloseSelectionWindow();

        if (_overlayWindow != null)
        {
            if (immediate)
                CloseOverlayWindow();
            else
                _overlayWindow.Dismiss(); // Fades out; its Dismissed handler clears the reference.
        }

        return hadSomething;
    }

    private void StartEscapeWatcher(Action onPressed)
    {
        StopEscapeWatcher();
        _escapeWatcher = new EscapeWatcher();
        _escapeWatcher.Pressed += (_, _) => onPressed();
        _escapeWatcher.Start();
    }

    private void StopEscapeWatcher()
    {
        _escapeWatcher?.Dispose();
        _escapeWatcher = null;
    }

    private async Task ProcessRegionAsync(Rect screenRegion, Bitmap? capturedRegion, CancellationToken ct)
    {
        RegionOverlayWindow? overlay = null;
        try
        {
            // 1. The region normally comes pre-cropped from the selection window's frozen
            //    screenshot; only fall back to a live grab if that didn't work out. The live grab
            //    has to happen *before* the overlay goes up - it used to happen after, and so
            //    OCR'd our own "Translating..." panel instead of the text underneath it.
            if (capturedRegion == null)
                ScreenCaptureGuard.WaitForCompositor();

            var bitmap = capturedRegion ?? await CaptureRegionAsync(
                (int)screenRegion.Left,
                (int)screenRegion.Top,
                (int)screenRegion.Width,
                (int)screenRegion.Height);

            if (bitmap == null || ct.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return;
            }

            // Show overlay in loading state. Held in a local as well as the field: every callback
            // below checks it is still *the* overlay before touching anything, because the field
            // moves on to a newer overlay the moment the user presses Alt+W again.
            overlay = new RegionOverlayWindow();
            _overlayWindow = overlay;
            overlay.Dismissed += (_, _) =>
            {
                // A dismissal that finishes fading after a newer region flow has started belongs
                // to the old overlay. Acting on it used to cancel the *new* flow and stop its
                // Escape watcher - leaving its frozen, dimmed selection screen stuck with no way
                // out - or close the new overlay out from under it.
                if (!ReferenceEquals(overlay, _overlayWindow)) return;

                // Clicking away mid-translation should stop the lookup too, not just hide it -
                // same contract as the Alt+Q overlay.
                _cts?.Cancel();
                CloseOverlayWindow();
            };
            overlay.ApiChanged += OnApiChanged;

            var overlayOptions = _settingsService.Current;
            overlay.ApplyOptions(
                dimBehind: overlayOptions.DimBehindOverlays,
                showOriginal: overlayOptions.RegionShowOriginal,
                matchSourceFontSize: overlayOptions.RegionFontSizeMode != "FitRegion");

            overlay.PositionOverRegion(screenRegion);
            overlay.ShowLoading();
            overlay.Show();

            try
            {
                ct.ThrowIfCancellationRequested();

                // 2. OCR all text in the region
                var settings = _settingsService.Current;
                string sourceLanguageTag;
                RecognizedText? ocrResult;

                if (settings.AutoDetectSourceLanguage)
                {
                    var candidates = TranslationService.SourceLanguages
                        .Select(l => l.Code)
                        .Where(_ocrService.IsLanguageAvailable)
                        .ToList();

                    var detected = await _ocrService.DetectAndRecognizeAsync(bitmap, candidates, settings.SourceLanguage, ct);
                    ct.ThrowIfCancellationRequested();

                    if (detected == null)
                    {
                        ShowNoTextFound(overlay);
                        return;
                    }

                    ocrResult = detected.Value.Result;
                    sourceLanguageTag = detected.Value.LanguageTag;
                }
                else
                {
                    sourceLanguageTag = settings.SourceLanguage;
                    ocrResult = await _ocrService.RecognizeTextAsync(bitmap, sourceLanguageTag);
                    ct.ThrowIfCancellationRequested();
                }

                if (ocrResult == null || ocrResult.Lines.Count == 0)
                {
                    ShowNoTextFound(overlay);
                    return;
                }

                // 3. Build the text from the OCR lines, rejoining sentences that only wrapped on
                //    screen. Sent row by row, a sentence split across three rows was translated as
                //    three fragments, each forced into the source's clause order - see TextReflow.
                var logicalLines = TextReflow.LogicalLines(ocrResult);
                var originalText = string.Join("\n", logicalLines);
                if (string.IsNullOrWhiteSpace(originalText))
                {
                    ShowNoTextFound(overlay);
                    return;
                }

                ct.ThrowIfCancellationRequested();

                // 4. Translate the combined text with whichever provider is configured as the
                //    default, so the chip on the overlay names the source actually used.
                var (targetLangCode, _) = GetTargetLanguage();
                _lastSourceLanguage = sourceLanguageTag;
                _lastOcrLineCount = logicalLines.Count;

                var currentProvider = _translationService.ResolveRegionProvider();

                var translatedText = await TranslateRegionTextAsync(currentProvider, originalText, sourceLanguageTag, targetLangCode, ct);

                ct.ThrowIfCancellationRequested();

                // 5. Show the translation
                if (ReferenceEquals(overlay, _overlayWindow))
                {
                    var isRtl = TranslationService.IsRtlLanguage(targetLangCode);

                    // Borrow the region's own colors so the overlay sits inside the game's look
                    // instead of on top of it. Only when the user asked for it, and never at the
                    // cost of the translation: extraction failing just leaves the default plate.
                    if (_settingsService.Current.RegionMatchesSceneTheme && bitmap != null)
                    {
                        try
                        {
                            overlay.ApplySceneTheme(ColorExtractionService.ExtractFromBitmap(bitmap));
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Scene theme extraction failed; using the default overlay colors");
                        }
                    }

                    overlay.SetProviders(TranslationService.TranslationProviders, currentProvider);
                    overlay.SetTranslation(originalText, translatedText, isRtl,
                        logicalLines.Count, EstimateSourceFontSize(ocrResult),
                        TranslationService.IsRtlLanguage(sourceLanguageTag));
                }

                // 6. Region translations deliberately do NOT go into history: history is a
                //    vocabulary list of looked-up words (Alt+Q), and filling it with whole
                //    paragraphs of screen text buries those. The event still fires for anything
                //    that wants to observe region translations.
                TranslationCompleted?.Invoke(this, new TranslationResult
                {
                    OriginalWord = originalText,
                    ArabicTranslation = translatedText,
                    FullSentence = originalText,
                    SourceLanguage = sourceLanguageTag,
                    TargetLanguage = targetLangCode
                });
            }
            finally
            {
                bitmap?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or dismissed. Whoever cancelled normally took the overlay down already,
            // but if this one is somehow still the current overlay it must not be left sitting
            // on screen in its loading state with the dim behind it.
            if (overlay != null && ReferenceEquals(overlay, _overlayWindow))
                overlay.Dismiss();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Region translation pipeline failed");
            if (overlay == null || ReferenceEquals(overlay, _overlayWindow))
                CloseOverlayWindow();
        }
    }

    /// <summary>Re-runs the translation through the provider the user just picked from the
    /// overlay's chip. The overlay stays up throughout - only its text is swapped.</summary>
    private async void OnApiChanged(object? sender, string newApiId)
    {
        var overlay = _overlayWindow;
        if (overlay == null) return;

        var generation = ++_apiSwitchGeneration;
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            var originalText = overlay.CurrentOriginalText;
            if (string.IsNullOrWhiteSpace(originalText)) return;

            var (targetLangCode, _) = GetTargetLanguage();
            var translatedText = await TranslateRegionTextAsync(newApiId, originalText, _lastSourceLanguage, targetLangCode, ct);

            // A newer pick (or a dismissal) supersedes this one.
            if (generation != _apiSwitchGeneration || !ReferenceEquals(overlay, _overlayWindow)) return;

            overlay.SetTranslationDirect(translatedText);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Region translation provider switch to {Provider} failed", newApiId);
        }
    }

    /// <summary>Translates a whole block of OCR'd text, walking the full provider chain rather
    /// than a single hardcoded fallback - the overlay covers the text it replaces, so coming back
    /// empty leaves the user with less than they started with.</summary>
    private async Task<string> TranslateRegionTextAsync(string providerId, string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        var translated = await _translationService.TranslateTextWithFailoverAsync(providerId, text, sourceLang, ct);
        return string.IsNullOrWhiteSpace(translated) ? text : translated;
    }

    private void ShowNoTextFound(RegionOverlayWindow overlay)
    {
        if (ReferenceEquals(overlay, _overlayWindow))
            overlay.SetTranslation("", "No text found in region");
    }

    /// <summary>
    /// Estimates the font size (physical pixels) of the text OCR just read, so the overlay can
    /// render the translation at the size it's replacing instead of stretching it to fill the
    /// selection. Uses each line's ink span (top of its tallest ascender to the bottom of its
    /// lowest descender) rather than individual word boxes, since a line almost always contains
    /// both while a single word may contain neither.
    /// </summary>
    private static double EstimateSourceFontSize(RecognizedText ocrResult)
    {
        // Measured against Segoe UI text rendered at known sizes and put through this exact
        // pipeline: a mixed-case line's ink height is a very stable 0.77 of the em size
        // (0.76-0.79 from 12px to 36px). See the calibration run in the PR notes.
        const double InkToEmRatio = 0.77;

        var spans = new List<double>();
        foreach (var line in ocrResult.Lines)
        {
            if (line.Words.Count == 0) continue;

            var top = line.Words.Min(w => w.Bounds.Y);
            var bottom = line.Words.Max(w => w.Bounds.Bottom);

            var span = bottom - top;
            if (span > 0) spans.Add(span);
        }

        if (spans.Count == 0) return 0;

        spans.Sort();
        return spans[spans.Count / 2] / InkToEmRatio;
    }

    private (string Code, string Name) GetTargetLanguage()
    {
        var code = _settingsService.Current.TargetLanguage;
        if (string.IsNullOrEmpty(code))
            return ("ar", "Arabic");

        foreach (var lang in TranslationService.TargetLanguages)
        {
            if (lang.Code == code)
                return (lang.Code, lang.Name);
        }
        return ("ar", "Arabic");
    }

    private static async Task<Bitmap?> CaptureRegionAsync(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        return await Task.Run(() =>
        {
            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
            return bitmap;
        });
    }

    private void CloseSelectionWindow()
    {
        if (_selectionWindow != null)
        {
            try { _selectionWindow.Close(); } catch { }
            _selectionWindow = null;
        }
    }

    private void CloseOverlayWindow()
    {
        // Nothing left on screen to cancel once the overlay is gone.
        StopEscapeWatcher();

        if (_overlayWindow != null)
        {
            try { _overlayWindow.Close(); } catch { }
            _overlayWindow = null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        StopEscapeWatcher();
        CloseSelectionWindow();
        CloseOverlayWindow();
        GC.SuppressFinalize(this);
    }
}
