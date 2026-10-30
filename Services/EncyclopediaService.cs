using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace Tarjem.Services;

/// <summary>What a name refers to, plus where that came from.</summary>
public sealed record EncyclopediaEntry(string Title, string Summary, string? Description, string SourceUrl);

/// <summary>
/// Answers "what *is* this?" for words no dictionary will ever carry - NVIDIA, Photoshop, Genshin
/// Impact, a person's name. A dictionary returning "no definition found" for these is technically
/// correct and completely useless; Wikipedia's summary endpoint gives a one-paragraph answer with
/// no key, no registration, and a single fast request.
/// </summary>
public sealed class EncyclopediaService
{
    private readonly HttpClient _http;

    public EncyclopediaService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // Wikimedia asks every client to identify itself and rate-limits anonymous defaults harder.
        _http.DefaultRequestHeaders.Add("User-Agent", "Tarjem/0.4 (https://github.com/IdleBuilt/Tarjem)");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Looks <paramref name="term"/> up in the Wikipedia edition for <paramref name="languageCode"/>.
    /// Returns null for anything that isn't a confident, specific answer - a disambiguation page
    /// ("Mercury may refer to...") or a miss both come back as null, because showing the user a
    /// list of things their word might have meant is worse than showing nothing.
    /// </summary>
    /// <summary>
    /// Wikipedia first, then Wikidata. Wikipedia gives a paragraph but only for things with a
    /// prose article; Wikidata holds a one-line description for far more entities - products,
    /// minor characters, small companies - which is exactly the long tail this feature exists for.
    /// A short true answer beats "no definition found".
    /// </summary>
    /// <summary>
    /// Asks one specific wiki source. The order they are tried in - and the AI sources that now sit
    /// behind them - is <see cref="TranslationService"/>'s business, because that is where the
    /// provider catalog, the user's chosen default and the API keys all live. This class only knows
    /// how to talk to Wikimedia.
    /// </summary>
    /// <param name="maxSummaryLength">How much of the article's opening paragraph to keep, cut on a
    /// sentence boundary - the "Answer length" setting. Wikidata answers are a single line and are
    /// never trimmed.</param>
    /// <returns>Null for an unrecognized provider id as well as for a miss, so a stale setting
    /// degrades into the next source in the chain rather than throwing.</returns>
    public async Task<EncyclopediaEntry?> LookupAsync(
        string providerId, string term, string languageCode = "en",
        int maxSummaryLength = StandardSummaryLength, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;

        return providerId switch
        {
            "wikipedia" => await LookupWikipediaAsync(term, languageCode, maxSummaryLength, ct),
            "wikidata" => await LookupWikidataAsync(term, languageCode, ct),
            _ => null,
        };
    }

    public const int ShortSummaryLength = 140;
    public const int StandardSummaryLength = 260;
    public const int FullSummaryLength = 700;

    /// <summary>Maps the stored "Answer length" id onto a character budget.</summary>
    public static int SummaryLengthFor(string? setting) => setting switch
    {
        "Short" => ShortSummaryLength,
        "Full" => FullSummaryLength,
        _ => StandardSummaryLength,
    };

    private async Task<EncyclopediaEntry?> LookupWikipediaAsync(
        string term, string languageCode, int maxSummaryLength, CancellationToken ct)
    {
        var edition = EditionFor(languageCode);
        var url = $"https://{edition}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(term.Trim())}?redirect=true";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            // "disambiguation" and "no-extract" pages are hits at the HTTP level but non-answers
            // in substance.
            if (root.TryGetProperty("type", out var type) &&
                type.GetString() is string t && t.Contains("disambiguation", StringComparison.OrdinalIgnoreCase))
                return null;

            var summary = root.TryGetProperty("extract", out var extract) ? extract.GetString() : null;
            if (string.IsNullOrWhiteSpace(summary)) return null;

            var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? term : term;
            var description = root.TryGetProperty("description", out var descEl) ? descEl.GetString() : null;

            var sourceUrl = root.TryGetProperty("content_urls", out var urls) &&
                            urls.TryGetProperty("desktop", out var desktop) &&
                            desktop.TryGetProperty("page", out var page)
                ? page.GetString() ?? url
                : url;

            return new EncyclopediaEntry(title, FirstSentences(summary, maxSummaryLength), description, sourceUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Wikipedia lookup failed for {Term}", term);
            return null;
        }
    }

    /// <summary>
    /// Wikidata's entity search, which returns a label plus a one-line description ("American
    /// multinational technology company"). Not a paragraph, but for a name Wikipedia has no
    /// article about it is the difference between an answer and nothing.
    /// </summary>
    private async Task<EncyclopediaEntry?> LookupWikidataAsync(string term, string languageCode, CancellationToken ct)
    {
        var language = EditionFor(languageCode);
        var url = "https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&limit=1" +
                  $"&language={language}&uselang={language}&search={Uri.EscapeDataString(term.Trim())}";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("search", out var results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return null;

            var top = results[0];

            // No description means Wikidata has the entity but nothing to say about it, which is
            // no more useful than a miss.
            if (!top.TryGetProperty("description", out var descEl) ||
                descEl.GetString() is not { Length: > 0 } description)
                return null;

            var title = top.TryGetProperty("label", out var labelEl) ? labelEl.GetString() ?? term : term;
            var id = top.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

            return new EncyclopediaEntry(
                title,
                // Read as a sentence rather than a bare fragment, since it lands under "About".
                char.ToUpperInvariant(description[0]) + description[1..],
                Description: null,
                SourceUrl: id is null ? url : $"https://www.wikidata.org/wiki/{id}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Wikidata lookup failed for {Term}", term);
            return null;
        }
    }

    /// <summary>
    /// Whether a word is worth asking Wikipedia about. This gate matters: the encyclopedia lookup
    /// only ever runs after a dictionary has already come back empty, and firing it on every
    /// mistyped or OCR-mangled word would add a wasted request to exactly the lookups that are
    /// already slowest. Capitalization, digits and multi-word phrases are what actually separate
    /// a proper noun from a garbled common word.
    /// </summary>
    public static bool LooksLikeAName(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 2) return false;

        var trimmed = word.Trim();
        if (trimmed.Length > 60) return false;

        var hasUpper = char.IsUpper(trimmed[0]);
        var allCaps = trimmed.All(c => !char.IsLetter(c) || char.IsUpper(c));
        var hasDigit = trimmed.Any(char.IsDigit);
        var multiWord = trimmed.Contains(' ');

        return hasUpper || allCaps || hasDigit || multiWord;
    }

    /// <summary>Wikipedia's first paragraph runs long; the popup has room for a line or two.
    /// Cut on sentence boundaries so the result never ends mid-clause.</summary>
    private static string FirstSentences(string text, int maxLength = StandardSummaryLength)
    {
        var clean = text.Replace('\n', ' ').Trim();
        if (clean.Length <= maxLength) return clean;

        var cut = clean.LastIndexOf(". ", maxLength, StringComparison.Ordinal);
        return cut > 60 ? clean[..(cut + 1)] : clean[..maxLength].TrimEnd() + "…";
    }

    /// <summary>Wikipedia editions are keyed by base language tag - there is no zh-CN edition,
    /// only zh (plus a separate zh-yue and so on we don't target).</summary>
    private static string EditionFor(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return "en";
        var dash = languageCode.IndexOf('-');
        return (dash > 0 ? languageCode[..dash] : languageCode).ToLowerInvariant();
    }
}
