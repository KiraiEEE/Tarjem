using Tarjem.Core.Providers;
using Xunit;

namespace Tarjem.Core.Tests;

/// <summary>
/// The order Alt+E asks its sources in.
///
/// This is the one chain in the app where rank is a correctness question rather than a latency one:
/// the wikis return a citable page and the AI sources return a recollection, so a change that lets
/// a model answer ahead of Wikipedia changes what the card is claiming, not just how fast it says
/// it.
/// </summary>
public class EncyclopediaChainTests
{
    private static bool HasEveryKey(ProviderDescriptor _) => true;
    private static bool HasNoKeys(ProviderDescriptor p) => !p.RequiresKey;

    [Fact]
    public void WikisComeBeforeTheModels()
    {
        var ids = ProviderCatalog.Usable(ProviderKind.Encyclopedia, "en", HasEveryKey).Select(p => p.Id).ToList();

        Assert.Equal(["wikipedia", "wikidata", "gemini", "groq", "cerebras"], ids);
    }

    [Fact]
    public void RecommendsAKeylessSourceOnAFreshInstall()
    {
        var recommended = ProviderCatalog.RecommendedFor(ProviderKind.Encyclopedia, "en", HasNoKeys);

        Assert.NotNull(recommended);
        Assert.Equal("wikipedia", recommended.Id);
        Assert.False(recommended.RequiresKey);
    }

    /// <summary>Without a key an AI source can only ever produce an error, so it is dropped from
    /// the chain rather than tried and skipped.</summary>
    [Fact]
    public void DropsKeyedSourcesWhenNoKeyIsPresent()
    {
        var ids = ProviderCatalog.Usable(ProviderKind.Encyclopedia, "en", HasNoKeys).Select(p => p.Id).ToList();

        Assert.Equal(["wikipedia", "wikidata"], ids);
    }

    [Fact]
    public void PuttingAModelFirstStillFallsBackToTheWikis()
    {
        var ids = ProviderCatalog
            .FailoverChain(ProviderKind.Encyclopedia, "gemini", "en", HasEveryKey)
            .Select(p => p.Id)
            .ToList();

        Assert.Equal("gemini", ids[0]);
        Assert.Contains("wikipedia", ids);
        Assert.Contains("wikidata", ids);
    }

    /// <summary>Every AI encyclopedia source must reuse a provider id the app already stores a key
    /// under - a new id here would silently never have a key and never be offered.</summary>
    [Fact]
    public void KeyedEncyclopediaSourcesShareTheirIdWithAnExistingKeyedProvider()
    {
        var keyedElsewhere = ProviderCatalog.All
            .Where(p => p.RequiresKey && p.Kind != ProviderKind.Encyclopedia)
            .Select(p => p.Id)
            .ToHashSet();

        foreach (var provider in ProviderCatalog.OfKind(ProviderKind.Encyclopedia).Where(p => p.RequiresKey))
        {
            Assert.Contains(provider.Id, keyedElsewhere);
            Assert.False(string.IsNullOrWhiteSpace(provider.KeyUrl));
        }
    }
}
