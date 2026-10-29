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
    public sealed record WordPeek(WordMatch Match, string LanguageTag, WpfRect WordRectPhysical)
    {
        public bool Found => Match.Found;

        /// <summary>The raw token, punctuation trimmed - what Alt+E asks about.</summary>
        public string Word => Match.Word?.Text.Trim().Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']') ?? "";
    }

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


            var recognized = await RecognizeAsync(bitmap, settings, ct);
            if (recognized == null) return null;

            var (text, languageTag) = recognized.Value;
            if (text.IsEmpty) return null;

            // Word matching walks the bundled 47k frequency list for anything it doesn't
            // recognize - far too much work to do on the dispatcher thread.
            var match = await Task.Run(
                () => WordMatcher.FindBestWord(text, cursorPosition.X - offsetX, cursorPosition.Y - offsetY, languageTag),
                ct);

            if (!match.Found) return null;

            var wordRect = new WpfRect(
                offsetX + match.Word!.Bounds.X,
                offsetY + match.Word.Bounds.Y,
                match.Word.Bounds.Width,
                match.Word.Bounds.Height);

            return new WordPeek(match, languageTag, wordRect);
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
    public async Task<LookupOutcome?> RunAsync(LookupMode mode, Point cursorPosition, CancellationToken ct, WordPeek? peek = null)
    {
        peek ??= await PeekWordAsync(cursorPosition, ct);
        if (peek is not { Found: true }) return null;

        var result = mode == LookupMode.Explain
            ? await ExplainAsync(peek, ct)
            : await TranslateAsync(peek.Match, peek.LanguageTag, ct);

        ct.ThrowIfCancellationRequested();

        // Encyclopedia answers aren't vocabulary, so they stay out of history for the same
        // reason region translations do.
        if (result != null && _settings.Current.HistoryEnabled && mode == LookupMode.Translate)
            _history.Add(result);

        return new LookupOutcome(result, peek.WordRectPhysical);
    }

    private async Task<(RecognizedText Text, string LanguageTag)?> RecognizeAsync(
        Bitmap bitmap, AppSettings settings, CancellationToken ct)
    {
        if (!settings.AutoDetectSourceLanguage)
        {
            var text = await _ocr.RecognizeTextAsync(bitmap, settings.SourceLanguage);
            ct.ThrowIfCancellationRequested();
            return text == null ? null : (text, settings.SourceLanguage);
        }

        // Only race languages that actually have a Windows OCR pack installed - trying all 18
        // regardless would be needlessly slow and most would fail to create an engine anyway.
        var candidates = TranslationService.SourceLanguages
            .Select(l => l.Code)
            .Where(_ocr.IsLanguageAvailable)
            .ToList();

        var detected = await _ocr.DetectAndRecognizeAsync(bitmap, candidates, settings.SourceLanguage, ct);
        ct.ThrowIfCancellationRequested();

        return detected == null ? null : (detected.Value.Result, detected.Value.LanguageTag);
    }

    /// <summary>The Alt+Q path, with the in-memory cache in front of it. That cache is a speed
    /// optimization rather than a record, so it stays on even with history off - "history off"
    /// means nothing is written to disk.</summary>
    private async Task<TranslationResult?> TranslateAsync(WordMatch match, string languageTag, CancellationToken ct)
    {
        // Both spellings, because which one the lookup will settle on isn't known until it runs -
        // and the cache is keyed on the answer, not on the guess.
        if (match.WasCorrected && _history.IsCached(match.Raw))
            return _history.GetCached(match.Raw);

        if (_history.IsCached(match.Target))
            return _history.GetCached(match.Target);

        var result = await _translation.TranslateWord(match.Target, match.Context, languageTag, ct, match.Raw);

        // Keyed on the word actually answered about. Keying it on the correction meant a confirmed
        // reading of "governorate" was filed under "governor", and the next hover on "governor"
        // got the wrong entry back out of the cache.
        _history.CacheResult(result.OriginalWord, result);
        return result;
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
