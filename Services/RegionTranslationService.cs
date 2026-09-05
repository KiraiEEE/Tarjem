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

        // Close any existing windows
        CloseSelectionWindow();
        CloseOverlayWindow();

        _selectionWindow = new RegionSelectionWindow();
        _selectionWindow.RegionSelected += async (_, rect) =>
        {
            // Crop from the frozen screenshot before the selection window goes away, so what
            // gets OCR'd is the desktop as it looked when the user drew the box.
            var regionBitmap = _selectionWindow?.CropRegion(rect);
            CloseSelectionWindow();
            await ProcessRegionAsync(rect, regionBitmap, ct);
        };
        _selectionWindow.SelectionCancelled += (_, _) =>
        {
            StopEscapeWatcher();
            CloseSelectionWindow();
        };

        // Escape cancels. The selection window is WS_EX_NOACTIVATE so it never gets keyboard
        // input of its own - the watcher reads the key state globally instead.
        StartEscapeWatcher(Cancel);

        _selectionWindow.ShowWithScreenshot();
    }

    /// <summary>Cancels whatever part of the region flow is currently on screen.</summary>
    public void Cancel()
    {
        StopEscapeWatcher();
        _cts?.Cancel();
        CloseSelectionWindow();

        if (_overlayWindow != null)
        {
            // Let the overlay fade out; its Dismissed handler clears the reference.
            _overlayWindow.Dismiss();
        }
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
        try
        {
            // Show overlay in loading state
            _overlayWindow = new RegionOverlayWindow();
            _overlayWindow.Dismissed += (_, _) =>
            {
                // Clicking away mid-translation should stop the lookup too, not just hide it -
                // same contract as the Alt+Q overlay.
                _cts?.Cancel();
                CloseOverlayWindow();
            };
            _overlayWindow.ApiChanged += OnApiChanged;

            var overlayOptions = _settingsService.Current;
            _overlayWindow.ApplyOptions(
                dimBehind: overlayOptions.DimBehindOverlays,
                showOriginal: overlayOptions.RegionShowOriginal,
                matchSourceFontSize: overlayOptions.RegionFontSizeMode != "FitRegion");

            _overlayWindow.PositionOverRegion(screenRegion);
            _overlayWindow.ShowLoading();
            _overlayWindow.Show();

            ct.ThrowIfCancellationRequested();

            // 1. The region normally comes pre-cropped from the selection window's frozen
            //    screenshot; only fall back to a live grab if that didn't work out.
            var bitmap = capturedRegion ?? await CaptureRegionAsync(
                (int)screenRegion.Left,
                (int)screenRegion.Top,
                (int)screenRegion.Width,
                (int)screenRegion.Height);

            if (bitmap == null || ct.IsCancellationRequested)
            {
                bitmap?.Dispose();
                CloseOverlayWindow();
                return;
            }

            try
            {
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

                    var detected = await _ocrService.DetectAndRecognizeAsync(bitmap, candidates, ct);
                    ct.ThrowIfCancellationRequested();

                    if (detected == null)
                    {
                        ShowNoTextFound();
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
                    ShowNoTextFound();
                    return;
                }

                // 3. Build full text from all OCR lines
                var originalText = ocrResult.AllText;
                if (string.IsNullOrWhiteSpace(originalText))
                {
                    ShowNoTextFound();
                    return;
                }

                ct.ThrowIfCancellationRequested();

                // 4. Translate the combined text with whichever provider is configured as the
                //    default, so the chip on the overlay names the source actually used.
                var (targetLangCode, _) = GetTargetLanguage();
                _lastSourceLanguage = sourceLanguageTag;
                _lastOcrLineCount = ocrResult.Lines.Count;

                var currentProvider = string.IsNullOrEmpty(_settingsService.Current.DefaultTranslationProvider)
                    ? "google"
                    : _settingsService.Current.DefaultTranslationProvider;

                var translatedText = await TranslateRegionTextAsync(currentProvider, originalText, sourceLanguageTag, targetLangCode, ct);

                ct.ThrowIfCancellationRequested();

                // 5. Show the translation
                if (_overlayWindow != null)
                {
                    var isRtl = TranslationService.IsRtlLanguage(targetLangCode);

                    // Borrow the region's own colors so the overlay sits inside the game's look
                    // instead of on top of it. Only when the user asked for it, and never at the
                    // cost of the translation: extraction failing just leaves the default plate.
                    if (_settingsService.Current.RegionMatchesSceneTheme && bitmap != null)
                    {
                        try
                        {
                            _overlayWindow.ApplySceneTheme(ColorExtractionService.ExtractFromBitmap(bitmap));
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Scene theme extraction failed; using the default overlay colors");
                        }
                    }

                    _overlayWindow.SetProviders(TranslationService.TranslationProviders, currentProvider);
                    _overlayWindow.SetTranslation(originalText, translatedText, isRtl,
                        ocrResult.Lines.Count, EstimateSourceFontSize(ocrResult),
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
            // Superseded or dismissed - clean up silently
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Region translation pipeline failed");
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

    private void ShowNoTextFound()
    {
        _overlayWindow?.SetTranslation("", "No text found in region");
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
