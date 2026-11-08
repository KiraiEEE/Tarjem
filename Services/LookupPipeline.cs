using System.Drawing;
using Serilog;
using Tarjem.Core.Ocr;
using Tarjem.Models;

namespace Tarjem.Services;

/// <summary>What the user asked for when they pressed a hotkey.</summary>
public enum LookupMode
{
    /// <summary>What does this word mean, and what does the sentence say.</summary>
    Translate,

    /// <summary>What <em>is</em> this - for names no dictionary carries.</summary>
    Explain,
}

/// <summary>The lookup needs the network and there isn't one. Raised before any request is
/// made, so the user hears "offline" at once instead of after every source has timed out.</summary>
public sealed class OfflineException() : Exception("No network connection");

/// <summary>Everything the UI needs to present one finished lookup.</summary>
/// <param name="Result">The answer, or null when there was nothing to show.</param>
/// <param name="WordRectPhysical">Where the word sits on screen, in physical pixels.</param>
public sealed record LookupOutcome(TranslationResult? Result, WpfRect WordRectPhysical);

/// <summary>
/// Capture → OCR → word match → answer, for both word hotkeys.
///
/// Extracted from App.xaml.cs, which was orchestrating this inline alongside tray icons, window
/// lifetime and hotkey registration. Pulling it out is what lets the interesting half - which word
/// did we pick, and what did we ask about it - be reasoned about (and tested) on its own.
/// </summary>
public sealed class LookupPipeline
{
    private readonly OcrService _ocr;
    private readonly TranslationService _translation;
    private readonly SettingsService _settings;
    private readonly HistoryService _history;

    public LookupPipeline(OcrService ocr, TranslationService translation, SettingsService settings, HistoryService history)
    {
        _ocr = ocr;
        _translation = translation;
        _settings = settings;
        _history = history;
    }

    /// <summary>
    /// Everything up to (but not including) asking a provider: capture, recognize, pick the word.
    /// Split out so a caller can put something on screen while the network call is still running.
    /// </summary>
    /// <param name="SourceIsGuess">The language was detected rather than set by the user, so the
    /// translator may overrule it.</param>
    public sealed record WordPeek(WordMatch Match, string LanguageTag, WpfRect WordRectPhysical, bool SourceIsGuess = false)
    {
        public bool Found => Match.Found;

        /// <summary>The raw token, punctuation trimmed - what Alt+E asks about.</summary>
        public string Word => Match.Word?.Text.Trim().Trim(TrimmedPunctuation) ?? "";
    }

    private static readonly char[] TrimmedPunctuation =
    [
        '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '<', '>',
        '\u201C', '\u201D', '\u2018', '\u2019', '\u00AB', '\u00BB', '\u2026',
        '\u060C', '\u061F', '\u061B', '\u06D4',                 // Arabic comma, question mark, semicolon, full stop
        '\u3002', '\u3001', '\u300C', '\u300D', '\u300E', '\u300F', '\uFF01', '\uFF1F', '\uFF0C', '\uFF08', '\uFF09', // CJK
    ];

    /// <summary>Area searched again, at a higher upscale, when the first pass found nothing under
    /// the cursor. Physical pixels; small enough that 3x stays quick.</summary>
    private const int CloseUpWidth = 420;
    private const int CloseUpHeight = 140;
    private const int CloseUpScale = 3;

    /// <summary>Finds the word under the cursor without asking any provider about it.</summary>
    public async Task<WordPeek?> PeekWordAsync(Point cursorPosition, CancellationToken ct)
    {
        var settings = _settings.Current;
        Bitmap? capture = null;

        try
        {
            var (bitmap, offsetX, offsetY) = await _ocr.CaptureAtCursorAsync(new WpfPoint(cursorPosition.X, cursorPosition.Y));
            capture = bitmap;
            ct.ThrowIfCancellationRequested();


            var cursorX = cursorPosition.X - offsetX;
            var cursorY = cursorPosition.Y - offsetY;

            var recognized = await RecognizeAsync(bitmap, settings, ct);
            var match = WordMatch.None;
            var languageTag = settings.SourceLanguage;
            double matchOffsetX = 0, matchOffsetY = 0;

            if (recognized is { Text.IsEmpty: false } found)
            {
                languageTag = found.LanguageTag;
                match = await MatchAsync(found.Text, cursorX, cursorY, languageTag, ct);
            }

            // Nothing under the cursor. The usual reason is text too small for the engine at 2x -
            // game HUDs, dense tables, 9px UI labels - so look again, closer: a small crop around
            // the cursor at a higher upscale. It costs one quick extra pass only on presses that
            // would otherwise have done nothing at all.
            if (!match.Found)
            {
                var closeUp = await CloseUpAsync(bitmap, cursorX, cursorY, languageTag, ct);
                if (closeUp is { } c)
                {
                    match = c.Match;
                    matchOffsetX = c.OffsetX;
                    matchOffsetY = c.OffsetY;
                    Log.Debug("Close-up pass found {Word} after the first pass found nothing", match.Target);
                }
            }

            if (!match.Found) return null;

            var wordRect = new WpfRect(
                offsetX + matchOffsetX + match.Word!.Bounds.X,
                offsetY + matchOffsetY + match.Word.Bounds.Y,
                match.Word.Bounds.Width,
                match.Word.Bounds.Height);

            return new WordPeek(match, languageTag, wordRect, settings.AutoDetectSourceLanguage);
        }
        finally
        {
            capture?.Dispose();
        }
    }

    /// <summary>
    /// Runs a full lookup for whatever is under <paramref name="cursorPosition"/> (physical
    /// pixels). Returns null when there was no word to act on at all - a press over empty space -
    /// which the caller should treat as "do nothing" rather than as an error.
    /// </summary>
    /// <param name="peek">An already-completed <see cref="PeekWordAsync"/>, so a caller that
    /// showed a loading state doesn't capture and recognize the screen a second time.</param>
    /// <param name="onEarly">Called with an early result when the translation is in before the
    /// definition, so the popup can appear without waiting for the slower half.</param>
    public async Task<LookupOutcome?> RunAsync(LookupMode mode, Point cursorPosition, CancellationToken ct, WordPeek? peek = null,
        Action<LookupOutcome>? onEarly = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        peek ??= await PeekWordAsync(cursorPosition, ct);
        var readMs = started.ElapsedMilliseconds;

        if (peek is not { Found: true })
        {
            Log.Information("{Mode}: nothing under the cursor ({Ms} ms)", mode, readMs);
            return null;
        }

        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            Log.Information("{Mode}: offline", mode);
            throw new OfflineException();
        }

        Action<TranslationResult>? early = onEarly == null ? null : r => onEarly(new LookupOutcome(r, peek.WordRectPhysical));

        var result = mode == LookupMode.Explain
            ? await ExplainAsync(peek, ct)
            : await TranslateAsync(peek.Match, peek.LanguageTag, peek.SourceIsGuess, ct, early);

        ct.ThrowIfCancellationRequested();

        // Stages, timings and sources - never the word or the sentence, which stay on the machine.
        Log.Information("{Mode}: read in {ReadMs} ms ({Language}), answered in {TotalMs} ms (translation {Translation}, definition {Definition}{Unavailable})",
            mode, readMs, peek.LanguageTag, started.ElapsedMilliseconds,
            string.IsNullOrEmpty(result?.TranslationProvider) ? "none" : result!.TranslationProvider,
            string.IsNullOrEmpty(result?.DefinitionProvider) ? "none" : result!.DefinitionProvider,
            result?.TranslationUnavailable == true ? ", no translation source answered" : "");

        // Encyclopedia answers aren't vocabulary, so they stay out of history for the same
        // reason region translations do.
        if (result != null && _settings.Current.HistoryEnabled && mode == LookupMode.Translate)
            _history.Add(result);

        return new LookupOutcome(result, peek.WordRectPhysical);
    }

    /// <summary>Word matching walks the bundled 47k frequency list for anything it doesn't
    /// recognize - far too much work to do on the dispatcher thread.</summary>
    private static Task<WordMatch> MatchAsync(RecognizedText text, double x, double y, string languageTag, CancellationToken ct) =>
        Task.Run(() => WordMatcher.FindBestWord(text, x, y, languageTag, OcrService.Segment), ct);

    private async Task<(WordMatch Match, double OffsetX, double OffsetY)?> CloseUpAsync(
        Bitmap capture, double cursorX, double cursorY, string languageTag, CancellationToken ct)
    {
        var width = Math.Min(CloseUpWidth, capture.Width);
        var height = Math.Min(CloseUpHeight, capture.Height);
        var left = (int)Math.Clamp(cursorX - width / 2.0, 0, capture.Width - width);
        var top = (int)Math.Clamp(cursorY - height / 2.0, 0, capture.Height - height);

        using var crop = capture.Clone(new Rectangle(left, top, width, height), capture.PixelFormat);
        var (text, usedTag) = await _ocr.RecognizeWithLanguageAsync(crop, languageTag, CloseUpScale);
        ct.ThrowIfCancellationRequested();
        if (text.IsEmpty) return null;

        var match = await MatchAsync(text, cursorX - left, cursorY - top, usedTag, ct);
        return match.Found ? (match, left, top) : null;
    }

    /// <summary>
    /// Re-answers a finished lookup after the user changed language from the popup itself: same
    /// word, same sentence, the languages now in Settings. The screen is not read again - the dim
    /// and the popup are on top of it - so a change of <em>script</em> needs a fresh press, but a
    /// change of target, or of source within a script, is answered in place.
    /// </summary>
    public async Task<TranslationResult> RetranslateAsync(TranslationResult previous, CancellationToken ct)
    {
        var settings = _settings.Current;
        var guess = settings.AutoDetectSourceLanguage;
        var source = guess ? previous.SourceLanguage : settings.SourceLanguage;

        var scope = CacheScope(previous.FullSentence, source);
        if (_history.GetCached(scope + previous.OriginalWord) is { } cached)
            return cached;

        var result = await _translation.TranslateWord(previous.OriginalWord, previous.FullSentence, source, ct,
            sourceIsGuess: guess);
        ct.ThrowIfCancellationRequested();

        if (!result.TranslationUnavailable)
            _history.CacheResult(scope + result.OriginalWord, result);

        // A new target language is a new vocabulary entry; the same target with a corrected source
        // is the same entry, and counting it as a second lookup would inflate "most looked up".
        if (settings.HistoryEnabled && result.TargetLanguage != previous.TargetLanguage)
            _history.Add(result);

        return result;
    }

    private async Task<(RecognizedText Text, string LanguageTag)?> RecognizeAsync(
        Bitmap bitmap, AppSettings settings, CancellationToken ct)
    {
        if (!settings.AutoDetectSourceLanguage)
        {
            var (text, usedTag) = await _ocr.RecognizeWithLanguageAsync(bitmap, settings.SourceLanguage);
            ct.ThrowIfCancellationRequested();
            return (text, usedTag);
        }

        // Only the languages Windows can actually read here - one engine per script among them.
        var candidates = OcrService.InstalledLanguages();
        if (candidates.Count == 0)
            candidates = [settings.SourceLanguage];

        var detected = await _ocr.DetectAndRecognizeAsync(bitmap, candidates, settings.SourceLanguage, ct);
        ct.ThrowIfCancellationRequested();

        return detected == null ? null : (detected.Value.Result, detected.Value.LanguageTag);
    }

    /// <summary>The Alt+Q path, with the in-memory cache in front of it. That cache is a speed
    /// optimization rather than a record, so it stays on even with history off - "history off"
    /// means nothing is written to disk.</summary>
    private async Task<TranslationResult?> TranslateAsync(WordMatch match, string languageTag, bool sourceIsGuess, CancellationToken ct,
        Action<TranslationResult>? early = null)
    {
        // Both spellings, because which one the lookup will settle on isn't known until it runs -
        // and the cache is keyed on the answer, not on the guess.
        var scope = CacheScope(match.Context, languageTag);

        if (match.WasCorrected && _history.GetCached(scope + match.Raw) is { } cachedRaw)
            return cachedRaw;

        if (_history.GetCached(scope + match.Target) is { } cached)
            return cached;

        var result = await _translation.TranslateWord(match.Target, match.Context, languageTag, ct, match.Raw, sourceIsGuess, early);

        // Keyed on the word actually answered about. Keying it on the correction meant a confirmed
        // reading of "governorate" was filed under "governor", and the next hover on "governor"
        // got the wrong entry back out of the cache.
        //
        // Not when the translation failed: that answer is about the connection, not the word, and
        // caching it served the failure again for the rest of the session.
        if (!result.TranslationUnavailable)
            _history.CacheResult(scope + result.OriginalWord, result);
        return result;
    }

    /// <summary>Asks an AI source for a natural translation of an answer already on screen, and
    /// keeps the better one in the cache. Null when there is nothing to improve.</summary>
    public async Task<TranslationResult?> RefineAsync(TranslationResult shown, CancellationToken ct)
    {
        var refined = await _translation.RefineWithAiAsync(shown, ct);
        if (refined == null) return null;

        _history.CacheResult(CacheScope(shown.FullSentence, shown.SourceLanguage) + refined.OriginalWord, refined);
        if (_settings.Current.HistoryEnabled)
            _history.Add(refined);

        Log.Information("Alt+Q: translation refined by {Provider}", refined.TranslationProvider);
        return refined;
    }

    /// <summary>
    /// Everything besides the word that shapes the answer. The cache used to be keyed on the word
    /// alone, so hovering "bank" in a second sentence brought back the first sentence and its
    /// translation, and changing the target language kept serving answers in the old one until
    /// restart. The section toggles are in here because a hidden section is never fetched - a
    /// result cached with the translation off has no translation to show once it's turned on.
    /// </summary>
    private string CacheScope(string context, string languageTag)
    {
        var s = _settings.Current;
        return string.Join('\u001F',
            languageTag, s.TargetLanguage, s.DefaultDictionaryProvider, s.DefaultTranslationProvider,
            s.ShowDictionarySection, s.ShowTranslationSection, s.ShowInlineWordTranslation,
            s.EncyclopediaFallbackEnabled, context.Trim(), "");
    }

    /// <summary>
    /// The Alt+E path. Deliberately asks about the <em>raw</em> OCR token rather than the
    /// spell-corrected one: correction is built around an English frequency list, and a proper
    /// noun - which is exactly what this feature is for - is by definition not in it, so the
    /// corrected form is the one most likely to have been mangled into a different word.
    /// </summary>
    private async Task<TranslationResult?> ExplainAsync(WordPeek peek, CancellationToken ct)
    {
        var raw = peek.Word;
        if (raw.Length == 0) return null;

        var explained = await _translation.ExplainAsync(raw, peek.LanguageTag, ct);
        if (explained != null) return explained;

        Log.Debug("No encyclopedia entry for {Word}", raw);

        // Say so rather than doing nothing. Alt+Q can afford to stay silent when it finds nothing,
        // because it fires on whatever happens to be under the cursor; Alt+E is a deliberate
        // question about a specific word, and swallowing it reads as a broken hotkey.
        return new TranslationResult
        {
            OriginalWord = raw,
            Meanings = [$"Nothing found for “{raw}” in Wikipedia or Wikidata."],
            IsEncyclopedic = true,
            SourceLanguage = peek.LanguageTag,
            TargetLanguage = _settings.Current.TargetLanguage,
        };
    }
}
