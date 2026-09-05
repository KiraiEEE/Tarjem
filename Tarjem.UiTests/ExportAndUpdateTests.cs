using System.IO;
using Tarjem.Models;
using Tarjem.Services;
using Xunit;

namespace Tarjem.UiTests;

[Collection(IsolatedDataCollection.Name)]
public class VocabularyExporterTests
{
    private static HistoryEntry Entry(string word = "library") => new()
    {
        Word = word,
        Definition = "A building containing books, some of which have, commas.",
        ArabicTranslation = "مكتبة",
        Sentence = "The library was \"quiet\".",
        TranslatedSentence = "كانت المكتبة هادئة.",
        PartOfSpeech = "noun",
        Phonetic = "/ˈlaɪbrəri/",
        CefrLevel = "A2",
        Synonyms = ["archive", "collection"],
    };

    private static string ExportToTemp(IEnumerable<HistoryEntry> entries, ExportFormat format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarjem-test-{Guid.NewGuid():N}.txt");
        try
        {
            VocabularyExporter.Export(entries, path, format);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnkiExportCarriesTheImportDirectives()
    {
        var content = ExportToTemp([Entry()], ExportFormat.Anki);

        // Without these, the user has to configure the separator and field mapping by hand.
        Assert.Contains("#separator:tab", content);
        Assert.Contains("#columns:", content);
    }

    [Fact]
    public void AnkiRowKeepsOneLinePerCard()
    {
        var entry = Entry();
        entry.Sentence = "First line.\nSecond line.";

        var content = ExportToTemp([entry], ExportFormat.Anki);
        var rows = content.Split('\n').Where(l => !l.StartsWith('#') && l.Trim().Length > 0).ToList();

        // A newline inside a field would otherwise split one card into two broken ones.
        Assert.Single(rows);
    }

    [Fact]
    public void AnkiExportPutsTheWordAndTranslationInTheFirstTwoFields()
    {
        var content = ExportToTemp([Entry()], ExportFormat.Anki);
        var row = content.Split('\n').First(l => !l.StartsWith('#') && l.Trim().Length > 0);
        var fields = row.Split('\t');

        Assert.Equal("library", fields[0]);
        Assert.Equal("مكتبة", fields[1]);
    }

    [Fact]
    public void CsvQuotesEmbeddedCommasAndQuotes()
    {
        var content = ExportToTemp([Entry()], ExportFormat.Csv);

        // The definition contains a comma and the sentence contains quotes; both must survive.
        Assert.Contains("\"\"quiet\"\"", content);
        Assert.Contains("some of which have, commas", content);
    }

    [Fact]
    public void EntriesWithoutAWordAreSkipped()
    {
        var content = ExportToTemp([Entry(""), Entry("library")], ExportFormat.Csv);
        var rows = content.Split('\n').Where(l => l.Trim().Length > 0).Skip(1).ToList();

        Assert.Single(rows);
    }

    [Theory]
    [InlineData("deck.csv", ExportFormat.Csv)]
    [InlineData("deck.txt", ExportFormat.Anki)]
    [InlineData("deck.tsv", ExportFormat.Anki)]
    public void FormatFollowsTheChosenExtension(string fileName, ExportFormat expected) =>
        Assert.Equal(expected, VocabularyExporter.FormatForPath(fileName));
}

public class UpdateServiceTests
{
    [Theory]
    [InlineData("0.5.0", "0.4.0")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.4.1", "0.4.0")]
    public void NewerVersionsAreDetected(string candidate, string current) =>
        Assert.True(UpdateService.IsNewer(candidate, current));

    [Theory]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("0.3.9", "0.4.0")]
    public void SameOrOlderVersionsAreNot(string candidate, string current) =>
        Assert.False(UpdateService.IsNewer(candidate, current));

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("v")]
    public void UnparseableTagsNeverProduceAnUpdatePrompt(string candidate) =>
        Assert.False(UpdateService.IsNewer(candidate, "0.4.0"));
}
