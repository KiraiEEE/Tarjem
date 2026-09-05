using System.IO;
using System.Text.Json;
using Serilog;
using Tarjem.Models;

namespace Tarjem.Services;

public class HistoryService
{
    // See the note in SettingsService: a property so a redirected AppPaths is honoured.
    private static string HistoryPath => AppPaths.HistoryFile;

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

    public bool IsCached(string word)
    {
        return _cache.ContainsKey(word.ToLower().Trim());
    }

    public TranslationResult? GetCached(string word)
    {
        return _cache.TryGetValue(word.ToLower().Trim(), out var result) ? result : null;
    }

    public void CacheResult(string word, TranslationResult result)
    {
        var key = word.ToLower().Trim();
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
            Timestamp = DateTime.Now
        };

        _entries.Insert(0, entry);
        if (_entries.Count > 500) _entries.RemoveAt(_entries.Count - 1);
        Save();
    }

    public List<HistoryEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _entries.ToList();
        var q = query.ToLower().Trim();
        return _entries.Where(e =>
            e.Word.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.Definition.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.ArabicTranslation.Contains(q, StringComparison.OrdinalIgnoreCase))
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
            File.WriteAllText(HistoryPath, json);
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
                if (loaded != null) _entries.AddRange(loaded);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load history from {Path}", HistoryPath);
        }
    }
}
