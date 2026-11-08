namespace Tarjem.Models;

public class TranslationResult
{
    public string OriginalWord { get; set; } = string.Empty;
    public string[] Meanings { get; set; } = [];
    public string Phonetic { get; set; } = string.Empty;
    public string PartOfSpeech { get; set; } = string.Empty;
    public string CefrLevel { get; set; } = string.Empty;
    public string[] Synonyms { get; set; } = [];
    public string ArabicTranslation { get; set; } = string.Empty;
    public string ArabicWord { get; set; } = string.Empty;
    public string FullSentence { get; set; } = string.Empty;
    public string TranslatedSentence { get; set; } = string.Empty;

    /// <summary>Start index (in <see cref="TranslatedSentence"/>) of the span that translates
    /// <see cref="OriginalWord"/>, as marked by Gemini itself inside the sentence translation.
    /// -1 when no marker was found (caller should fall back to matching <see cref="ArabicWord"/>).</summary>
    public int TranslatedSentenceHighlightStart { get; set; } = -1;
    public int TranslatedSentenceHighlightLength { get; set; }

    /// <summary>True if either section came from Gemini - drives the single "AI" star badge
    /// used in history. The popup itself uses the two more precise flags below, since with
    /// per-section default providers the definition and translation can come from different
    /// sources in the same result.</summary>
    public bool IsFromGemini { get; set; }
    public bool IsDefinitionFromGemini { get; set; }
    public bool IsTranslationFromGemini { get; set; }
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "ar";

    /// <summary>Which source actually answered each section. After a failover this is not the
    /// source configured in Settings, and the popup says so rather than misattributing the
    /// result to a provider that was down at the time.</summary>
    public string DefinitionProvider { get; set; } = string.Empty;
    public string TranslationProvider { get; set; } = string.Empty;

    /// <summary>True when <see cref="Meanings"/> came from an encyclopedia rather than a
    /// dictionary - i.e. the word is a name, and this says what it *is* rather than what it
    /// means. The popup labels it differently, since "NVIDIA is a technology company" is not a
    /// definition and shouldn't be dressed as one.</summary>
    public bool IsEncyclopedic { get; set; }

    /// <summary>Attribution link for sources that require one (Wikipedia, Wiktionary).</summary>
    public string? SourceUrl { get; set; }

    /// <summary>A translation was wanted and no source could give one - offline, or every source
    /// failing. Shown as such, and never cached: the same word looked up a minute later, once the
    /// connection is back, has to be asked again rather than served this.</summary>
    public bool TranslationUnavailable { get; set; }

    /// <summary>The definition is still being fetched; this result is the early half, shown so the
    /// popup appears as soon as the translation is in.</summary>
    public bool DefinitionPending { get; set; }

    /// <summary>The translation was upgraded by an AI source after the popup first appeared.</summary>
    public bool IsRefined { get; set; }

    /// <summary>A shallow copy, for building the early and refined versions of one answer.</summary>
    public TranslationResult Clone() => (TranslationResult)MemberwiseClone();
}
