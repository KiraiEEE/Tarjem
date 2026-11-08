using System.IO;
using System.Text.Json;
using Serilog;
using Tarjem.Models;

namespace Tarjem.Services;

public class HistoryService
{
    // See the note in SettingsService: a property so a redirected AppPaths is honoured.
    private static string HistoryPath => AppPaths.HistoryFile;

    private const int MaxEntries = 500;

    private readonly List<HistoryEntry> _entries = new();
    private readonly Dictionary<string, TranslationResult> _cache = new();

    // Persisting history used to run synchronously on the dispatcher thread, inline in
    // Add() - which App.OnHotkeyPressed calls *before* showing the popup, so every single
    // lookup paid for a blocking disk write (500 entries, WriteIndented) as part of its
    // perceived latency. Save() now snapshots in-memory state synchronously (cheap, and
    // avoids a torn read if _entries mutates again before the write happens) and hands the
    // actual write to a background task chain: chaining (rather than a bare Task.Run per
    // call) keeps writes in call order even if two saves are requested close together,
    // without needing a lock around the whole save path.
    private Task _pendingSave = Task.CompletedTask;
    private readonly object _saveChainLock = new();

    /// <summary>How many looked-up words the in-memory result cache holds before it starts
    /// evicting. It exists to make re-hovering the same word instant, not to be a record, so it
    /// doesn't need to be unbounded - and unbounded is what it was, growing for the entire
    /// lifetime of a process that is designed to sit in the tray for days.</summary>
    private const int MaxCachedResults = 400;

    private readonly Queue<string> _cacheOrder = new();

    public IReadOnlyList<HistoryEntry> Entries => _entries.AsReadOnly();

    public HistoryService()
    {
        Load();
    }

    public TranslationResult? GetCached(string key)
    {
        return _cache.TryGetValue(NormalizeKey(key), out var result) ? result : null;
    }

    public void CacheResult(string key, TranslationResult result)
    {
        key = NormalizeKey(key);
        if (_cache.ContainsKey(key))
        {
            _cache[key] = result;
            return;
        }

        _cache[key] = result;
        _cacheOrder.Enqueue(key);

        while (_cacheOrder.Count > MaxCachedResults)
            _cache.Remove(_cacheOrder.Dequeue());
    }

    // Invariant, not the current culture: under Turkish "I".ToLower() is a dotless "ı", which
    // made "IMAGE" and "image" two different words to the cache and to the history.
    private static string NormalizeKey(string key) => key.Trim().ToLowerInvariant();

    public void Add(TranslationResult result)
    {
        var entry = new HistoryEntry
        {
            Word = result.OriginalWord,
            Definition = result.Meanings.FirstOrDefault() ?? "",
            ArabicTranslation = result.ArabicTranslation,
            Sentence = result.FullSentence,
            TranslatedSentence = result.TranslatedSentence,
            Phonetic = result.Phonetic,
            PartOfSpeech = result.PartOfSpeech,
            CefrLevel = result.CefrLevel,
            Synonyms = result.Synonyms,
            IsFromGemini = result.IsFromGemini,
            SourceLanguage = result.SourceLanguage,
            TargetLanguage = result.TargetLanguage,
            Timestamp = DateTime.Now
        };

        // Looking the same word up again used to append a duplicate, so a word hovered ten times
        // filled ten rows - and pushed ten other words out of the 500-entry cap. Fold it into the
        // existing entry instead: newest answer and context, but the count and first-seen date
        // carry over, and a field the new answer lacks keeps what the old one had.
        var key = NormalizeKey(entry.Word);
        var existing = _entries.FindIndex(e =>
            NormalizeKey(e.Word) == key &&
            (e.TargetLanguage.Length == 0 || e.TargetLanguage == entry.TargetLanguage));

        if (existing >= 0)
        {
            var previous = _entries[existing];
            _entries.RemoveAt(existing);
            MergeInto(entry, previous);
        }

        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
        Save();
    }

    // ── Review ──

    /// <summary>New words a session introduces at most, so a long history doesn't turn the first
    /// review into a few hundred cards.</summary>
    public const int NewWordsPerSession = 20;

    /// <summary>Words worth reviewing: ones looked up with an actual translation or definition,
    /// not encyclopedia lines or failed lookups.</summary>
    private static bool IsReviewable(HistoryEntry e) =>
        e.Word.Length > 0 &&
        (e.ArabicTranslation.Length > 0 || (e.Definition.Length > 0 && e.Definition != "No definition found"));

    /// <summary>The words due now: those already in review whose time has come (most overdue
    /// first), then new ones (most looked-up first - the words the user keeps forgetting), capped
    /// at <see cref="NewWordsPerSession"/>.</summary>
    public List<HistoryEntry> DueForReview(DateTime now)
    {
        var reviewable = _entries.Where(IsReviewable).ToList();

        var due = reviewable
            .Where(e => e.ReviewCount > 0 && e.ReviewDue <= now)
            .OrderBy(e => e.ReviewDue)
            .ToList();

        due.AddRange(reviewable
            .Where(e => e.ReviewCount == 0)
            .OrderByDescending(e => e.LookupCount)
            .ThenByDescending(e => e.Timestamp)
            .Take(NewWordsPerSession));

        return due;
    }

    public int DueCount(DateTime now) => DueForReview(now).Count;

    /// <summary>Records how a review went and schedules the word's next one.</summary>
    public void RecordReview(HistoryEntry entry, Tarjem.Core.Learning.ReviewGrade grade, DateTime now)
    {
        entry.Review = Tarjem.Core.Learning.ReviewScheduler.Next(entry.Review, grade, now);
        Save();
    }

    private static void MergeInto(HistoryEntry entry, HistoryEntry previous)
    {
        // Looking a word up again doesn't reset what review has learned about it.
        entry.ReviewDue = previous.ReviewDue;
        entry.ReviewIntervalDays = previous.ReviewIntervalDays;
        entry.ReviewStability = previous.ReviewStability;
        entry.ReviewDifficulty = previous.ReviewDifficulty;
        entry.ReviewLast = previous.ReviewLast;
        entry.ReviewCount = previous.ReviewCount;
        entry.ReviewLapses = previous.ReviewLapses;

        entry.LookupCount = Math.Max(1, previous.LookupCount) + 1;
        entry.FirstLookedUp = previous.FirstLookedUp ?? previous.Timestamp;

        if (entry.Definition.Length == 0) entry.Definition = previous.Definition;
        if (entry.ArabicTranslation.Length == 0) entry.ArabicTranslation = previous.ArabicTranslation;
        if (entry.Phonetic.Length == 0) entry.Phonetic = previous.Phonetic;
        if (entry.PartOfSpeech.Length == 0) entry.PartOfSpeech = previous.PartOfSpeech;
        if (entry.CefrLevel.Length == 0) entry.CefrLevel = previous.CefrLevel;
        if (entry.Synonyms.Length == 0) entry.Synonyms = previous.Synonyms;

        if (entry.Sentence.Length == 0)
        {
            entry.Sentence = previous.Sentence;
            entry.TranslatedSentence = previous.TranslatedSentence;
        }
    }

    public List<HistoryEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _entries.ToList();
        var q = query.Trim();
        return _entries.Where(e =>
            e.Word.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.Definition.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.ArabicTranslation.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.Sentence.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void Remove(HistoryEntry entry)
    {
        _entries.Remove(entry);
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    /// <summary>Waits for queued writes to reach the disk. Called on exit: the writes run on the
    /// thread pool, and the process ending under one of them dropped the last lookups of the
    /// session.</summary>
    public void Flush(TimeSpan timeout)
    {
        Task pending;
        lock (_saveChainLock) pending = _pendingSave;

        try { pending.Wait(timeout); }
        catch (Exception ex) { Log.Warning(ex, "History flush failed"); }
    }

    private void Save()
    {
        var snapshot = _entries.ToList();

        lock (_saveChainLock)
        {
            _pendingSave = _pendingSave.ContinueWith(_ => WriteToDisk(snapshot), TaskScheduler.Default);
        }
    }

    private void WriteToDisk(List<HistoryEntry> snapshot)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(HistoryPath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save history to {Path}", HistoryPath);
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
                if (loaded != null) _entries.AddRange(loaded.Where(e => e != null));
            }
        }
        catch (Exception ex)
        {
            // The next save would otherwise overwrite the unreadable file with an empty list,
            // turning one bad byte into every lookup the user ever made being gone.
            Log.Warning(ex, "Failed to load history from {Path}", HistoryPath);
            AtomicFile.KeepCorruptCopy(HistoryPath);
        }
    }
}
