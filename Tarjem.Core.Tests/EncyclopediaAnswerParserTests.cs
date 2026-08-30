using Tarjem.Core.Translation;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// The gate between "a model said something" and "the card shows it as fact".
///
/// Everything here is a shape a real model has produced when asked what an obscure proper noun is.
/// The ones that must be rejected matter more than the one that must pass: an answer that gets
/// through is rendered identically to a Wikipedia article, so anything less than a confident,
/// substantive reply has to be dropped and left to the next source in the chain.
/// </summary>
public class EncyclopediaAnswerParserTests
{
    [Fact]
    public void AcceptsAConfidentAnswer()
    {
        var answer = EncyclopediaAnswerParser.Parse(
            """
            {"known": true, "title": "Nvidia", "kind": "American technology company",
             "summary": "Nvidia designs graphics processing units for gaming and professional markets."}
            """);

        Assert.NotNull(answer);
        Assert.Equal("Nvidia", answer.Title);
        Assert.Equal("American technology company", answer.Kind);
        Assert.StartsWith("Nvidia designs", answer.Summary);
    }

    [Fact]
    public void ReadsThroughMarkdownFences()
    {
        var answer = EncyclopediaAnswerParser.Parse(
            "```json\n{\"known\": true, \"title\": \"Genshin Impact\", \"kind\": \"action role-playing game\", " +
            "\"summary\": \"An open-world action role-playing game developed by miHoYo.\"}\n```");

        Assert.NotNull(answer);
        Assert.Equal("Genshin Impact", answer.Title);
    }

    [Fact]
    public void ReadsThroughSurroundingProse()
    {
        var answer = EncyclopediaAnswerParser.Parse(
            "Sure! Here is the JSON you asked for:\n" +
            "{\"known\": true, \"title\": \"Blender\", \"kind\": \"3D graphics software\", " +
            "\"summary\": \"A free and open-source 3D creation suite used for modelling and animation.\"}\n" +
            "Let me know if you need anything else.");

        Assert.NotNull(answer);
        Assert.Equal("Blender", answer.Title);
    }

    [Fact]
    public void RejectsAnAdmittedNonAnswer()
    {
        Assert.Null(EncyclopediaAnswerParser.Parse(
            """{"known": false, "title": "asdfqwer", "kind": "", "summary": ""}"""));
    }

    /// <summary>The dangerous shape: the model claims to know and then says nothing, which used to
    /// reach the card as a confident-looking blank instead of falling through to the next source.</summary>
    [Fact]
    public void RejectsAConfidentClaimWithNoSubstance()
    {
        Assert.Null(EncyclopediaAnswerParser.Parse(
            """{"known": true, "title": "Xyzzy", "kind": "", "summary": ""}"""));

        Assert.Null(EncyclopediaAnswerParser.Parse(
            """{"known": true, "title": "Xyzzy", "kind": "", "summary": "Unknown."}"""));
    }

    /// <summary>A model that dropped the flag wasn't following the schema, and an identity claim is
    /// not the place to give one the benefit of the doubt.</summary>
    [Fact]
    public void RejectsAnAnswerMissingTheKnownFlag()
    {
        Assert.Null(EncyclopediaAnswerParser.Parse(
            """{"title": "Nvidia", "kind": "company", "summary": "Nvidia makes graphics cards."}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I'm sorry, I don't know what that is.")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("[1, 2, 3]")]
    public void RejectsAnythingThatIsNotAnAnswerObject(string? content)
    {
        Assert.Null(EncyclopediaAnswerParser.Parse(content));
    }

    [Fact]
    public void KeepsAnAnswerWhoseTitleIsMissing()
    {
        // Caller substitutes the term it asked about; a blank title must not sink a real summary.
        var answer = EncyclopediaAnswerParser.Parse(
            """{"known": true, "kind": "Japanese animation studio", "summary": "A studio known for its television anime."}""");

        Assert.NotNull(answer);
        Assert.Equal("", answer.Title);
        Assert.Equal("Japanese animation studio", answer.Kind);
    }
}
