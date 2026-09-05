using System.Text.Json;

namespace Tarjem.Core.Translation;

/// <summary>What an AI provider said a name refers to.</summary>
/// <param name="Title">The name, written properly.</param>
/// <param name="Kind">The short descriptor ("American technology company"), or empty.</param>
/// <param name="Summary">One to three sentences in the source language.</param>
public sealed record EncyclopediaAnswer(string Title, string Kind, string Summary);

/// <summary>
/// Reads the JSON an AI provider returns for a "what is this?" lookup - see
/// <see cref="TranslationPrompts.EncyclopediaRequest"/>.
///
/// Shared by Gemini, Groq and Cerebras, and deliberately strict about the one thing that matters:
/// an answer only counts if the model said it knew and then actually produced a summary. A model
/// that sets <c>known: true</c> and leaves the summary empty has not answered, and neither has one
/// that returned an apology in place of a fact - both used to reach the card as a confident-looking
/// blank.
/// </summary>
public static class EncyclopediaAnswerParser
{
    /// <summary>Below this a "summary" is a refusal, a label, or a fragment, not an answer.</summary>
    private const int MinimumSummaryLength = 12;

    public static EncyclopediaAnswer? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        using var doc = LenientJson.Parse(content);
        if (doc == null) return null;

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Absent is treated as "not known": a model that dropped the field is a model that wasn't
        // following the schema, which is not a state to trust an identity claim from.
        if (!root.TryGetProperty("known", out var known) || known.ValueKind != JsonValueKind.True)
            return null;

        var summary = Text(root, "summary");
        if (summary.Length < MinimumSummaryLength) return null;

        var title = Text(root, "title");
        return new EncyclopediaAnswer(title, Text(root, "kind"), summary);
    }

    private static string Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()?.Trim() ?? ""
            : "";
}

/// <summary>
/// JSON parsing that tolerates what models actually send when told to return only JSON: a fenced
/// block, or the object with a sentence of commentary wrapped around it.
/// </summary>
public static class LenientJson
{
    /// <summary>Returns null rather than throwing - a body that isn't JSON at all is a normal
    /// outcome here (a captive portal, a proxy error page, a model that ignored the instruction),
    /// and it must leave the failover chain free to try the next source.</summary>
    public static JsonDocument? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();

        if (TryParse(text, out var doc)) return doc;

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];

            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                text = text[..fenceEnd];

            text = text.Trim();
            if (TryParse(text, out doc)) return doc;
        }

        // Last resort: the outermost { ... } block, for a model that prefaced its JSON with prose.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start && TryParse(text[start..(end + 1)], out doc))
            return doc;

        return null;
    }

    private static bool TryParse(string text, out JsonDocument? doc)
    {
        try
        {
            doc = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            doc = null;
            return false;
        }
    }
}
