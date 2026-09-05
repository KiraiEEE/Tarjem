using Tarjem.Models;

namespace Tarjem.Core.Translation;

public interface ITranslationProvider
{
    string Id { get; }
    Task<TranslationResult?> TranslateAsync(
        string word, string fullSentence, string sourceLanguageCode, string sourceLanguageName,
        string targetLanguageCode, string targetLanguageName, CancellationToken ct);
}
