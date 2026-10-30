using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Tarjem.Services;

namespace Tarjem.Models;

public class HistoryEntry
{
    public string Word { get; set; } = string.Empty;
    public string Definition { get; set; } = string.Empty;
    public string ArabicTranslation { get; set; } = string.Empty;
    public string Sentence { get; set; } = string.Empty;
    public string TranslatedSentence { get; set; } = string.Empty;
    public string Phonetic { get; set; } = string.Empty;
    public string PartOfSpeech { get; set; } = string.Empty;
    public string CefrLevel { get; set; } = string.Empty;
    public string[] Synonyms { get; set; } = [];
    public bool IsFromGemini { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>Language the word was read in. Empty on entries saved before 0.6.</summary>
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>Language <see cref="ArabicTranslation"/> is in - which, despite the property's
    /// historical name, is any of the 18 targets. Empty on entries saved before 0.6.</summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>How many times this word has been looked up. Looking a word up again used to
    /// append a second, identical entry; now it moves the existing one to the top and counts it,
    /// because a word you keep looking up is exactly the one worth learning.</summary>
    public int LookupCount { get; set; } = 1;

    /// <summary>When the word was first looked up; <see cref="Timestamp"/> is the latest time.
    /// Null on entries that predate the counter.</summary>
    public DateTime? FirstLookedUp { get; set; }

    /// <summary>The list and detail pane used to hard-code right-to-left for every translation,
    /// which mirrored French and English answers. Entries saved before the target language was
    /// recorded fall back to looking at the script itself.</summary>
    [JsonIgnore]
    public bool IsTranslationRtl => TargetLanguage.Length > 0
        ? TranslationService.IsRtlLanguage(TargetLanguage)
        : ContainsRtlScript(ArabicTranslation) || ContainsRtlScript(TranslatedSentence);

    [JsonIgnore]
    public FlowDirection TranslationFlowDirection =>
        IsTranslationRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    [JsonIgnore]
    public FontFamily TranslationFont => UiFonts.For(IsTranslationRtl);

    /// <summary>"×3" beside a word looked up more than once; empty otherwise.</summary>
    [JsonIgnore]
    public string LookupCountBadge => LookupCount > 1 ? $"×{LookupCount}" : string.Empty;

    private static bool ContainsRtlScript(string text) =>
        text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿');
}
