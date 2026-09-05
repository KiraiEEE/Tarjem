using System.Net;
using System.Text;

namespace Tarjem.Core.Language;

/// <summary>
/// Flattens the small HTML fragments Wiktionary returns inside its JSON ("a <i>large</i> feline")
/// into the plain text the popup renders. Deliberately not a parser - these are single-sentence
/// fragments with inline markup, and pulling in an HTML library to strip a handful of tags would
/// cost more than the entire feature is worth.
/// </summary>
public static class HtmlText
{
    public static string Strip(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var sb = new StringBuilder(html.Length);
        var insideTag = false;

        foreach (var c in html)
        {
            if (c == '<') insideTag = true;
            else if (c == '>') insideTag = false;
            else if (!insideTag) sb.Append(c);
        }

        var text = WebUtility.HtmlDecode(sb.ToString());

        // Tag removal leaves runs of whitespace where the markup used to be.
        var collapsed = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && lastWasSpace) continue;
            collapsed.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;
        }

        return collapsed.ToString().Trim();
    }
}
