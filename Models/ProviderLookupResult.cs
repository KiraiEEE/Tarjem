namespace Tarjem.Models;

/// <summary>Result of an on-demand dictionary lookup against a specific provider, triggered by
/// the popup's source switcher. <see cref="Error"/> is non-null (and the other fields empty)
/// when the provider couldn't produce a definition, so the UI can show why instead of blanking.</summary>
public record DictionaryLookupResult(string[] Meanings, string Phonetic, string PartOfSpeech, string[] Synonyms, string? Error)
{
    public static DictionaryLookupResult Failed(string error) => new([], "", "", [], error);
}

/// <summary>Result of an on-demand translation lookup against a specific provider.</summary>
public record TranslationLookupResult(string ArabicWord, string TranslatedSentence, string? Error)
{
    public static TranslationLookupResult Failed(string error) => new("", "", error);
}
