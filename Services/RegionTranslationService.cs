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

    /// <summary>True while a region is being translated live. Alt+Q leaves a live region alone:
    /// someone following a game's subtitles still wants to look up a word without losing them.</summary>
    public bool IsLive => _liveTimer != null;

    /// <summary>The selected region, in physical pixels - what live mode keeps watching.</summary>
    private Rect _region;

    // ── Live mode state ──
    private System.Windows.Threading.DispatcherTimer? _liveTimer;
    private LiveBarWindow? _liveBar;
    private byte[]? _liveShownSignature;
    private byte[]? _livePendingSignature;
    private bool _livePaused;
    private bool _liveBusy;
    private bool _liveShowingOriginal;

    /// <summary>The last few lines live mode translated, oldest first - the conversation so far,
    /// handed to the AI with each new line so speakers, names and terms stay consistent.</summary>
    private readonly List<(string Source, string Translation)> _liveHistory = new();
    private const int LiveContextLines = 4;

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
            overlay.LiveRequested += (_, _) => StartLive(overlay);
            overlay.OffersLive = true;
            _region = screenRegion;

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

        IReadOnlyList<string>? candidates = null;
        if (settings.AutoDetectSourceLanguage)
        {
            candidates = OcrService.InstalledLanguages();
            if (candidates.Count == 0) candidates = [settings.SourceLanguage];
        }

        var read = await _ocrService.RecognizeAdaptiveAsync(bitmap, settings.SourceLanguage, candidates, ct);
        if (read == null) return null;
        (ocr, language) = read.Value;

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
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            Log.Information("Alt+W: offline");
            ShowProblem(overlay, reading, "You're offline. Tarjem needs a connection to translate.");
            return;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var context = IsLive && _liveHistory.Count > 0 ? _liveHistory.ToList() : null;
        var translated = await _translationService.TranslateBlockAsync(
            reading.Provider, reading.Text, reading.LanguageTag, reading.SourceIsGuess, ct, context);

        if (IsLive && translated != null)
        {
            _liveHistory.Add((reading.Text, translated.Text));
            if (_liveHistory.Count > LiveContextLines) _liveHistory.RemoveAt(0);
        }
        ct.ThrowIfCancellationRequested();

        if (!ReferenceEquals(overlay, _overlayWindow)) return;

        Log.Information("Alt+W: {Blocks} blocks ({Language}), {Outcome} in {Ms} ms",
            reading.Blocks.Count, reading.LanguageTag,
            translated == null ? "no source answered" : $"translated by {translated.ProviderId}",
            started.ElapsedMilliseconds);

        if (translated == null)
        {
            // Showing the original text here, as this used to, looked like a translation that
            // happened to change nothing.
            ShowProblem(overlay, reading,
                "Couldn't translate this right now - no translation service answered. Check your connection, or pick another source from the arrow above.");
            return;
        }

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

        // One block, several lines back: a model breaking a long line in two. With a single block
        // there is no doubt where they belong, so join them rather than give up on the layout.
        if (reading.Blocks.Count == 1 && lines.Count > 1)
            lines = [string.Join(" ", lines)];

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

    /// <summary>Says what went wrong in the overlay itself, keeping the reading so that picking
    /// another source or language from the chips above retries it.</summary>
    private void ShowProblem(RegionOverlayWindow overlay, RegionReading reading, string message)
    {
        if (!ReferenceEquals(overlay, _overlayWindow)) return;

        _reading = reading;
        var settings = _settingsService.Current;
        overlay.SetProviders(TranslationService.TranslationProviders, reading.Provider);
        overlay.SetLanguages(reading.LanguageTag, settings.AutoDetectSourceLanguage, settings.TargetLanguage,
            OcrService.InstalledLanguages(), LanguageChoice.RecentTargets(settings));
        overlay.SetTranslation(reading.Text, message);
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

    // ===================== Live mode =====================

    /// <summary>How often a live region is looked at. Each look is a tiny thumbnail comparison;
    /// OCR and translation only run when the text has actually changed.</summary>
    private static readonly TimeSpan LiveInterval = TimeSpan.FromMilliseconds(450);

    /// <summary>Thumbnail the change detector compares, in cells. Coarse on purpose: a caret
    /// blinking or a particle drifting past must not count as new dialogue; a new line does.</summary>
    private const int SignatureWidth = 48;
    private const int SignatureHeight = 12;

    /// <summary>
    /// Keeps translating the selected region as its text changes - dialogue boxes, subtitles, a
    /// quest log - so a game can be played in another language without pressing anything per line.
    ///
    /// The loop is built to cost nothing while nothing happens: every tick takes a 48x12 thumbnail
    /// of the region and compares it with the last one. Only when the picture has changed, and then
    /// held still for one more tick (text that types itself out letter by letter is translated
    /// once, finished, not twelve times), is it read and translated - and a line already
    /// translated comes straight back out of the cache.
    /// </summary>
    private void StartLive(RegionOverlayWindow overlay)
    {
        if (!ReferenceEquals(overlay, _overlayWindow) || !overlay.CanGoLive || _liveTimer != null) return;

        overlay.EnterLive();
        _liveHistory.Clear();
        if (_reading != null && !string.IsNullOrWhiteSpace(overlay.CurrentTranslatedText))
            _liveHistory.Add((_reading.Text, overlay.CurrentTranslatedText));

        // Esc no longer ends it: it's the pause-menu key in most games, and it also closes an
        // Alt+Q popup opened over the subtitles. The bar's stop button, or Alt+W, ends live mode.
        StopEscapeWatcher();
        _livePaused = false;
        _liveShowingOriginal = false;
        _liveShownSignature = _capture != null ? Signature(_capture) : null;
        _livePendingSignature = null;

        _liveBar = new LiveBarWindow();
        _liveBar.PauseToggled += (_, _) =>
        {
            _livePaused = !_livePaused;
            UpdateLiveBar();
        };
        _liveBar.OriginalToggled += (_, _) =>
        {
            if (_overlayWindow != null) _liveShowingOriginal = _overlayWindow.ToggleOriginal();
            UpdateLiveBar();
        };
        _liveBar.StopRequested += (_, _) => Cancel(immediate: true);
        _liveBar.PlaceBeside(_region);
        UpdateLiveBar();

        _liveTimer = new System.Windows.Threading.DispatcherTimer { Interval = LiveInterval };
        _liveTimer.Tick += async (_, _) => await LiveTickAsync();
        _liveTimer.Start();

        Log.Information("Alt+W: live mode started");
    }

    private void UpdateLiveBar() => _liveBar?.SetState(_livePaused, _liveBusy, _liveShowingOriginal);

    private async Task LiveTickAsync()
    {
        var overlay = _overlayWindow;
        if (overlay == null || !overlay.IsLive || _livePaused || _liveBusy) return;

        Bitmap? frame = null;
        try
        {
            frame = await CaptureRegionAsync((int)_region.Left, (int)_region.Top, (int)_region.Width, (int)_region.Height);
            if (frame == null || !ReferenceEquals(overlay, _overlayWindow)) return;

            var signature = Signature(frame);

            // Unchanged since the translation on screen: nothing to do.
            if (_liveShownSignature != null && Similar(signature, _liveShownSignature))
            {
                _livePendingSignature = null;
                return;
            }

            // Changed, but still moving - wait until it holds still for a tick.
            if (_livePendingSignature == null || !Similar(signature, _livePendingSignature))
            {
                _livePendingSignature = signature;
                return;
            }

            _liveBusy = true;
            UpdateLiveBar();

            var ct = _cts?.Token ?? CancellationToken.None;
            var reading = await ReadAsync(frame, ct);
            if (!ReferenceEquals(overlay, _overlayWindow)) return;

            _liveShownSignature = signature;
            _livePendingSignature = null;

            if (reading == null)
            {
                // The dialogue box closed: show nothing rather than the last line.
                _reading = null;
                overlay.ClearLive();
                return;
            }

            // Same words, moved or redrawn (a box sliding in): keep the translation on screen.
            if (_reading != null && _reading.Text == reading.Text)
                return;

            ReplaceCapture(frame);
            frame = null;
            await TranslateAndPresentAsync(overlay, reading, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Live region update failed");
        }
        finally
        {
            frame?.Dispose();
            _liveBusy = false;
            UpdateLiveBar();
        }
    }

    private void StopLive()
    {
        if (_liveTimer == null && _liveBar == null) return;

        _liveTimer?.Stop();
        _liveTimer = null;
        try { _liveBar?.Close(); } catch { }
        _liveBar = null;
        _liveShownSignature = _livePendingSignature = null;
        _liveHistory.Clear();
        Log.Information("Alt+W: live mode stopped");
    }

    /// <summary>A coarse grayscale thumbnail of the frame, for change detection.</summary>
    private static byte[] Signature(Bitmap frame)
    {
        using var small = new Bitmap(SignatureWidth, SignatureHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(frame, 0, 0, SignatureWidth, SignatureHeight);
        }

        var data = small.LockBits(new Rectangle(0, 0, SignatureWidth, SignatureHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var raw = new byte[Math.Abs(data.Stride) * SignatureHeight];
        Marshal.Copy(data.Scan0, raw, 0, raw.Length);
        small.UnlockBits(data);

        var stride = Math.Abs(data.Stride);
        var gray = new byte[SignatureWidth * SignatureHeight];
        for (var y = 0; y < SignatureHeight; y++)
            for (var x = 0; x < SignatureWidth; x++)
            {
                var i = y * stride + x * 4;
                gray[y * SignatureWidth + x] = (byte)((raw[i] * 11 + raw[i + 1] * 59 + raw[i + 2] * 30) / 100);
            }
        return gray;
    }

    /// <summary>Two thumbnails show the same text: almost every cell within a small tolerance.
    /// A new line of dialogue changes a large share of cells; noise changes a few.</summary>
    private static bool Similar(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 12) changed++;
        return changed <= a.Length / 50;
    }

    private void CloseOverlayWindow()
    {
        // Nothing left on screen to cancel once the overlay is gone.
        StopEscapeWatcher();
        StopLive();

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
