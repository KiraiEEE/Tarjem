using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Tarjem.Core.Language;
using Tarjem.Core.Ocr;

namespace Tarjem.Services;

/// <summary>
/// Screen capture and text recognition. Deciding <em>which</em> recognized word the user meant,
/// and repairing what OCR misread, lives in <see cref="WordMatcher"/> over in Tarjem.Core - this
/// class only produces the <see cref="RecognizedText"/> that feeds it.
/// </summary>
public class OcrService
{
    private readonly Dictionary<string, OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Factor <see cref="PreprocessForOcr"/> upscales by before recognition, by default.
    /// Every coordinate the engine reports is in that larger space and has to be divided back out.</summary>
    public const int DefaultUpscale = 2;

    /// <summary>The engine used when the requested language has no pack installed. English when
    /// there is one, otherwise whatever Windows can read.</summary>
    private readonly OcrEngine _fallbackEngine;

    public OcrService()
    {
        // English is the preferred fallback whenever a configured source language's Windows OCR
        // pack turns out not to be installed. It used to be *required*, which kept the app from
        // starting at all on a Windows install whose only OCR language is, say, Polish or Arabic.
        // Any installed recognizer will do; only having none at all is fatal.
        var english = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (english != null)
            _engines["en"] = english;

        _fallbackEngine = english
            ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null)
            ?? throw new InvalidOperationException("No Windows OCR language is installed.");
    }

    /// <summary>Windows OCR language packs are opt-in (Settings > Time & Language > Language,
    /// "Optional features"), so a language the user picks in our Settings may not actually be
    /// installed. Callers should check this before relying on <see cref="GetOrCreateEngine"/>
    /// succeeding, so they can tell the user why instead of silently falling back.</summary>
    public bool IsLanguageAvailable(string languageTag)
    {
        lock (_engines)
        {
            if (_engines.ContainsKey(languageTag)) return true;
        }

        return IsWindowsLanguagePackInstalled(languageTag);
    }

    /// <summary>Static so Settings UI can warn about a picked language before OCR ever runs,
    /// without needing an <see cref="OcrService"/> instance (it's a plain Windows API query,
    /// not something that benefits from the engine cache).</summary>
    public static bool IsWindowsLanguagePackInstalled(string languageTag)
    {
        try
        {
            return OcrEngine.IsLanguageSupported(new Windows.Globalization.Language(languageTag));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Catalog codes of every language Windows can OCR on this machine right now, straight from
    /// the recognizer list. Auto-detect used to find these by asking about each of the app's
    /// languages in turn - which also meant a pack the app didn't list (Romanian, say) could be
    /// installed and never used.
    /// </summary>
    public static IReadOnlyList<string> InstalledLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => LanguageCatalog.Find(l.LanguageTag)?.Code)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not list the installed OCR languages");
            return Array.Empty<string>();
        }
    }

    private static readonly Dictionary<string, Windows.Data.Text.WordsSegmenter?> Segmenters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Windows word breaker, for languages written without spaces. Returns no tokens when
    /// Windows has no real segmenter for the language (it then resolves to "und" and splits every
    /// character apart, which is worse than not segmenting at all) - Japanese always has one;
    /// Chinese has one when a Chinese language is installed in Windows.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Segment(string text, string languageTag)
    {
        Windows.Data.Text.WordsSegmenter? segmenter;
        lock (Segmenters)
        {
            if (!Segmenters.TryGetValue(languageTag, out segmenter))
            {
                try
                {
                    var tag = LanguageCatalog.BaseTag(languageTag) == "zh"
                        ? (languageTag.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ? "zh-Hant" : "zh-Hans")
                        : languageTag;
                    var candidate = new Windows.Data.Text.WordsSegmenter(tag);
                    segmenter = candidate.ResolvedLanguage == "und" ? null : candidate;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "No word segmenter for {Language}", languageTag);
                    segmenter = null;
                }
                Segmenters[languageTag] = segmenter;
            }
        }

        if (segmenter == null || string.IsNullOrEmpty(text)) return Array.Empty<(int, int)>();

        return segmenter.GetTokens(text)
            .Select(t => ((int)t.SourceTextSegment.StartPosition, (int)t.SourceTextSegment.Length))
            .ToList();
    }

    /// <summary>Builds and caches the engine for <paramref name="languageTag"/> ahead of time, so
    /// the first lookup doesn't pay for it. Silently does nothing if that language has no pack
    /// installed - the lookup path already falls back to English in that case.</summary>
    public void WarmUp(string languageTag) => GetOrCreateEngine(languageTag);

    /// <remarks>Locked because <see cref="WarmUp"/> runs this on a background thread at startup
    /// while a hotkey press could be reaching it from the UI thread at the same moment.</remarks>
    private OcrEngine? GetOrCreateEngine(string languageTag)
    {
        lock (_engines)
        {
            if (_engines.TryGetValue(languageTag, out var cached))
                return cached;

            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(languageTag));
                if (engine != null)
                    _engines[languageTag] = engine;
                return engine;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to create OCR engine for language {LanguageTag}", languageTag);
                return null;
            }
        }
    }

    public async Task<(Bitmap bitmap, int offsetX, int offsetY)> CaptureAtCursorAsync(WpfPoint cursorPos)
    {
        int cx = (int)cursorPos.X;
        int cy = (int)cursorPos.Y;

        // Cursor.Position and CopyFromScreen both work in physical pixels, so we must
        // clamp against the *physical* bounds of whichever monitor the cursor is on
        // (which may sit at a non-zero/negative origin) rather than the primary
        // monitor's DIP-based SystemParameters size. Getting this wrong causes the
        // capture region to drift off the intended text on secondary monitors or
        // scaled displays.
        var screen = System.Windows.Forms.Screen.FromPoint(new Point(cx, cy));
        var bounds = screen.Bounds;

        int width = Math.Min(900, bounds.Width);
        int height = Math.Min(320, bounds.Height);

        int x = cx - width / 2;
        int y = cy - height / 2;

        if (x + width > bounds.Right) x = bounds.Right - width;
        if (y + height > bounds.Bottom) y = bounds.Bottom - height;
        if (x < bounds.Left) x = bounds.Left;
        if (y < bounds.Top) y = bounds.Top;

        var bitmap = await CaptureRegionAsync(x, y, width, height);
        return (bitmap, x, y);
    }

    public async Task<Bitmap> CaptureRegionAsync(int x, int y, int width, int height)
    {
        return await Task.Run(() =>
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return bitmap;
        });
    }

    /// <summary>Runs OCR with a single, known source language - the normal (fast) path. Falls
    /// back to English if the requested language's Windows OCR pack isn't installed, so a stale
    /// setting (e.g. after uninstalling a language pack) degrades instead of throwing.</summary>
    public async Task<RecognizedText?> RecognizeTextAsync(Bitmap bitmap, string languageTag, int upscale = DefaultUpscale) =>
        (await RecognizeWithLanguageAsync(bitmap, languageTag, upscale)).Text;

    /// <summary>As <see cref="RecognizeTextAsync"/>, also reporting which language's engine
    /// actually read it. When the requested pack is missing that is the fallback's language, and
    /// labelling English OCR output "Ukrainian" sent the translator the wrong source.</summary>
    public async Task<(RecognizedText Text, string LanguageTag)> RecognizeWithLanguageAsync(
        Bitmap bitmap, string languageTag, int upscale = DefaultUpscale)
    {
        var engine = GetOrCreateEngine(languageTag);
        var usedTag = languageTag;
        if (engine == null)
        {
            engine = _fallbackEngine;
            usedTag = LanguageCatalog.Find(_fallbackEngine.RecognizerLanguage.LanguageTag)?.Code ?? "en";
        }

        var scale = EffectiveScale(bitmap, upscale);
        using var softwareBitmap = await PrepareSoftwareBitmapAsync(bitmap, scale);
        return (Convert(await engine.RecognizeAsync(softwareBitmap), scale), usedTag);
    }

    /// <summary>
    /// Maps the WinRT result onto the engine-agnostic model everything downstream works against.
    /// Coordinates are halved here to undo the 2x upscale <see cref="PreprocessForOcr"/> applies,
    /// so callers never have to know it happened.
    /// </summary>
    private static RecognizedText Convert(OcrResult result, double upscale)
    {
        var lines = new List<RecognizedLine>(result.Lines.Count);

        for (var lineIndex = 0; lineIndex < result.Lines.Count; lineIndex++)
        {
            var line = result.Lines[lineIndex];
            var words = new List<RecognizedWord>(line.Words.Count);

            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                var box = word.BoundingRect;

                words.Add(new RecognizedWord(
                    word.Text,
                    new TextRect(box.X / upscale, box.Y / upscale, box.Width / upscale, box.Height / upscale),
                    lineIndex,
                    wordIndex));
            }

            // Not the engine's own line.Text: that joins words left to right with spaces, which
            // reverses Arabic sentences and spaces out Chinese and Japanese. See OcrLineText.
            lines.Add(new RecognizedLine(OcrLineText.Compose(words.Select(w => w.Text)), words));
        }

        return new RecognizedText(lines);
    }

    /// <summary>
    /// Auto-detect. Runs one engine per <em>script</em> among the candidates, in parallel, and keeps
    /// the reading that is most convincingly in its engine's own script (see
    /// <see cref="LanguageGuesser.ReadingScore"/>). When several candidates share the winning script
    /// - English, French and Polish are all Latin - the language is then told apart from the text
    /// itself, and only if that points at a different language than the one already run does a
    /// second pass run with its engine, so its own letters (Polish ł, French œ) come out right.
    ///
    /// This used to run every candidate's engine and keep the longest output: one OCR pass per
    /// installed language on every press, and within a script a coin toss over which language the
    /// text was labelled - a label that is sent to the translator as the source language.
    /// </summary>
    /// <param name="preferredLanguageTag">The user's configured source language. It represents its
    /// script in the first pass and wins every undecided call within it.</param>
    public async Task<(RecognizedText Result, string LanguageTag)?> DetectAndRecognizeAsync(
        Bitmap bitmap, IReadOnlyList<string> candidateLanguageTags, string? preferredLanguageTag, CancellationToken ct)
    {
        var available = candidateLanguageTags.Where(tag => GetOrCreateEngine(tag) != null).ToList();
        if (available.Count == 0) return null;

        var scale = EffectiveScale(bitmap, DefaultUpscale);
        using var softwareBitmap = await PrepareSoftwareBitmapAsync(bitmap, scale);

        var groups = available
            .GroupBy(LanguageGuesser.ScriptOf)
            .Select(g => g.ToList())
            .Select(members => (Members: members, Representative: Representative(members, preferredLanguageTag)))
            .ToList();

        var firstPass = await Task.WhenAll(groups.Select(async g =>
            (g.Members, g.Representative, Result: await GetOrCreateEngine(g.Representative)!.RecognizeAsync(softwareBitmap))));
        ct.ThrowIfCancellationRequested();

        var best = firstPass
            .OrderByDescending(r => LanguageGuesser.ReadingScore(r.Result.Text, r.Representative))
            .ThenBy(r => r.Representative == preferredLanguageTag ? 0 : 1)
            .First();

        var languageTag = best.Representative;
        var result = best.Result;

        if (best.Members.Count > 1)
        {
            var guess = LanguageGuesser.GuessWithinScript(result.Text, best.Members, best.Representative);
            if (guess != best.Representative && GetOrCreateEngine(guess) is { } engine)
            {
                result = await engine.RecognizeAsync(softwareBitmap);
                languageTag = guess;
                ct.ThrowIfCancellationRequested();
            }
        }

        return (Convert(result, scale), languageTag);
    }

    /// <summary>The language whose engine reads a script in the first pass: the user's own source
    /// language if it is in this script, else English, else the first in the list.</summary>
    private static string Representative(IReadOnlyList<string> members, string? preferred)
    {
        if (preferred != null && members.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            return members.First(m => string.Equals(m, preferred, StringComparison.OrdinalIgnoreCase));
        return members.FirstOrDefault(m => m == "en") ?? members[0];
    }

    /// <summary>The upscale actually applied: the requested one, reduced as far as needed to keep
    /// the image inside what Windows OCR accepts. A very wide region at 2x was refused outright and
    /// came back as "no text found". Every coordinate is divided by this same number.</summary>
    private static double EffectiveScale(Bitmap bitmap, double requested)
    {
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (longest <= 0) return requested;
        return Math.Min(requested, (OcrEngine.MaxImageDimension - 2) / (double)longest);
    }

    private static async Task<SoftwareBitmap> PrepareSoftwareBitmapAsync(Bitmap bitmap, double upscale)
    {
        // The preprocessing below is a 2x upscale plus three full-frame passes over ~1.2M pixels,
        // one of them a 3x3 convolution in managed code. It used to run synchronously on whatever
        // thread called in - which, because the callers reach here from an awaited continuation on
        // the dispatcher, was the UI thread, freezing the app for the duration of every lookup.
        var encoded = await Task.Run(() =>
        {
            using var processed = PreprocessForOcr(bitmap, upscale);
            using var buffer = new MemoryStream();
            processed.Save(buffer, ImageFormat.Bmp);
            return buffer.ToArray();
        });

        using var stream = new MemoryStream(encoded);
        var randomAccessStream = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private static Bitmap PreprocessForOcr(Bitmap source, double upscale)
    {
        // Screen text is already crisp and high-contrast (unlike photographed
        // documents), so we avoid hard global-threshold binarization here: on
        // anything but a plain background it flattens anti-aliased glyphs into
        // blobs and is a major source of OCR misreads. A modest contrast boost
        // plus a light sharpen is enough for the Windows OCR engine to work well
        // while staying accurate across dark/light/colorful UI backgrounds.
        var scaled = ScaleBitmap(source, upscale);
        var grayscale = ToGrayscale(scaled);
        scaled.Dispose();

        var contrast = AdjustContrast(grayscale, 1.2f);
        grayscale.Dispose();

        var sharpened = Sharpen(contrast);
        contrast.Dispose();

        return sharpened;
    }

    private static Bitmap ScaleBitmap(Bitmap source, double scale)
    {
        var scaled = new Bitmap(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        return scaled;
    }

    private static Bitmap ToGrayscale(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var destData = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        int bytes = Math.Abs(data.Stride) * data.Height;
        var pixels = new byte[bytes];
        var destPixels = new byte[bytes];
        Marshal.Copy(data.Scan0, pixels, 0, bytes);

        for (int i = 0; i < bytes; i += 4)
        {
            byte gray = (byte)(pixels[i] * 0.11 + pixels[i + 1] * 0.59 + pixels[i + 2] * 0.3);
            destPixels[i] = gray;
            destPixels[i + 1] = gray;
            destPixels[i + 2] = gray;
            destPixels[i + 3] = 255;
        }

        Marshal.Copy(destPixels, 0, destData.Scan0, bytes);
        source.UnlockBits(data);
        result.UnlockBits(destData);
        return result;
    }

    private static Bitmap AdjustContrast(Bitmap source, float factor)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var destData = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        int bytes = Math.Abs(data.Stride) * data.Height;
        var pixels = new byte[bytes];
        var destPixels = new byte[bytes];
        Marshal.Copy(data.Scan0, pixels, 0, bytes);

        float intercept = 128f * (1f - factor);
        for (int i = 0; i < bytes; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                float val = pixels[i + c] * factor + intercept;
                destPixels[i + c] = (byte)Math.Clamp(val, 0, 255);
            }
            destPixels[i + 3] = 255;
        }

        Marshal.Copy(destPixels, 0, destData.Scan0, bytes);
        source.UnlockBits(data);
        result.UnlockBits(destData);
        return result;
    }

    private static Bitmap Sharpen(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var destData = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        int w = source.Width, h = source.Height;
        int stride = Math.Abs(data.Stride);
        var pixels = new byte[stride * h];
        var destPixels = new byte[stride * h];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

        float[] kernel = { 0, -1, 0, -1, 5, -1, 0, -1, 0 };

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    float sum = 0;
                    int ki = 0;
                    for (int ky = -1; ky <= 1; ky++)
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            int idx = (y + ky) * stride + (x + kx) * 4 + c;
                            sum += pixels[idx] * kernel[ki++];
                        }
                    destPixels[y * stride + x * 4 + c] = (byte)Math.Clamp(sum, 0, 255);
                }
                destPixels[y * stride + x * 4 + 3] = 255;
            }
        }

        Marshal.Copy(destPixels, 0, destData.Scan0, pixels.Length);
        source.UnlockBits(data);
        result.UnlockBits(destData);
        return result;
    }
}
