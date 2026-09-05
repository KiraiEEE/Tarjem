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
}
