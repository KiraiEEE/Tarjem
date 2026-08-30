using Tarjem.Core.Language;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// Telling a cross-reference apart from a definition.
///
/// The false positives matter far more than the false negatives here. Missing one leaves the popup
/// saying "plural of revelation", which is the bug; but following a definition that merely contains
/// the word "of" silently replaces a correct answer with a different word's, and the reader has no
/// way to tell. Every "not a reference" case below is a real dictionary definition.
/// </summary>
public class InflectionParserTests
{
    [Theory]
    [InlineData("plural of revelation", "revelation", "plural")]
    [InlineData("Plural of mouse.", "mouse", "Plural")]
    [InlineData("present participle and gerund of run", "run", "present participle and gerund")]
    [InlineData("simple past tense and past participle of walk", "walk", "simple past tense and past participle")]
    [InlineData("third-person singular simple present indicative of go", "go", "third-person singular simple present indicative")]
    [InlineData("comparative of good", "good", "comparative")]
    [InlineData("superlative of bad", "bad", "superlative")]
    [InlineData("alternative spelling of colour", "colour", "alternative spelling")]
    [InlineData("obsolete form of music", "music", "obsolete form")]
    [InlineData("common misspelling of separate", "separate", "common misspelling")]
    [InlineData("(informal) plural of thing", "thing", "plural")]
    [InlineData("[archaic] singular of kine", "kine", "singular")]
    public void FollowsACrossReference(string definition, string expectedBase, string expectedLabel)
    {
        var form = InflectionParser.Parse(definition);

        Assert.NotNull(form);
        Assert.Equal(expectedBase, form.BaseWord);
        Assert.Equal(expectedLabel, form.Label);
    }

    [Theory]
    // Starts with an article, which no grammatical cross-reference does - this is the single test
    // carrying most of the weight against false positives.
    [InlineData("A form of energy.")]
    [InlineData("A member of a group of people.")]
    [InlineData("The act of revealing or disclosing.")]
    [InlineData("An instance of stealing.")]
    // Real definitions that happen to end in "of <word>".
    [InlineData("Form of address.")]
    [InlineData("Made of wood.")]
    // Carries on past the word it names, so it is defining rather than pointing.
    [InlineData("plural of revelation, used in the title of the last book of the Bible")]
    // No grammatical vocabulary at all.
    [InlineData("Consisting of tin.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LeavesARealDefinitionAlone(string? definition)
    {
        Assert.Null(InflectionParser.Parse(definition));
    }

    [Fact]
    public void KeepsRealSensesAheadOfReferences()
    {
        // "mice" as the source actually returns it: the pointer first, the real sense after.
        var (real, references) = InflectionParser.Partition(
        [
            "plural of mouse",
            "To hunt or catch mice.",
        ]);

        Assert.Equal(["To hunt or catch mice."], real);
        Assert.Equal(["plural of mouse"], references);
    }

    [Fact]
    public void RedirectsWhenEveryDefinitionIsAReference()
    {
        var form = InflectionParser.RedirectFor(["plural of revelation"], "revelations");

        Assert.NotNull(form);
        Assert.Equal("revelation", form.BaseWord);
    }

    /// <summary>Two references agreeing is still one answer - "walked" is both a past tense and a
    /// past participle of the same verb.</summary>
    [Fact]
    public void RedirectsWhenSeveralReferencesAgree()
    {
        var form = InflectionParser.RedirectFor(
            ["simple past tense of walk", "past participle of walk"], "walked");

        Assert.NotNull(form);
        Assert.Equal("walk", form.BaseWord);
    }

    [Fact]
    public void DoesNotRedirectWhenARealDefinitionIsPresent()
    {
        Assert.Null(InflectionParser.RedirectFor(
            ["plural of mouse", "To hunt or catch mice."], "mice"));
    }

    /// <summary>A homograph pointing two different directions has no single right answer, so it
    /// keeps what the source said rather than picking one at random.</summary>
    [Fact]
    public void DoesNotRedirectWhenReferencesDisagree()
    {
        Assert.Null(InflectionParser.RedirectFor(
            ["plural of axis", "plural of axe"], "axes"));
    }

    /// <summary>Guards the loop: a source that answers "plural of X" for X itself must not send the
    /// lookup straight back where it came from.</summary>
    [Fact]
    public void DoesNotRedirectToTheWordItWasAskedAbout()
    {
        Assert.Null(InflectionParser.RedirectFor(["plural of sheep"], "sheep"));
        Assert.Null(InflectionParser.RedirectFor(["plural of Sheep"], "sheep"));
    }

    [Fact]
    public void DoesNotRedirectOnAnEmptyAnswer()
    {
        Assert.Null(InflectionParser.RedirectFor([], "anything"));
    }
}
