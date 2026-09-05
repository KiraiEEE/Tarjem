namespace Tarjem.Core.Providers;

public enum ProviderKind
{
    /// <summary>Defines a word in its own language (definition, part of speech, synonyms).</summary>
    Dictionary,
    /// <summary>Translates a word or a block of text into the target language.</summary>
    Translation,
    /// <summary>Explains what a proper noun *is* ("nvidia" -> a company that makes GPUs).
    /// Not a dictionary: these words have no definition, only an identity.</summary>
    Encyclopedia,
}

/// <summary>
/// Everything the app needs to know about one source without having constructed it: whether it
/// can handle a given language, whether it needs a key, where to get one, and how good it is
/// relative to its peers.
/// </summary>
/// <param name="Rank">Lower is better. Drives both the recommended default for a language pair
/// and the order the automatic failover walks. Ties are broken by declaration order.</param>
/// <param name="Languages">Language codes this source handles, or null for "anything". Codes are
/// matched on their base subtag, so "zh" covers both "zh-CN" and "zh-TW".</param>
/// <param name="KeyUrl">Where a user goes to get their own key. Also what generates the
/// API-keys.txt handout, so every keyed provider must have one.</param>
public sealed record ProviderDescriptor(
    string Id,
    string Label,
    ProviderKind Kind,
    int Rank,
    bool RequiresKey = false,
    IReadOnlySet<string>? Languages = null,
    string? KeyUrl = null,
    string? Note = null)
{
    /// <summary>True when this source can handle the given language code. Unknown/empty codes
    /// are treated as supported - refusing to even try is worse than a failed request that the
    /// failover chain will simply step past.</summary>
    public bool Supports(string? languageCode)
    {
        if (Languages == null || string.IsNullOrEmpty(languageCode))
            return true;

        var dash = languageCode.IndexOf('-');
        var baseTag = dash > 0 ? languageCode[..dash] : languageCode;
        return Languages.Contains(baseTag);
    }
}

/// <summary>
/// The single list of every source Tarjem can talk to. Nothing here constructs a client or opens
/// a socket - it is pure data, read at startup to populate dropdowns and pick defaults, so the
/// app pays nothing for the sources a given user never touches.
/// </summary>
public static class ProviderCatalog
{
    private static readonly HashSet<string> EnglishOnly = new(StringComparer.OrdinalIgnoreCase) { "en" };

    /// <summary>Languages FreeDictionaryAPI/Wiktionary have real coverage for. Wider than
    /// English-only, thinner than "everything" - Persian and Urdu in particular are sparse
    /// enough that they must not be anyone's recommended default.</summary>
    private static readonly HashSet<string> WiktionaryLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "es", "fr", "de", "it", "pt", "ru", "nl", "pl", "ja", "ko", "zh", "tr", "ar", "hi", "ur", "fa",
    };

    /// <summary>
    /// Ranks below are ordered by measured round-trip time against a live probe (2026-08), not by
    /// guesswork. Re-run <c>Settings > check</c> on each source if these ever feel wrong; the
    /// numbers that produced this order were:
    ///
    ///   Wikipedia 0.7s · Google 1.25s · FreeDictionary 1.33s · Gemini 1.1-1.8s · Wiktionary 1.6s
    ///   Wikidata 1.8s · dictionaryapi.dev 1.9s · MyMemory 2.1s · Lingva 3.4s
    ///
    /// Keyed sources are ranked on quality rather than latency, because the failover chain drops
    /// them entirely when no key is present - they only ever compete when the user has opted in.
    /// </summary>
    public static readonly IReadOnlyList<ProviderDescriptor> All =
    [
        // ── Dictionary ──────────────────────────────────────────────────────────────────
        // FreeDictionaryAPI is both the fastest and the widest: same Wiktionary data as the
        // others, no key, and it answers for 17 languages instead of only English.
        new("freedictionaryapi.com", "Free Dictionary", ProviderKind.Dictionary, Rank: 10,
            Languages: WiktionaryLanguages,
            Note: "No key needed. 1,000 lookups per hour."),

        new("wiktionary", "Wiktionary", ProviderKind.Dictionary, Rank: 20,
            Languages: WiktionaryLanguages,
            Note: "No key needed. Broadest coverage, plainest formatting."),

        new("dictionaryapi.dev", "Dictionary API", ProviderKind.Dictionary, Rank: 30,
            Languages: EnglishOnly,
            Note: "No key needed. English only, but the richest English entries."),

        new("gemini", "Gemini AI", ProviderKind.Dictionary, Rank: 40, RequiresKey: true,
            KeyUrl: "https://aistudio.google.com/apikey",
            Note: "Reads the word in context and handles any language, but spends a request per lookup."),

        new("merriam-webster", "Merriam-Webster", ProviderKind.Dictionary, Rank: 50, RequiresKey: true,
            Languages: EnglishOnly,
            KeyUrl: "https://dictionaryapi.com/register/index",
            Note: "Free key. Authoritative American English."),

        // ── Translation ─────────────────────────────────────────────────────────────────
        // Google is the default because it is keyless and the fastest measured. The AI providers
        // translate better and are one switch away in the popup, but making one the automatic
        // default would spend a model request on every single hover.
        new("google", "Google Translate", ProviderKind.Translation, Rank: 10,
            Note: "No key needed. Fast and reliable."),

        new("gemini", "Gemini AI", ProviderKind.Translation, Rank: 20, RequiresKey: true,
            KeyUrl: "https://aistudio.google.com/apikey",
            Note: "Best quality - rewrites naturally instead of word for word."),

        new("groq", "Groq (Llama)", ProviderKind.Translation, Rank: 30, RequiresKey: true,
            KeyUrl: "https://console.groq.com/keys",
            Note: "Free key, no card. The fastest AI translation available."),

        new("cerebras", "Cerebras", ProviderKind.Translation, Rank: 40, RequiresKey: true,
            KeyUrl: "https://cloud.cerebras.ai/",
            Note: "Free key, no card. 1M tokens per day."),

        new("mymemory", "MyMemory", ProviderKind.Translation, Rank: 50,
            Note: "No key needed. 5,000 characters per day."),

        // Ranked last on measured latency, and it is the flakiest of the keyless sources: two of
        // its three public instances were dead when this was last checked.
        new("lingva", "Lingva", ProviderKind.Translation, Rank: 60,
            Note: "No key needed. Community-run mirror of Google Translate."),

        // ── Encyclopedia ────────────────────────────────────────────────────────────────
        new("wikipedia", "Wikipedia", ProviderKind.Encyclopedia, Rank: 10,
            Note: "No key needed. A paragraph on whatever the name refers to."),

        // Wikidata answers where Wikipedia has no article: it holds a one-line description for
        // vastly more entities (products, minor characters, companies) than have prose pages.
        new("wikidata", "Wikidata", ProviderKind.Encyclopedia, Rank: 20,
            Note: "No key needed. One-line descriptions, including things Wikipedia has no article for."),

        // Ranked below both wikis on purpose. These answer for the genuine long tail - a character
        // from a game, a small studio, a piece of slang-turned-product - that neither wiki carries
        // an entity for at all. But they are models rather than records: they are asked to say when
        // they don't know (see TranslationPrompts.EncyclopediaRequest), and an answer that survives
        // that is still a recollection, not a citation. Sources that can cite come first.
        new("gemini", "Gemini AI", ProviderKind.Encyclopedia, Rank: 30, RequiresKey: true,
            KeyUrl: "https://aistudio.google.com/apikey",
            Note: "Answers for names no wiki has an entry for. From the model's memory, so it can be wrong."),

        new("groq", "Groq (Llama)", ProviderKind.Encyclopedia, Rank: 40, RequiresKey: true,
            KeyUrl: "https://console.groq.com/keys",
            Note: "Free key, no card. The fastest of the AI sources."),

        new("cerebras", "Cerebras", ProviderKind.Encyclopedia, Rank: 50, RequiresKey: true,
            KeyUrl: "https://cloud.cerebras.ai/",
            Note: "Free key, no card. 1M tokens per day."),
    ];

    public static IEnumerable<ProviderDescriptor> OfKind(ProviderKind kind) =>
        All.Where(p => p.Kind == kind);

    public static ProviderDescriptor? Find(string id, ProviderKind kind) =>
        All.FirstOrDefault(p => p.Id == id && p.Kind == kind);

    /// <summary>Every source of this kind that can handle <paramref name="languageCode"/>, best
    /// first. <paramref name="hasKey"/> reports whether a keyed provider is actually usable right
    /// now; sources whose key is missing are dropped rather than ranked last, since attempting
    /// them can only ever produce an error.</summary>
    public static IReadOnlyList<ProviderDescriptor> Usable(
        ProviderKind kind, string? languageCode, Func<ProviderDescriptor, bool> hasKey) =>
        OfKind(kind)
            .Where(p => p.Supports(languageCode))
            .Where(p => !p.RequiresKey || hasKey(p))
            .OrderBy(p => p.Rank)
            .ToList();

    /// <summary>The source to preselect for a language pair on a fresh install - the best-ranked
    /// one that works without the user having to obtain anything first. Falls back to the
    /// best-ranked source overall if every candidate needs a key.</summary>
    public static ProviderDescriptor? RecommendedFor(
        ProviderKind kind, string? languageCode, Func<ProviderDescriptor, bool> hasKey)
    {
        var usable = Usable(kind, languageCode, hasKey);
        return usable.FirstOrDefault(p => !p.RequiresKey) ?? usable.FirstOrDefault();
    }

    /// <summary>
    /// The order the automatic failover tries sources in: whatever the user chose first (their
    /// choice is never silently overridden), then every other usable source by rank. This is
    /// what turns "Gemini is down" from a failed lookup into a slightly different-looking
    /// successful one - the setting is not rewritten, only this one lookup is rerouted.
    /// </summary>
    public static IReadOnlyList<ProviderDescriptor> FailoverChain(
        ProviderKind kind, string? preferredId, string? languageCode, Func<ProviderDescriptor, bool> hasKey)
    {
        var usable = Usable(kind, languageCode, hasKey);
        var preferred = usable.FirstOrDefault(p => p.Id == preferredId);
        if (preferred == null)
            return usable;

        return new[] { preferred }.Concat(usable.Where(p => p.Id != preferredId)).ToList();
    }

    /// <summary>Every keyed provider and where to get its key. Read at packaging time (see
    /// <c>BundledKeys.Pack</c>) to report which keyed sources the build is shipping without, so a
    /// new keyed provider can't be added and then silently forgotten at packaging time.</summary>
    public static IEnumerable<ProviderDescriptor> KeyedProviders() =>
        All.Where(p => p.RequiresKey).DistinctBy(p => p.Id).OrderBy(p => p.Rank);
}
