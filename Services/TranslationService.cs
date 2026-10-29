using System.Net.Http;
using System.Text.Json;
using Serilog;
using Tarjem.Core.Language;
using Tarjem.Core.Providers;
using Tarjem.Core.Translation;
using Tarjem.Models;

namespace Tarjem.Services;

/// <summary>
/// Every lookup the app performs, routed through <see cref="ProviderCatalog"/>.
///
/// Two principles run through this class. First, <b>a lookup does not fail because one source
/// did</b>: each request walks a failover chain (the user's chosen source first, then the rest by
/// rank) and returns the first real answer, without ever rewriting the user's setting - a bad
/// afternoon for Gemini shows up as a slightly different-looking result, not an error card.
/// Second, <b>nothing is built until it is used</b>: the AI clients are lazy, so an install that
/// only ever uses the keyless dictionary never constructs, authenticates or allocates for them.
/// </summary>
public class TranslationService
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    private readonly Lazy<GeminiService> _gemini;
    private readonly Lazy<EncyclopediaService> _encyclopedia;
    private OpenAiCompatibleService? _groq;
    private OpenAiCompatibleService? _cerebras;
    private string? _groqKeyInUse;
    private string? _cerebrasKeyInUse;

    /// <summary>Dictionary sources, best first. Sourced from the catalog so the dropdowns, the
    /// failover order and the recommended defaults can never disagree with each other.</summary>
    public static (string Id, string Label)[] DictionaryProviders =>
        ProviderCatalog.OfKind(ProviderKind.Dictionary).OrderBy(p => p.Rank).Select(p => (p.Id, p.Label)).ToArray();

    public static (string Id, string Label)[] TranslationProviders =>
        ProviderCatalog.OfKind(ProviderKind.Translation).OrderBy(p => p.Rank).Select(p => (p.Id, p.Label)).ToArray();

    public static (string Id, string Label)[] EncyclopediaProviders =>
        ProviderCatalog.OfKind(ProviderKind.Encyclopedia).OrderBy(p => p.Rank).Select(p => (p.Id, p.Label)).ToArray();

    /// <summary>The <see cref="AppSettings.RegionTranslationProvider"/> value meaning "pick for me".</summary>
    public const string AutoRegionProvider = "auto";

    /// <summary>Sources that translate by rewriting rather than by mapping - the only ones the
    /// prompts in <see cref="TranslationPrompts"/> reach.</summary>
    private static readonly HashSet<string> AiProviders = new(StringComparer.OrdinalIgnoreCase) { "gemini", "groq", "cerebras" };

    public static bool IsAiProvider(string? providerId) => providerId != null && AiProviders.Contains(providerId);

    /// <summary>What the Alt+W source picker in Settings offers: the automatic choice, then every
    /// translation source.</summary>
    public static (string Id, string Label)[] RegionTranslationSources =>
        new[] { (AutoRegionProvider, "Best available (AI when possible)") }.Concat(TranslationProviders).ToArray();

    /// <summary>
    /// The source Alt+W should translate with right now. An explicit choice is always honoured;
    /// "auto" takes the best-ranked AI source that has a key, and only falls back to the ordinary
    /// default (Google, out of the box) when none does.
    /// </summary>
    public string ResolveRegionProvider()
    {
        var settings = _settingsService.Current;

        if (!string.IsNullOrEmpty(settings.RegionTranslationProvider) &&
            settings.RegionTranslationProvider != AutoRegionProvider)
            return settings.RegionTranslationProvider;

        var (targetCode, _) = GetTargetLanguage();
        var ai = ProviderCatalog.Usable(ProviderKind.Translation, targetCode, HasKey).FirstOrDefault(p => IsAiProvider(p.Id));
        if (ai != null) return ai.Id;

        return string.IsNullOrEmpty(settings.DefaultTranslationProvider) ? "google" : settings.DefaultTranslationProvider;
    }

    /// <summary>Languages the translation section can target. IsRtl drives the popup's text
    /// flow direction for whichever language is currently selected.
    ///
    /// English is in here as well as in <see cref="SourceLanguages"/>: leaving it out meant the
    /// app could read 18 languages but only write 17, and the one it couldn't write was the one
    /// most people reading a foreign-language game actually want out of it.</summary>
    public static readonly (string Code, string Name, bool IsRtl)[] TargetLanguages =
    {
        ("en", "English", false),
        ("ar", "Arabic", true),
        ("es", "Spanish", false),
        ("fr", "French", false),
        ("de", "German", false),
        ("it", "Italian", false),
        ("pt", "Portuguese", false),
        ("ru", "Russian", false),
        ("tr", "Turkish", false),
        ("nl", "Dutch", false),
        ("pl", "Polish", false),
        ("ja", "Japanese", false),
        ("ko", "Korean", false),
        ("zh-CN", "Chinese (Simplified)", false),
        ("zh-TW", "Chinese (Traditional)", false),
        ("hi", "Hindi", false),
        ("fa", "Persian", true),
        ("ur", "Urdu", true),
    };

    /// <summary>Languages OCR can read from the screen - the same set
    /// <see cref="TargetLanguages"/> covers. Actually usable at runtime only for languages with a
    /// Windows OCR pack installed - check <see cref="OcrService.IsLanguageAvailable"/>.</summary>
    public static readonly (string Code, string Name)[] SourceLanguages =
        TargetLanguages.Select(l => (l.Code, l.Name)).ToArray();

    public static bool IsRtlLanguage(string code) =>
        TargetLanguages.FirstOrDefault(l => l.Code == code).IsRtl;

    public TranslationService(SettingsService settingsService)
    {
        _settingsService = settingsService;

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Tarjem/0.4");

        _gemini = new Lazy<GeminiService>(() => new GeminiService(settingsService.Current, settingsService.Save, ResolveKey("gemini")));
        _encyclopedia = new Lazy<EncyclopediaService>(() => new EncyclopediaService());
    }

    // ===================== Keys =====================

    /// <summary>The .env / keys.dat name a catalog provider id stores its key under, or null for
    /// the keyless sources.</summary>
    public static string? KeyNameFor(string providerId) => providerId switch
    {
        "gemini" => SecureKeyService.Gemini,
        "groq" => SecureKeyService.Groq,
        "cerebras" => SecureKeyService.Cerebras,
        "merriam-webster" => SecureKeyService.MerriamWebster,
        _ => null,
    };

    /// <summary>The key actually in force for a provider: the user's own if they supplied one,
    /// otherwise the free key the build ships with. Both come out of the same DPAPI-encrypted
    /// store; neither is ever read from settings.json.</summary>
    public string? ResolveKey(string providerId) =>
        KeyNameFor(providerId) is { } name
            ? SecureKeyService.GetUserKey(name) ?? SecureKeyService.GetKey(name)
            : null;

    /// <summary>True when the user supplied their own key rather than riding the shared one -
    /// what the UI needs to decide whether to mention that a shared key can run slow.</summary>
    public bool IsUsingOwnKey(string providerId) =>
        KeyNameFor(providerId) is { } name && !string.IsNullOrWhiteSpace(SecureKeyService.GetUserKey(name));

    private bool HasKey(ProviderDescriptor p) => !string.IsNullOrWhiteSpace(ResolveKey(p.Id));

    /// <summary>Rebuilds any AI client whose key changed. Called after Settings writes a key, so
    /// the next lookup uses it without a restart.</summary>
    public void RefreshKeys()
    {
        _gemini.Value.SetApiKey(ResolveKey("gemini"));
        _groq = null;
        _cerebras = null;
    }

    private OpenAiCompatibleService? Groq()
    {
        var key = ResolveKey("groq");
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (_groq == null || _groqKeyInUse != key)
        {
            _groq = OpenAiCompatibleService.ForGroq(key);
            _groqKeyInUse = key;
        }
        return _groq;
    }

    private OpenAiCompatibleService? Cerebras()
    {
        var key = ResolveKey("cerebras");
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (_cerebras == null || _cerebrasKeyInUse != key)
        {
            _cerebras = OpenAiCompatibleService.ForCerebras(key);
            _cerebrasKeyInUse = key;
        }
        return _cerebras;
    }

    // ===================== Languages =====================

    private (string Code, string Name) GetTargetLanguage()
    {
        var code = _settingsService.Current.TargetLanguage;
        foreach (var lang in TargetLanguages)
            if (lang.Code == code) return (lang.Code, lang.Name);
        return ("ar", "Arabic");
    }

    private static (string Code, string Name) GetSourceLanguage(string code)
    {
        foreach (var lang in SourceLanguages)
            if (lang.Code == code) return (lang.Code, lang.Name);
        return ("en", "English");
    }

    // ===================== The word lookup (Alt+Q) =====================

    /// <summary>
    /// The automatic OCR lookup. Definition and translation are fetched concurrently, each
    /// through its own failover chain, so one slow or dead source costs a little latency rather
    /// than the whole result. Sections the user has switched off in Settings are not requested at
    /// all - not requested and hidden, so turning off a section actually makes lookups faster.
    /// </summary>
    /// <param name="asRead">What OCR actually read, when spelling correction changed it. The
    /// dictionary gets asked about this spelling first - see
    /// <see cref="ResolveSpellingAsync"/> for why that decision does not belong in the corrector.</param>
    public async Task<TranslationResult> TranslateWord(
        string word, string fullSentence, string sourceLanguageCode, CancellationToken ct = default, string? asRead = null)
    {
        var settings = _settingsService.Current;
        var (sourceLangCode, sourceLangName) = GetSourceLanguage(sourceLanguageCode);
        var (targetLangCode, targetLangName) = GetTargetLanguage();

        // OCR can legitimately find a word with no surrounding line (a lone menu label), and
        // every provider below treats "no sentence" as a valid mode rather than an error.
        var sentence = fullSentence ?? "";

        // Settle which spelling we are answering about before anything is dispatched, so the
        // definition, the translation, the headword and the history entry are all about the
        // same word.
        var confirmed = await ResolveSpellingAsync(
            word, asRead, sourceLangCode, sourceLangName, targetLangCode, targetLangName, sentence, ct);

        word = confirmed.Word;

        var result = new TranslationResult
        {
            OriginalWord = word,
            SourceLanguage = sourceLangCode,
            TargetLanguage = targetLangCode,
            FullSentence = sentence,
        };

        var dictionaryTask = !settings.ShowDictionarySection ? Task.FromResult<DictionaryOutcome?>(null)
            : confirmed.Definition != null ? Task.FromResult<DictionaryOutcome?>(confirmed.Definition)
            : LookupDictionaryWithFailoverAsync(settings.DefaultDictionaryProvider, word, sourceLangCode, sourceLangName, targetLangCode, targetLangName, sentence, ct);

        // Now that English is a valid target, source and target can legitimately be the same
        // language. Translating a word into the language it's already in would walk the entire
        // provider chain only for every one of them to echo the input back and be discarded as
        // "no useful answer" - so don't ask.
        var needsTranslation = (settings.ShowTranslationSection || settings.ShowInlineWordTranslation)
                               && !string.Equals(sourceLangCode, targetLangCode, StringComparison.OrdinalIgnoreCase);

        var translationTask = needsTranslation
            ? TranslateWordWithFailoverAsync(settings.DefaultTranslationProvider, word, sentence, sourceLangCode, sourceLangName, targetLangCode, targetLangName, ct)
            : Task.FromResult<TranslationOutcome?>(null);

        await Task.WhenAll(dictionaryTask, translationTask);

        var dictionary = await dictionaryTask;
        var translation = await translationTask;

        if (dictionary != null)
        {
            result.Meanings = dictionary.Meanings;
            result.Phonetic = dictionary.Phonetic;
            result.PartOfSpeech = dictionary.PartOfSpeech;
            result.CefrLevel = dictionary.CefrLevel;
            result.Synonyms = dictionary.Synonyms;
            result.DefinitionProvider = dictionary.ProviderId;
            result.IsDefinitionFromGemini = dictionary.ProviderId == "gemini";
            result.IsEncyclopedic = dictionary.IsEncyclopedic;
            result.SourceUrl = dictionary.SourceUrl;
        }

        if (translation != null)
        {
            result.ArabicWord = translation.Word;
            result.ArabicTranslation = translation.Word;
            result.TranslatedSentence = translation.Sentence;
            result.TranslatedSentenceHighlightStart = translation.HighlightStart;
            result.TranslatedSentenceHighlightLength = translation.HighlightLength;
            result.TranslationProvider = translation.ProviderId;
            result.IsTranslationFromGemini = translation.ProviderId == "gemini";
        }

        // The dictionary chain may have produced a translation too (Gemini answers both in one
        // request). Use it only where the translation chain came up empty, so an explicitly
        // chosen translation source always wins.
        if (dictionary?.TranslatedWord is { Length: > 0 } fromDictionary && string.IsNullOrEmpty(result.ArabicWord))
        {
            result.ArabicWord = fromDictionary;
            result.ArabicTranslation = fromDictionary;
        }

        // A hidden dictionary section was never requested, so an empty Meanings there is correct
        // and needs no placeholder; only a section the user can actually see does.
        if (result.Meanings.Length == 0 && settings.ShowDictionarySection)
            result.Meanings = ["No definition found"];

        result.IsFromGemini = result.IsDefinitionFromGemini || result.IsTranslationFromGemini;
        return result;
    }

    /// <summary>
    /// One named encyclopedia source, no failover - the Alt+E card's own source switcher.
    ///
    /// No failover for the same reason the word popup's switcher has none: the user picked that
    /// source specifically, and quietly answering from a different one would make the switcher
    /// lie about where the answer came from.
    /// </summary>
    public async Task<TranslationResult?> ExplainWithProviderAsync(
        string providerId, string term, string sourceLanguageCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;

        var settings = _settingsService.Current;
        var (sourceCode, _) = GetSourceLanguage(sourceLanguageCode);
        var (targetCode, _) = GetTargetLanguage();

        EncyclopediaEntry? entry;
        try
        {
            entry = await LookupOneEncyclopediaAsync(
                providerId, term, sourceCode,
                EncyclopediaService.SummaryLengthFor(settings.ExplainAnswerLength), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Encyclopedia source {Provider} threw for {Term}", providerId, term);
            return null;
        }

        if (entry == null) return null;

        var result = new TranslationResult
        {
            OriginalWord = entry.Title,
            Meanings = [entry.Summary],
            PartOfSpeech = entry.Description ?? "",
            SourceLanguage = sourceCode,
            TargetLanguage = targetCode,
            IsEncyclopedic = true,
            DefinitionProvider = providerId,
            SourceUrl = entry.SourceUrl,
            FullSentence = entry.Summary,
        };

        if (settings.ExplainTranslateAnswer &&
            !string.Equals(sourceCode, targetCode, StringComparison.OrdinalIgnoreCase))
        {
            var translated = await TranslateTextWithFailoverAsync(
                settings.DefaultTranslationProvider, entry.Summary, sourceCode, ct);

            if (!string.IsNullOrWhiteSpace(translated))
            {
                result.TranslatedSentence = translated;
                result.TranslationProvider = settings.DefaultTranslationProvider;
            }
        }

        return result;
    }

    /// <summary>Which spelling the lookup is about, and the definition that settled it.</summary>
    private readonly record struct ConfirmedSpelling(string Word, DictionaryOutcome? Definition);

    /// <summary>
    /// Decides between what OCR read and what spelling correction proposed, by asking the source
    /// that actually knows.
    ///
    /// The corrector has to answer "is this a real word?" from a 47,000-word frequency list, and
    /// that list is confidently wrong about a great many real words - English derives freely, so
    /// "governorate" is missing from it while "governor" sits exactly at the edit limit, and the
    /// correction looked like a repair rather than the silent substitution of a different word.
    /// Extending the list, or bolting a table of English suffixes onto it, only moves that line;
    /// it stays a list, and it stays English-only while the app reads eighteen languages.
    ///
    /// The dictionary about to be queried already knows every real word in all of them. So the
    /// reading OCR produced is offered to it first, and a definition is taken as proof that OCR was
    /// right - no list, no morphology, and it improves whenever the dictionaries do.
    ///
    /// Costs nothing in the case that matters: when OCR was right, this <em>is</em> the lookup, and
    /// the correction is simply never pursued. Only a genuine misreading pays for the extra
    /// request, and only when the corrector wanted to change something in the first place.
    /// </summary>
    private async Task<ConfirmedSpelling> ResolveSpellingAsync(
        string corrected, string? asRead, string sourceCode, string sourceName,
        string targetCode, string targetName, string sentence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asRead) ||
            asRead.Equals(corrected, StringComparison.OrdinalIgnoreCase) ||
            !_settingsService.Current.ShowDictionarySection)
            return new ConfirmedSpelling(corrected, null);

        var outcome = await LookupDictionaryWithFailoverAsync(
            _settingsService.Current.DefaultDictionaryProvider, asRead,
            sourceCode, sourceName, targetCode, targetName, sentence, ct);

        // An encyclopedia answer does not count as confirmation. That fallback fires for anything
        // that looks like a name, which is exactly what a mangled token looks like - taking it as
        // proof would confirm every misreading that happens to start with a capital.
        if (outcome is not { IsEncyclopedic: false, Meanings.Length: > 0 })
            return new ConfirmedSpelling(corrected, null);

        Log.Debug("Dictionary knows {AsRead}, so keeping it over the correction to {Corrected}", asRead, corrected);
        return new ConfirmedSpelling(asRead, outcome);
    }

    // ===================== The encyclopedia lookup (Alt+E) =====================

    /// <summary>
    /// Answers "what *is* this?" for the word under the cursor, on demand.
    ///
    /// Deliberately different from the encyclopedia fallback inside <see cref="TranslateWord"/> in
    /// two ways. It doesn't wait for every dictionary to come back empty first - the user pressing
    /// this key has already told us they want an identity, not a definition - and it skips the
    /// <see cref="EncyclopediaService.LooksLikeAName"/> gate, which exists only to stop the
    /// automatic path spending a request on every OCR-mangled token. An explicit press is worth a
    /// request even when the word is lowercase.
    ///
    /// The summary is also translated into the target language, because "NVIDIA is an American
    /// technology company" is not much use to someone who needed the translator in the first place.
    /// Returns null only when every source in
    /// <see cref="LookupEncyclopediaWithFailoverAsync"/> came back empty.
    /// </summary>
    public async Task<TranslationResult?> ExplainAsync(string word, string sourceLanguageCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;

        var (sourceCode, _) = GetSourceLanguage(sourceLanguageCode);
        var (targetCode, _) = GetTargetLanguage();

        var settings = _settingsService.Current;

        var found = await LookupEncyclopediaWithFailoverAsync(
            settings.DefaultEncyclopediaProvider, word, sourceCode,
            EncyclopediaService.SummaryLengthFor(settings.ExplainAnswerLength),
            mayReachUnchosenAiSources: true, ct);

        if (found == null) return null;

        var (providerId, entry) = found.Value;

        var result = new TranslationResult
        {
            OriginalWord = entry.Title,
            Meanings = [entry.Summary],
            PartOfSpeech = entry.Description ?? "",
            SourceLanguage = sourceCode,
            TargetLanguage = targetCode,
            IsEncyclopedic = true,
            DefinitionProvider = providerId,
            SourceUrl = entry.SourceUrl,
            FullSentence = entry.Summary,
        };

        // Same-language lookup needs no translation pass - the summary already is the answer - and
        // neither does a user who turned the translation off, which is the single biggest thing
        // Alt+E can be made to skip: the article is already in hand by this point, and everything
        // after it is another network round trip.
        if (settings.ExplainTranslateAnswer &&
            !string.Equals(sourceCode, targetCode, StringComparison.OrdinalIgnoreCase))
        {
            var translated = await TranslateTextWithFailoverAsync(
                settings.DefaultTranslationProvider, entry.Summary, sourceCode, ct);

            if (!string.IsNullOrWhiteSpace(translated))
            {
                result.TranslatedSentence = translated;
                result.TranslationProvider = settings.DefaultTranslationProvider;
            }
        }

        return result;
    }

    /// <summary>
    /// Walks the encyclopedia sources in order until one recognizes the name, and reports which one
    /// did - the card credits the source that actually answered, which after a failover is not the
    /// one in Settings.
    ///
    /// The chain matters more here than anywhere else in the app. The wikis are records: when they
    /// answer they are citable, and when they don't it is because the entity genuinely has no page.
    /// The AI sources behind them answer for the long tail that has no page at all, from memory
    /// rather than from a record - which is why they sit last, and why they are asked to decline
    /// rather than guess.
    /// </summary>
    /// <param name="mayReachUnchosenAiSources">False on the automatic path. Alt+Q's fallback fires
    /// on every capitalized token no dictionary knew, which over a screen of OCR output is a great
    /// many tokens that are not names at all - letting that walk into three model requests apiece
    /// would spend a user's whole free tier on recognition errors. An explicit Alt+E press is worth
    /// the requests; a speculative fallback is not, unless the user chose an AI source themselves.</param>
    private async Task<(string ProviderId, EncyclopediaEntry Entry)?> LookupEncyclopediaWithFailoverAsync(
        string? preferredId, string term, string sourceCode, int maxSummaryLength,
        bool mayReachUnchosenAiSources, CancellationToken ct)
    {
        IEnumerable<ProviderDescriptor> chain =
            ProviderCatalog.FailoverChain(ProviderKind.Encyclopedia, preferredId, sourceCode, HasKey);

        if (!mayReachUnchosenAiSources)
            chain = chain.Where(p => !p.RequiresKey || p.Id == preferredId);

        foreach (var provider in chain)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var entry = await LookupOneEncyclopediaAsync(provider.Id, term, sourceCode, maxSummaryLength, ct);
                if (entry != null) return (provider.Id, entry);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Encyclopedia source {Provider} threw for {Term}", provider.Id, term);
            }
        }

        return null;
    }

    /// <summary>One source's answer. The wikis go through <see cref="EncyclopediaService"/>; the AI
    /// sources are asked here, because this is where their clients and keys live.</summary>
    private async Task<EncyclopediaEntry?> LookupOneEncyclopediaAsync(
        string providerId, string term, string sourceCode, int maxSummaryLength, CancellationToken ct)
    {
        if (providerId is "wikipedia" or "wikidata")
            return await _encyclopedia.Value.LookupAsync(providerId, term, sourceCode, maxSummaryLength, ct);

        var (_, sourceName) = GetSourceLanguage(sourceCode);

        var answer = providerId switch
        {
            "gemini" => await _gemini.Value.ExplainAsync(term, sourceName, ct),
            "groq" => Groq() is { } groq ? await groq.ExplainAsync(term, sourceName, ct) : null,
            "cerebras" => Cerebras() is { } cerebras ? await cerebras.ExplainAsync(term, sourceName, ct) : null,
            _ => null,
        };

        if (answer == null) return null;

        // No SourceUrl: there is no page to send the user to, and "Read more" pointing at a search
        // result would be dressing a recollection up as a citation.
        return new EncyclopediaEntry(
            Title: string.IsNullOrWhiteSpace(answer.Title) ? term : answer.Title,
            Summary: Trim(answer.Summary, maxSummaryLength),
            Description: answer.Kind,
            SourceUrl: "");
    }

    /// <summary>The AI sources answer at whatever length they feel like, so the "Answer length"
    /// setting has to be applied to them here rather than being trusted to the prompt.</summary>
    private static string Trim(string text, int maxLength)
    {
        var clean = text.Replace('\n', ' ').Trim();
        if (clean.Length <= maxLength) return clean;

        var cut = clean.LastIndexOf(". ", maxLength, StringComparison.Ordinal);
        return cut > 60 ? clean[..(cut + 1)] : clean[..maxLength].TrimEnd() + "…";
    }

    // ===================== Dictionary =====================

    /// <summary>How many definitions the popup has room for.</summary>
    private const int ShownMeanings = 3;

    /// <summary>How many each parser collects before <see cref="Refine"/> trims them back. More
    /// than are shown, because a source can lead with cross-references ("plural of mouse") that
    /// are about to be demoted - collecting only what fits meant the real senses never made it
    /// out of the parser to be preferred in the first place.</summary>
    private const int CollectedMeanings = 10;

    /// <summary>One source's answer, plus which source it came from - the popup shows the
    /// provider that actually answered, which after a failover is not the one in Settings.</summary>
    public sealed record DictionaryOutcome(
        string ProviderId, string[] Meanings, string Phonetic, string PartOfSpeech,
        string CefrLevel, string[] Synonyms, string? TranslatedWord = null,
        bool IsEncyclopedic = false, string? SourceUrl = null);

    private async Task<DictionaryOutcome?> LookupDictionaryWithFailoverAsync(
        string preferredId, string word, string sourceCode, string sourceName,
        string targetCode, string targetName, string fullSentence, CancellationToken ct)
    {
        var chain = ProviderCatalog.FailoverChain(ProviderKind.Dictionary, preferredId, sourceCode, HasKey);

        foreach (var provider in chain)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await LookupOneDictionaryAsync(provider.Id, word, sourceCode, sourceName, targetCode, targetName, fullSentence, ct);
                if (outcome != null)
                {
                    if (provider.Id != preferredId)
                        Log.Debug("Dictionary failover: {Preferred} did not answer for {Word}, {Actual} did", preferredId, word, provider.Id);

                    return await FollowCrossReferenceAsync(
                        outcome, provider.Id, word, sourceCode, sourceName, targetCode, targetName, fullSentence, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Dictionary source {Provider} threw for {Word}", provider.Id, word);
            }
        }

        // Nothing defines it. Before giving up, consider that it may not be a word at all but a
        // name - which is the difference between "no definition found" and a useful answer.
        if (_settingsService.Current.EncyclopediaFallbackEnabled && EncyclopediaService.LooksLikeAName(word))
        {
            // The Alt+Q fallback always uses the short form: it is a consolation line inside a
            // dictionary popup, not the answer the user asked for. The length setting belongs to
            // Alt+E, where the article *is* the answer.
            var found = await LookupEncyclopediaWithFailoverAsync(
                _settingsService.Current.DefaultEncyclopediaProvider, word, sourceCode,
                EncyclopediaService.ShortSummaryLength,
                mayReachUnchosenAiSources: false, ct);

            if (found != null)
            {
                var (providerId, entry) = found.Value;
                return new DictionaryOutcome(
                    providerId, [entry.Summary], "", entry.Description ?? "", "", [],
                    IsEncyclopedic: true, SourceUrl: entry.SourceUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// Trims a source's definitions to what the popup shows, putting the ones that actually define
    /// something first.
    ///
    /// Sources list an inflected word's cross-reference ahead of its real senses - "mice" leads
    /// with "plural of mouse" and only then gives the verb, "running" with "present participle of
    /// run" before the adjective. Taking the first three therefore threw away every real sense the
    /// source had and kept the pointer. When the pointers are all there is, they are kept: they
    /// still say something true, and <see cref="FollowCrossReferenceAsync"/> is what replaces them.
    /// </summary>
    private static string[] Refine(string[] meanings)
    {
        var (real, references) = InflectionParser.Partition(meanings);

        return (real.Count > 0 ? real : references)
            .Take(ShownMeanings)
            .ToArray();
    }

    /// <summary>
    /// Turns "plural of revelation" into what revelation actually means.
    ///
    /// Wiktionary-derived sources - which the default dictionary is one of - give inflected words
    /// their own entries containing nothing but a pointer to the base word. That is correct
    /// lexicography and a useless answer: someone who hovered "revelations" because they did not
    /// know it does not know "revelation" either. So the pointer is followed once, against the same
    /// source, and the base word's real definition is shown under the word that was hovered.
    ///
    /// Once, not repeatedly: a second hop would be a dictionary loop waiting to happen, and no real
    /// entry is two pointers deep. If the base word does not resolve, the original answer stands -
    /// "plural of revelation" is at least true, and better than nothing at all.
    /// </summary>
    private async Task<DictionaryOutcome> FollowCrossReferenceAsync(
        DictionaryOutcome outcome, string providerId, string word, string sourceCode, string sourceName,
        string targetCode, string targetName, string fullSentence, CancellationToken ct)
    {
        var redirect = InflectionParser.RedirectFor(outcome.Meanings, word);
        if (redirect == null) return outcome;

        Log.Debug("{Word} is the {Label} of {Base}; looking that up instead", word, redirect.Label, redirect.BaseWord);

        var resolved = await LookupOneDictionaryAsync(
            providerId, redirect.BaseWord, sourceCode, sourceName, targetCode, targetName, fullSentence, ct);

        // A base word that only points somewhere else again is a chain we decline to follow.
        if (resolved == null || InflectionParser.RedirectFor(resolved.Meanings, redirect.BaseWord) != null)
            return outcome;

        // Everything except the word itself comes from the base entry: its definition, its
        // pronunciation, its synonyms. The headword stays whatever the reader hovered, because
        // that is the word on their screen and the one the highlight is sitting on.
        return resolved with
        {
            CefrLevel = string.IsNullOrEmpty(resolved.CefrLevel) ? outcome.CefrLevel : resolved.CefrLevel,
            TranslatedWord = outcome.TranslatedWord ?? resolved.TranslatedWord,
        };
    }

    private async Task<DictionaryOutcome?> LookupOneDictionaryAsync(
        string providerId, string word, string sourceCode, string sourceName,
        string targetCode, string targetName, string fullSentence, CancellationToken ct)
    {
        var lower = word.ToLowerInvariant().Trim();

        switch (providerId)
        {
            case "gemini":
            {
                var r = await _gemini.Value.TranslateAsync(word, fullSentence, sourceCode, sourceName, targetCode, targetName, ct);
                if (r == null || r.Meanings.Length == 0) return null;
                return new DictionaryOutcome("gemini", r.Meanings, r.Phonetic, r.PartOfSpeech, r.CefrLevel, r.Synonyms, r.ArabicWord);
            }
            case "dictionaryapi.dev":
                return Adapt("dictionaryapi.dev", await LookupDictionaryApiDevAsync(lower, ct), lower);
            case "freedictionaryapi.com":
                return Adapt("freedictionaryapi.com", await LookupFreeDictionaryApiAsync(lower, sourceCode, ct), lower);
            case "wiktionary":
                return Adapt("wiktionary", await LookupWiktionaryAsync(lower, sourceCode, ct), lower);
            case "merriam-webster":
                return Adapt("merriam-webster", await LookupMerriamWebsterAsync(lower, ct), lower);
            default:
                return null;
        }

        static DictionaryOutcome? Adapt(string id, DictionaryLookupResult? r, string word)
        {
            if (r is not { Error: null, Meanings.Length: > 0 }) return null;

            var meanings = Refine(r.Meanings);
            return new DictionaryOutcome(id, meanings, r.Phonetic, r.PartOfSpeech, CefrEstimator.Estimate(word, meanings), r.Synonyms);
        }
    }

    /// <summary>On-demand lookup from the popup's source switcher - one named source, no
    /// failover, because the user asked for that source specifically and silently answering from
    /// a different one would make the switcher lie.</summary>
    public async Task<DictionaryLookupResult> LookupDictionaryAsync(string providerId, string word, string languageCode = "en", CancellationToken ct = default)
    {
        var lower = word.ToLowerInvariant().Trim();
        try
        {
            var result = await AskOneDictionaryAsync(providerId, lower, languageCode, ct);
            if (result.Error != null) return result;

            // The switcher follows cross-references for the same reason the automatic path does:
            // picking a different source to escape "plural of revelation" and being handed "plural
            // of revelation" again is the switcher appearing not to work.
            var redirect = InflectionParser.RedirectFor(result.Meanings, lower);
            if (redirect != null)
            {
                var resolved = await AskOneDictionaryAsync(providerId, redirect.BaseWord, languageCode, ct);
                if (resolved.Error == null &&
                    resolved.Meanings.Length > 0 &&
                    InflectionParser.RedirectFor(resolved.Meanings, redirect.BaseWord) == null)
                    return resolved with { Meanings = Refine(resolved.Meanings) };
            }

            return result with { Meanings = Refine(result.Meanings) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Dictionary provider {Provider} failed for {Word}", providerId, word);
            return DictionaryLookupResult.Failed("Lookup failed - the service may be unavailable.");
        }
    }

    private async Task<DictionaryLookupResult> AskOneDictionaryAsync(
        string providerId, string word, string languageCode, CancellationToken ct) =>
        providerId switch
        {
            "dictionaryapi.dev" => await LookupDictionaryApiDevAsync(word, ct),
            "freedictionaryapi.com" => await LookupFreeDictionaryApiAsync(word, languageCode, ct),
            "wiktionary" => await LookupWiktionaryAsync(word, languageCode, ct),
            "merriam-webster" => await LookupMerriamWebsterAsync(word, ct),
            _ => DictionaryLookupResult.Failed("Unknown dictionary source."),
        };

    private async Task<DictionaryLookupResult> LookupDictionaryApiDevAsync(string word, CancellationToken ct)
    {
        var url = $"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(word)}";
        using var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return DictionaryLookupResult.Failed("No definition found.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            return DictionaryLookupResult.Failed("No definition found.");

        var entry = json.RootElement[0];
        var phonetic = "";

        if (entry.TryGetProperty("phonetic", out var phoneticProp) && phoneticProp.ValueKind == JsonValueKind.String)
            phonetic = phoneticProp.GetString() ?? "";
        else if (entry.TryGetProperty("phonetics", out var phoneticsArr) && phoneticsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in phoneticsArr.EnumerateArray())
            {
                if (p.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String &&
                    txt.GetString() is { Length: > 0 } t)
                {
                    phonetic = t;
                    break;
                }
            }
        }

        var meanings = new List<string>();
        var synonyms = new List<string>();
        var partOfSpeech = "";

        if (entry.TryGetProperty("meanings", out var meaningsArr) && meaningsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var meaning in meaningsArr.EnumerateArray())
            {
                if (partOfSpeech.Length == 0 && meaning.TryGetProperty("partOfSpeech", out var pos))
                    partOfSpeech = pos.GetString() ?? "";

                if (meanings.Count < CollectedMeanings && meaning.TryGetProperty("definitions", out var defs) && defs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in defs.EnumerateArray())
                    {
                        if (meanings.Count >= CollectedMeanings) break;
                        if (d.TryGetProperty("definition", out var text) && text.GetString() is { Length: > 0 } value)
                            meanings.Add(value);
                    }
                }

                if (meaning.TryGetProperty("synonyms", out var syns) && syns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in syns.EnumerateArray())
                    {
                        if (synonyms.Count >= 5) break;
                        if (s.GetString() is { Length: > 0 } value && !synonyms.Contains(value))
                            synonyms.Add(value);
                    }
                }
            }
        }

        return meanings.Count == 0
            ? DictionaryLookupResult.Failed("No definition found.")
            : new DictionaryLookupResult(meanings.ToArray(), phonetic, partOfSpeech, synonyms.ToArray(), null);
    }

    private async Task<DictionaryLookupResult> LookupFreeDictionaryApiAsync(string word, string languageCode, CancellationToken ct)
    {
        var lang = BaseTag(languageCode);
        var url = $"https://freedictionaryapi.com/api/v1/entries/{lang}/{Uri.EscapeDataString(word)}";
        using var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return DictionaryLookupResult.Failed("No definition found.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            return DictionaryLookupResult.Failed("No definition found.");

        var meanings = new List<string>();
        var synonyms = new List<string>();
        var partOfSpeech = "";
        var phonetic = "";

        foreach (var entry in entries.EnumerateArray())
        {
            if (partOfSpeech.Length == 0 && entry.TryGetProperty("partOfSpeech", out var posEl))
                partOfSpeech = posEl.GetString() ?? "";

            if (phonetic.Length == 0 && entry.TryGetProperty("pronunciations", out var prons) && prons.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in prons.EnumerateArray())
                {
                    if (p.TryGetProperty("text", out var txt) && txt.GetString() is { Length: > 0 } t)
                    {
                        phonetic = t;
                        break;
                    }
                }
            }

            if (entry.TryGetProperty("senses", out var senses) && senses.ValueKind == JsonValueKind.Array)
            {
                foreach (var sense in senses.EnumerateArray())
                {
                    if (meanings.Count < CollectedMeanings && sense.TryGetProperty("definition", out var defEl) &&
                        defEl.GetString() is { Length: > 0 } d)
                        meanings.Add(d);

                    if (sense.TryGetProperty("synonyms", out var syns) && syns.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in syns.EnumerateArray())
                        {
                            if (synonyms.Count >= 5) break;
                            if (s.GetString() is { Length: > 0 } value && !synonyms.Contains(value))
                                synonyms.Add(value);
                        }
                    }
                }
            }
        }

        return meanings.Count == 0
            ? DictionaryLookupResult.Failed("No definition found.")
            : new DictionaryLookupResult(meanings.ToArray(), phonetic, partOfSpeech, synonyms.ToArray(), null);
    }

    /// <summary>
    /// Wiktionary's own REST definition endpoint - the widest language coverage available without
    /// a key, and the backstop for languages FreeDictionaryAPI has thin data for. Definitions
    /// arrive as HTML fragments, so they need stripping before they can be shown.
    /// </summary>
    private async Task<DictionaryLookupResult> LookupWiktionaryAsync(string word, string languageCode, CancellationToken ct)
    {
        var lang = BaseTag(languageCode);
        var url = $"https://{lang}.wiktionary.org/api/rest_v1/page/definition/{Uri.EscapeDataString(word)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return DictionaryLookupResult.Failed("No definition found.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        // Keyed by language code: { "en": [ { partOfSpeech, definitions: [ { definition } ] } ] }
        if (!json.RootElement.TryGetProperty(lang, out var section) || section.ValueKind != JsonValueKind.Array)
            return DictionaryLookupResult.Failed("No definition found.");

        var meanings = new List<string>();
        var partOfSpeech = "";

        foreach (var group in section.EnumerateArray())
        {
            if (partOfSpeech.Length == 0 && group.TryGetProperty("partOfSpeech", out var pos))
                partOfSpeech = pos.GetString() ?? "";

            if (!group.TryGetProperty("definitions", out var defs) || defs.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var d in defs.EnumerateArray())
            {
                if (meanings.Count >= CollectedMeanings) break;
                if (d.TryGetProperty("definition", out var text) && text.GetString() is { Length: > 0 } raw)
                {
                    var clean = HtmlText.Strip(raw);
                    if (clean.Length > 0) meanings.Add(clean);
                }
            }

            if (meanings.Count >= CollectedMeanings) break;
        }

        return meanings.Count == 0
            ? DictionaryLookupResult.Failed("No definition found.")
            : new DictionaryLookupResult(meanings.ToArray(), "", partOfSpeech, [], null);
    }

    private async Task<DictionaryLookupResult> LookupMerriamWebsterAsync(string word, CancellationToken ct)
    {
        var apiKey = ResolveKey("merriam-webster");
        if (string.IsNullOrWhiteSpace(apiKey))
            return DictionaryLookupResult.Failed("Add a Merriam-Webster API key in Settings to use this source.");

        var url = $"https://www.dictionaryapi.com/api/v3/references/collegiate/json/{Uri.EscapeDataString(word)}?key={Uri.EscapeDataString(apiKey)}";
        using var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return DictionaryLookupResult.Failed("No definition found.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;

        // Plain strings instead of entry objects means MW didn't recognize the word and is
        // offering spelling suggestions rather than a definition.
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Object)
            return DictionaryLookupResult.Failed("No definition found.");

        var meanings = new List<string>();
        var partOfSpeech = "";

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            if (partOfSpeech.Length == 0 && entry.TryGetProperty("fl", out var fl))
                partOfSpeech = fl.GetString() ?? "";

            if (entry.TryGetProperty("shortdef", out var shortdef) && shortdef.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in shortdef.EnumerateArray())
                {
                    if (meanings.Count >= CollectedMeanings) break;
                    if (d.GetString() is { Length: > 0 } text) meanings.Add(text);
                }
            }

            if (meanings.Count >= CollectedMeanings) break;
        }

        return meanings.Count == 0
            ? DictionaryLookupResult.Failed("No definition found.")
            : new DictionaryLookupResult(meanings.ToArray(), "", partOfSpeech, [], null);
    }

    // ===================== Translation =====================

    public sealed record TranslationOutcome(
        string ProviderId, string Word, string Sentence, int HighlightStart = -1, int HighlightLength = 0);

    private async Task<TranslationOutcome?> TranslateWordWithFailoverAsync(
        string preferredId, string word, string sentence, string sourceCode, string sourceName,
        string targetCode, string targetName, CancellationToken ct)
    {
        var chain = ProviderCatalog.FailoverChain(ProviderKind.Translation, preferredId, targetCode, HasKey);

        foreach (var provider in chain)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await TranslateWordWithOneAsync(provider.Id, word, sentence, sourceCode, sourceName, targetCode, targetName, ct);
                if (outcome != null)
                {
                    if (provider.Id != preferredId)
                        Log.Debug("Translation failover: {Preferred} did not answer, {Actual} did", preferredId, provider.Id);
                    return outcome;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Translation source {Provider} threw", provider.Id);
            }
        }

        return null;
    }

    private async Task<TranslationOutcome?> TranslateWordWithOneAsync(
        string providerId, string word, string sentence, string sourceCode, string sourceName,
        string targetCode, string targetName, CancellationToken ct)
    {
        switch (providerId)
        {
            case "gemini":
            {
                var r = await _gemini.Value.TranslateAsync(word, sentence, sourceCode, sourceName, targetCode, targetName, ct);
                if (r == null || string.IsNullOrWhiteSpace(r.ArabicWord)) return null;
                return new TranslationOutcome("gemini", r.ArabicWord, r.TranslatedSentence,
                    r.TranslatedSentenceHighlightStart, r.TranslatedSentenceHighlightLength);
            }
            case "groq":
            case "cerebras":
            {
                var service = providerId == "groq" ? Groq() : Cerebras();
                if (service == null) return null;
                var r = await service.TranslateWordAsync(word, sentence, sourceName, targetName, ct);
                return r == null ? null : new TranslationOutcome(providerId, r.Value.Word, r.Value.Sentence);
            }
            case "google":
            {
                var translatedWord = await GoogleTranslateAsync(word, sourceCode, targetCode, ct);
                if (string.IsNullOrWhiteSpace(translatedWord) || translatedWord == word) return null;
                var translatedSentence = string.IsNullOrEmpty(sentence)
                    ? ""
                    : await GoogleTranslateAsync(sentence, sourceCode, targetCode, ct) ?? "";
                return new TranslationOutcome("google", translatedWord, translatedSentence);
            }
            case "mymemory":
            {
                var translatedWord = await MyMemoryTranslateAsync(word, sourceCode, targetCode, ct);
                if (string.IsNullOrWhiteSpace(translatedWord)) return null;
                var translatedSentence = string.IsNullOrEmpty(sentence)
                    ? ""
                    : await MyMemoryTranslateAsync(sentence, sourceCode, targetCode, ct) ?? "";
                return new TranslationOutcome("mymemory", translatedWord, translatedSentence);
            }
            case "lingva":
            {
                var translatedWord = await LingvaTranslateAsync(word, sourceCode, targetCode, ct);
                if (string.IsNullOrWhiteSpace(translatedWord)) return null;
                var translatedSentence = string.IsNullOrEmpty(sentence)
                    ? ""
                    : await LingvaTranslateAsync(sentence, sourceCode, targetCode, ct) ?? "";
                return new TranslationOutcome("lingva", translatedWord, translatedSentence);
            }
            default:
                return null;
        }
    }

    /// <summary>On-demand translation from the popup's source switcher - one named source only,
    /// for the same reason the dictionary switcher doesn't fail over.</summary>
    public async Task<TranslationLookupResult> TranslateWithProviderAsync(
        string providerId, string word, string sentence, string sourceLanguageCode = "en", CancellationToken ct = default)
    {
        var (srcCode, srcName) = GetSourceLanguage(sourceLanguageCode);
        var (tgtCode, tgtName) = GetTargetLanguage();

        try
        {
            var outcome = await TranslateWordWithOneAsync(providerId, word, sentence, srcCode, srcName, tgtCode, tgtName, ct);
            return outcome == null
                ? TranslationLookupResult.Failed("Translation failed - the service may be unavailable.")
                : new TranslationLookupResult(outcome.Word, outcome.Sentence, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Translation provider {Provider} failed for {Word}", providerId, word);
            return TranslationLookupResult.Failed("Translation failed - the service may be unavailable.");
        }
    }

    /// <summary>Direct on-demand Gemini call, used by the popup's source switcher.</summary>
    public Task<TranslationResult?> RequestGeminiAsync(string word, string fullSentence, string sourceLanguageCode, CancellationToken ct = default)
    {
        var (srcCode, srcName) = GetSourceLanguage(sourceLanguageCode);
        var (tgtCode, tgtName) = GetTargetLanguage();
        return _gemini.Value.TranslateAsync(word, fullSentence, srcCode, srcName, tgtCode, tgtName, ct);
    }

    // ===================== Region text (Alt+W) =====================

    /// <summary>
    /// Translates a whole block of screen text, falling over between sources the same way the
    /// word lookup does. The region overlay has nothing to fall back on if this returns null -
    /// the user gets an empty overlay where their text used to be - so it is worth walking the
    /// entire chain before giving up.
    /// </summary>
    public async Task<string?> TranslateTextWithFailoverAsync(
        string preferredId, string text, string sourceLanguageCode = "en", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var (targetCode, _) = GetTargetLanguage();

        // Re-selecting the same region, or flipping back to a provider already tried, used to
        // re-hit the network every time. Block translation is the app's slowest and most
        // rate-limit-hungry call, so it's the one most worth not repeating.
        var cacheKey = BlockCacheKey(preferredId, text, sourceLanguageCode, targetCode);
        if (_blockCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var chain = ProviderCatalog.FailoverChain(ProviderKind.Translation, preferredId, targetCode, HasKey);

        foreach (var provider in chain)
        {
            ct.ThrowIfCancellationRequested();
            var translated = await TranslateTextWithProviderAsync(provider.Id, text, sourceLanguageCode, ct);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                if (provider.Id != preferredId)
                    Log.Debug("Region translation failover: {Preferred} did not answer, {Actual} did", preferredId, provider.Id);

                RememberBlock(cacheKey, translated);
                return translated;
            }
        }

        return null;
    }

    // Bounded so a long session translating a lot of screen text can't grow without limit; the
    // insertion queue makes eviction oldest-first without needing a full LRU.
    private const int MaxCachedBlocks = 60;
    private readonly Dictionary<string, string> _blockCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _blockCacheOrder = new();

    private static string BlockCacheKey(string providerId, string text, string sourceCode, string targetCode) =>
        $"{providerId}{sourceCode}{targetCode}{text}";

    private void RememberBlock(string key, string translated)
    {
        if (!_blockCache.TryAdd(key, translated)) return;

        _blockCacheOrder.Enqueue(key);
        while (_blockCacheOrder.Count > MaxCachedBlocks)
            _blockCache.Remove(_blockCacheOrder.Dequeue());
    }

    /// <summary>Translates a block of text with one specific provider. Returns null when the
    /// provider produced nothing, so the caller can fall back.</summary>
    public async Task<string?> TranslateTextWithProviderAsync(
        string providerId, string text, string sourceLanguageCode = "en", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var (srcCode, srcName) = GetSourceLanguage(sourceLanguageCode);
        var (tgtCode, tgtName) = GetTargetLanguage();

        try
        {
            var translated = providerId switch
            {
                "gemini" => await _gemini.Value.TranslateTextAsync(text, srcName, tgtName, ct),
                "groq" => Groq() is { } g ? await g.TranslateTextAsync(text, srcName, tgtName, ct) : null,
                "cerebras" => Cerebras() is { } c ? await c.TranslateTextAsync(text, srcName, tgtName, ct) : null,
                "mymemory" => await MyMemoryTranslateAsync(text, srcCode, tgtCode, ct),
                "lingva" => await LingvaTranslateAsync(text, srcCode, tgtCode, ct),
                _ => await GoogleTranslateAsync(text, srcCode, tgtCode, ct),
            };

            // Every one of these endpoints echoes the input back when it can't translate; treat
            // that as "nothing useful" so the caller's fallback gets a turn.
            return string.IsNullOrWhiteSpace(translated) || translated == text ? null : translated;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Region translation provider {Provider} failed", providerId);
            return null;
        }
    }

    // ===================== Raw endpoints =====================

    private async Task<string?> GoogleTranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        try
        {
            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sourceLang}&tl={targetLang}&dt=t&q={Uri.EscapeDataString(text)}";
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
                return null;

            var segments = json.RootElement[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;

            var parts = new List<string>();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 &&
                    segment[0].GetString() is { Length: > 0 } part)
                    parts.Add(part);
            }

            return parts.Count == 0 ? null : string.Concat(parts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Google translation failed");
            return null;
        }
    }

    private async Task<string?> MyMemoryTranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={sourceLang}|{targetLang}";
        using var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("responseData", out var data) &&
               data.TryGetProperty("translatedText", out var textEl) &&
               textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString()
            : null;
    }

    // Public Lingva instances are community-run and individually flaky - tried in order, first
    // one to answer wins, instead of betting everything on a single host.
    //
    // Measured 2026-08: lingva.ml answers in ~3.4s; lingva.lunar.icu times out entirely and
    // translate.plausibility.cloud returns 500. Both dead hosts are kept, deliberately - they are
    // tried only after lingva.ml has already failed, so they cost nothing on the normal path, and
    // a community instance coming back online is exactly the sort of thing a fallback list should
    // pick up without a code change.
    private static readonly string[] LingvaInstances =
    {
        "https://lingva.ml",
        "https://lingva.lunar.icu",
        "https://translate.plausibility.cloud",
    };

    private async Task<string?> LingvaTranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        foreach (var instance in LingvaInstances)
        {
            try
            {
                var url = $"{instance}/api/v1/{sourceLang}/{targetLang}/{Uri.EscapeDataString(text)}";
                using var response = await _httpClient.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("translation", out var t) && t.GetString() is { Length: > 0 } value)
                    return value;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Lingva instance {Instance} failed", instance);
            }
        }
        return null;
    }

    // ===================== Health checks =====================

    /// <summary>
    /// Probes one source with a real request and reports how long it took. Backs the small status
    /// dot beside every source picker in Settings - the point is to answer "is this one working
    /// for me, right now", which only an actual request can.
    /// </summary>
    public async Task<ProviderProbe> TestProviderAsync(ProviderKind kind, string providerId, CancellationToken ct = default)
    {
        var descriptor = ProviderCatalog.Find(providerId, kind);
        if (descriptor == null) return ProviderProbe.Failed("Unknown source.");

        if (descriptor.RequiresKey && string.IsNullOrWhiteSpace(ResolveKey(providerId)))
            return ProviderProbe.NeedsKey();

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var ok = kind switch
            {
                ProviderKind.Dictionary => await ProbeDictionaryAsync(providerId, ct),
                ProviderKind.Translation => await ProbeTranslationAsync(providerId, ct),
                ProviderKind.Encyclopedia => await ProbeEncyclopediaAsync(providerId, ct),
                _ => false,
            };

            return ok
                ? ProviderProbe.Ok((int)started.ElapsedMilliseconds)
                : ProviderProbe.Failed("No response - the service may be down or rate limited.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Provider probe failed for {Provider}", providerId);
            return ProviderProbe.Failed("Could not reach the service.");
        }
    }

    private async Task<bool> ProbeDictionaryAsync(string providerId, CancellationToken ct)
    {
        if (providerId == "gemini")
            return await _gemini.Value.TestConnectionAsync(ct) is AiStatus.Ok;

        var result = await LookupDictionaryAsync(providerId, "language", "en", ct);
        return result.Error == null && result.Meanings.Length > 0;
    }

    /// <summary>Probes with a name every source should know, so a failure here means the source is
    /// unreachable rather than merely ignorant.</summary>
    private async Task<bool> ProbeEncyclopediaAsync(string providerId, CancellationToken ct) =>
        await LookupOneEncyclopediaAsync(providerId, "Nvidia", "en", EncyclopediaService.ShortSummaryLength, ct) != null;

    private async Task<bool> ProbeTranslationAsync(string providerId, CancellationToken ct)
    {
        if (providerId == "gemini")
            return await _gemini.Value.TestConnectionAsync(ct) is AiStatus.Ok;

        if (providerId is "groq" or "cerebras")
        {
            var service = providerId == "groq" ? Groq() : Cerebras();
            return service != null && await service.TestAsync(ct);
        }

        var translated = await TranslateTextWithProviderAsync(providerId, "language", "en", ct);
        return !string.IsNullOrWhiteSpace(translated);
    }

    /// <summary>Runs the Gemini model chain once and reports which model answered.</summary>
    public Task<AiStatus> TestGeminiConnectionAsync(CancellationToken ct = default) =>
        _gemini.Value.TestConnectionAsync(ct);

    private static string BaseTag(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return "en";
        var dash = languageCode.IndexOf('-');
        return (dash > 0 ? languageCode[..dash] : languageCode).ToLowerInvariant();
    }
}

/// <summary>Outcome of a single "is this source working" probe.</summary>
public sealed record ProviderProbe(bool Success, int LatencyMs, string? Message)
{
    public static ProviderProbe Ok(int latencyMs) => new(true, latencyMs, null);
    public static ProviderProbe Failed(string message) => new(false, 0, message);
    public static ProviderProbe NeedsKey() => new(false, 0, "Needs an API key.");
}
