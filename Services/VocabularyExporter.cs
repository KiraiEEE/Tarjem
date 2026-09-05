using System.IO;
using System.Text;
using Tarjem.Models;

namespace Tarjem.Services;

/// <summary>Which file shape to write.</summary>
public enum ExportFormat
{
    /// <summary>Tab-separated, with the header directives Anki reads to configure the import.
    /// Front is the word, back is the translation plus definition, and the example sentence goes
    /// in its own field.</summary>
    Anki,

    /// <summary>Plain CSV with a header row, for spreadsheets and everything else.</summary>
    Csv,
}

/// <summary>
/// Writes the lookup history out as a vocabulary list.
///
/// The history already holds exactly what a flashcard needs - word, translation, definition, the
/// sentence it was found in, and a difficulty level - so this is a formatting job rather than a
/// feature: the data was being collected and then only ever shown in a read-only list.
/// </summary>
public static class VocabularyExporter
{
    /// <summary>Writes <paramref name="entries"/> to <paramref name="path"/> and returns how many
    /// rows were written.</summary>
    public static int Export(IEnumerable<HistoryEntry> entries, string path, ExportFormat format)
    {
        var rows = entries.Where(e => !string.IsNullOrWhiteSpace(e.Word)).ToList();
        var content = format == ExportFormat.Anki ? BuildAnki(rows) : BuildCsv(rows);

        // UTF-8 with a BOM: without it Excel mis-reads Arabic, and Anki is happy either way.
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return rows.Count;
    }

    public static ExportFormat FormatForPath(string path) =>
        Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? ExportFormat.Csv
            : ExportFormat.Anki;

    private static string BuildAnki(List<HistoryEntry> entries)
    {
        var sb = new StringBuilder();

        // Anki reads these directives from the top of the file, which saves the user configuring
        // the separator and field mapping by hand on import.
        sb.AppendLine("#separator:tab");
        sb.AppendLine("#html:true");
        sb.AppendLine("#columns:Word\tTranslation\tDefinition\tExample\tExampleTranslation\tLevel\tTags");

        foreach (var entry in entries)
        {
            sb.AppendLine(string.Join('\t',
                Field(entry.Word),
                Field(entry.ArabicTranslation),
                Field(BackOfCard(entry)),
                Field(entry.Sentence),
                Field(entry.TranslatedSentence),
                Field(entry.CefrLevel),
                Field(TagsFor(entry))));
        }

        return sb.ToString();

        // Tabs and newlines are the field and record separators, so they cannot survive inside a
        // value; <br> keeps the line break visible on the card since #html:true is set.
        static string Field(string? value) =>
            (value ?? "").Replace("\t", " ").Replace("\r\n", "<br>").Replace('\n', ' ').Trim();
    }

    /// <summary>Part of speech and phonetic belong with the definition rather than in fields of
    /// their own - they're context on the answer, not something you'd ever sort or search by.</summary>
    private static string BackOfCard(HistoryEntry entry)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(entry.PartOfSpeech)) parts.Add($"({entry.PartOfSpeech})");
        if (!string.IsNullOrWhiteSpace(entry.Phonetic)) parts.Add(entry.Phonetic);
        if (!string.IsNullOrWhiteSpace(entry.Definition)) parts.Add(entry.Definition);
        if (entry.Synonyms.Length > 0) parts.Add($"Synonyms: {string.Join(", ", entry.Synonyms)}");

        return string.Join(" ", parts);
    }

    private static string TagsFor(HistoryEntry entry)
    {
        var tags = new List<string> { "tarjem" };
        if (!string.IsNullOrWhiteSpace(entry.CefrLevel)) tags.Add(entry.CefrLevel);
        return string.Join(' ', tags);
    }

    private static string BuildCsv(List<HistoryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Word,Translation,Definition,PartOfSpeech,Phonetic,Level,Sentence,TranslatedSentence,Synonyms,LookedUp");

        foreach (var entry in entries)
        {
            sb.AppendLine(string.Join(',',
                Quote(entry.Word),
                Quote(entry.ArabicTranslation),
                Quote(entry.Definition),
                Quote(entry.PartOfSpeech),
                Quote(entry.Phonetic),
                Quote(entry.CefrLevel),
                Quote(entry.Sentence),
                Quote(entry.TranslatedSentence),
                Quote(string.Join("; ", entry.Synonyms)),
                Quote(entry.Timestamp.ToString("yyyy-MM-dd HH:mm"))));
        }

        return sb.ToString();

        // RFC 4180: always quote, and double any embedded quote. Definitions routinely contain
        // commas, and translated sentences routinely contain quotation marks.
        static string Quote(string? value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
    }
}
