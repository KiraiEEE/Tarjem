using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using Serilog;
using Tarjem.Core.Language;
using Tarjem.Core.Ocr;
using Tarjem.Models;
using Tarjem.Views;

using Color = System.Windows.Media.Color;

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

    /// <summary>The region as captured, kept while its overlay is up so a change of source
    /// language can read it again with the right engine - the screen itself is covered by the
    /// overlay by then. Disposed with the overlay.</summary>
    private Bitmap? _capture;

    /// <summary>What the current overlay is showing, so a provider or language change can redo
    /// just the part that changed.</summary>
    private RegionReading? _reading;

    private int _redoGeneration;
    private EscapeWatcher? _escapeWatcher;

    /// <summary>Fires when a region translation completes.</summary>
    public event EventHandler<TranslationResult>? TranslationCompleted;

    /// <summary>One region, read: its blocks of text, in which language, and how sure we are.</summary>
    private sealed record RegionReading(
        RecognizedText Ocr, IReadOnlyList<TextBlock> Blocks, string LanguageTag, bool SourceIsGuess, string Provider)
    {
        public string Text => string.Join("\n", Blocks.Select(b => b.Text));
    }

    /// <remarks>Deliberately takes no HistoryService: region translations are whole paragraphs of
    /// screen text, and writing those into what is meant to be a vocabulary list of looked-up
    /// words buries the words.</remarks>
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

            ReplaceCapture(bitmap);

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
            overlay.LanguagePicked += OnLanguagePicked;

            var overlayOptions = _settingsService.Current;
            overlay.ApplyOptions(
                dimBehind: overlayOptions.DimBehindOverlays,
                showOriginal: overlayOptions.RegionShowOriginal,
                matchSourceFontSize: overlayOptions.RegionFontSizeMode != "FitRegion");

            overlay.PositionOverRegion(screenRegion);
            overlay.OffersShowOriginal = true;
            overlay.ShowLoading();
            overlay.Show();

            ct.ThrowIfCancellationRequested();

            // 2-4. Read, translate, show.
            var reading = await ReadAsync(bitmap, ct);
            if (reading == null)
            {
                ShowNoTextFound(overlay);
                return;
            }

            await TranslateAndPresentAsync(overlay, reading, ct);
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

    /// <summary>OCR the region in the language Settings asks for, or the detected one, and split it
    /// into blocks. Null when there is no text in it.</summary>
    private async Task<RegionReading?> ReadAsync(Bitmap bitmap, CancellationToken ct)
    {
        var settings = _settingsService.Current;
        RecognizedText? ocr;
        string language;

        if (settings.AutoDetectSourceLanguage)
        {
            var candidates = OcrService.InstalledLanguages();
            if (candidates.Count == 0) candidates = [settings.SourceLanguage];

            var detected = await _ocrService.DetectAndRecognizeAsync(bitmap, candidates, settings.SourceLanguage, ct);
            ct.ThrowIfCancellationRequested();
            if (detected == null) return null;

            ocr = detected.Value.Result;
            language = detected.Value.LanguageTag;
        }
        else
        {
            (ocr, language) = await _ocrService.RecognizeWithLanguageAsync(bitmap, settings.SourceLanguage);
            ct.ThrowIfCancellationRequested();
        }

        if (ocr == null || ocr.Lines.Count == 0) return null;

        // Rejoin sentences that only wrapped on screen. Sent row by row, a sentence split across
        // three rows was translated as three fragments, each forced into the source's clause
        // order - see TextReflow.
        var blocks = TextReflow.LogicalBlocks(ocr);
        if (blocks.Count == 0 || blocks.All(b => string.IsNullOrWhiteSpace(b.Text))) return null;

        return new RegionReading(ocr, blocks, language, settings.AutoDetectSourceLanguage,
            _translationService.ResolveRegionProvider());
    }

    /// <summary>Translates a reading with its provider and puts the result on the overlay.</summary>
    private async Task TranslateAndPresentAsync(RegionOverlayWindow overlay, RegionReading reading, CancellationToken ct)
    {
        var translated = await _translationService.TranslateBlockAsync(
            reading.Provider, reading.Text, reading.LanguageTag, reading.SourceIsGuess, ct);
        ct.ThrowIfCancellationRequested();

        if (!ReferenceEquals(overlay, _overlayWindow)) return;

        // The translator saw whole sentences; when it says the text is in another language than
        // OCR guessed, the label on the chip follows it.
        if (reading.SourceIsGuess && translated?.DetectedLanguage is { } detected &&
            LanguageCatalog.Find(detected)?.Code is { } detectedCode && detectedCode != LanguageCatalog.Find(reading.LanguageTag)?.Code)
            reading = reading with { LanguageTag = detectedCode };

        _reading = reading;
        Present(overlay, reading, translated);

        TranslationCompleted?.Invoke(this, new TranslationResult
        {
            OriginalWord = reading.Text,
            ArabicTranslation = translated?.Text ?? reading.Text,
            FullSentence = reading.Text,
            SourceLanguage = reading.LanguageTag,
            TargetLanguage = _settingsService.Current.TargetLanguage,
        });
    }

    /// <summary>
    /// Lays the translation out. In place when that is the setting and the translation came back
    /// with one line per block - the translators are sent the blocks one per line and nearly
    /// always keep that shape, which is what lets each line go back where it came from. When the
    /// shape didn't survive, or nothing translated, the panel shows it instead: a whole translation
    /// on a plate beats a misaligned one.
    /// </summary>
    private void Present(RegionOverlayWindow overlay, RegionReading reading, TranslationService.BlockTranslation? translated)
    {
        var settings = _settingsService.Current;
        var target = settings.TargetLanguage;
        var isRtl = TranslationService.IsRtlLanguage(target);

        // Credit the source that actually answered - after a failover, not the one picked.
        overlay.SetProviders(TranslationService.TranslationProviders, translated?.ProviderId ?? reading.Provider);
        overlay.SetLanguages(reading.LanguageTag, settings.AutoDetectSourceLanguage, target,
            OcrService.InstalledLanguages(), LanguageChoice.RecentTargets(settings));

        var text = translated?.Text ?? reading.Text;
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var inPlace = settings.RegionDisplayMode != "Panel"
                      && translated != null
                      && lines.Count == reading.Blocks.Count
                      && _capture != null;

        if (inPlace)
        {
            try
            {
                overlay.SetBlocks(BuildBlocks(reading, lines), reading.Text, text, isRtl);
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "In-place layout failed; showing the panel instead");
            }
        }
        else if (settings.RegionDisplayMode != "Panel" && translated != null)
        {
            Log.Debug("Translation came back with {Lines} lines for {Blocks} blocks; using the panel",
                lines.Count, reading.Blocks.Count);
        }

        // Borrow the region's own colors so the panel sits inside the game's look instead of on
        // top of it. Only when the user asked for it, and never at the cost of the translation.
        if (settings.RegionMatchesSceneTheme && _capture != null)
        {
            try
            {
                overlay.ApplySceneTheme(ColorExtractionService.ExtractFromBitmap(_capture));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Scene theme extraction failed; using the default overlay colors");
            }
        }

        overlay.SetTranslation(reading.Text, text, isRtl,
            reading.Blocks.Count, EstimateSourceFontSize(reading.Ocr),
            TranslationService.IsRtlLanguage(reading.LanguageTag));
    }

    /// <summary>Pairs each block with its translated line and the colours it was drawn in.</summary>
    private List<RegionBlock> BuildBlocks(RegionReading reading, IReadOnlyList<string> lines)
    {
        var capture = _capture!;
        var rect = new Rectangle(0, 0, capture.Width, capture.Height);
        var data = capture.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels;
        int stride;
        try
        {
            stride = Math.Abs(data.Stride);
            pixels = new byte[stride * capture.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            capture.UnlockBits(data);
        }

        var blocks = new List<RegionBlock>(reading.Blocks.Count);
        for (var i = 0; i < reading.Blocks.Count; i++)
        {
            var block = reading.Blocks[i];
            var ring = (int)Math.Ceiling(Math.Max(2, block.RowHeight * 0.3));
            var palette = BlockColors.Sample(pixels, capture.Width, capture.Height, stride, block.Bounds, ring);

            blocks.Add(new RegionBlock(
                new Rect(block.Bounds.X, block.Bounds.Y, block.Bounds.Width, block.Bounds.Height),
                lines[i],
                ToColor(palette.Background),
                ToColor(palette.Foreground),
                InkMetrics.FontSizeFromInk(block.RowHeight, block.Text),
                block.LineIndices.Count));
        }
        return blocks;
    }

    private static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    /// <summary>Re-runs the translation through the provider the user just picked from the
    /// overlay's chip. The overlay stays up throughout - only its text is swapped.</summary>
    private async void OnApiChanged(object? sender, string newApiId)
    {
        var overlay = _overlayWindow;
        if (overlay == null || _reading == null) return;

        var generation = ++_redoGeneration;
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            var reading = _reading with { Provider = newApiId };
            var translated = await _translationService.TranslateBlockAsync(
                newApiId, reading.Text, reading.LanguageTag, reading.SourceIsGuess, ct);

            // A newer pick (or a dismissal) supersedes this one.
            if (generation != _redoGeneration || !ReferenceEquals(overlay, _overlayWindow)) return;

            _reading = reading;
            Present(overlay, reading, translated);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Region translation provider switch to {Provider} failed", newApiId);
        }
    }

    /// <summary>
    /// A language picked on the overlay. It becomes the default, like a pick anywhere else. A new
    /// target only needs the same text translated again; a new source means the region has to be
    /// read again with that language's engine - Japanese read by the English engine is not text
    /// any translator can rescue - which is what the kept capture is for.
    /// </summary>
    private async void OnLanguagePicked(object? sender, LanguagePick pick)
    {
        var overlay = _overlayWindow;
        if (overlay == null) return;
        if (!LanguageChoice.Apply(_settingsService, pick)) return;

        var generation = ++_redoGeneration;
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            overlay.ShowLoading();

            RegionReading? reading = _reading;
            if (pick.Source != null || reading == null)
            {
                if (_capture == null) return;
                reading = await ReadAsync(_capture, ct);
            }
            else
            {
                reading = reading with { Provider = _translationService.ResolveRegionProvider() };
            }

            if (generation != _redoGeneration || !ReferenceEquals(overlay, _overlayWindow)) return;

            if (reading == null)
            {
                ShowNoTextFound(overlay);
                return;
            }

            await TranslateAndPresentAsync(overlay, reading, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Region re-translation after a language change failed");
        }
    }

    private void ShowNoTextFound(RegionOverlayWindow overlay)
    {
        if (!ReferenceEquals(overlay, _overlayWindow)) return;

        var settings = _settingsService.Current;
        overlay.SetLanguages(settings.SourceLanguage, settings.AutoDetectSourceLanguage, settings.TargetLanguage,
            OcrService.InstalledLanguages(), LanguageChoice.RecentTargets(settings));

        // Name the likely cause. With a fixed source language, the usual one is that the region
        // is in another script - which the language chip right above this message can fix.
        var hint = settings.AutoDetectSourceLanguage
            ? "No text found in this region."
            : $"No {LanguageCatalog.NameOf(settings.SourceLanguage)} text found. If it's in another language, pick it above.";
        overlay.SetTranslation("", hint);
    }

    /// <summary>
    /// Estimates the font size (physical pixels) of the text OCR just read, so the panel can
    /// render the translation at the size it's replacing instead of stretching it to fill the
    /// selection. Uses each line's ink span (top of its tallest ascender to the bottom of its
    /// lowest descender) rather than individual word boxes, since a line almost always contains
    /// both while a single word may contain neither.
    /// </summary>
    private static double EstimateSourceFontSize(RecognizedText ocrResult)
    {
        // Measured against Segoe UI text rendered at known sizes and put through this exact
        // pipeline: a mixed-case line's ink height is a very stable 0.77 of the em size
        // (0.76-0.79 from 12px to 36px).
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

    private void ReplaceCapture(Bitmap? bitmap)
    {
        if (ReferenceEquals(_capture, bitmap)) return;
        _capture?.Dispose();
        _capture = bitmap;
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

        _reading = null;
        ReplaceCapture(null);
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
