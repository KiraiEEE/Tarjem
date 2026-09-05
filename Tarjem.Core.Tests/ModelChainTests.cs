using Tarjem.Core.Translation;

namespace Tarjem.Core.Tests;

public class ModelChainTests
{
    private static readonly GeminiModel[] Chain =
    [
        new("model-a", 4000),
        new("model-b", 4000),
        new("model-c", 9000)
    ];

    [Fact]
    public void UnknownModel_WithNoHealthEntry_IsUsable()
    {
        var health = new Dictionary<string, ModelHealth>();

        var usable = ModelChain.UsableInOrder(Chain, health, DateTimeOffset.UtcNow).ToList();

        Assert.Equal(Chain, usable);
    }

    [Fact]
    public void RetiredModel_IsSkipped_WhileCoolingDown()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new Dictionary<string, ModelHealth>
        {
            ["model-a"] = new ModelHealth { Id = "model-a", State = ModelState.Retired, CooldownUntil = now.AddDays(7) }
        };

        var usable = ModelChain.UsableInOrder(Chain, health, now).ToList();

        Assert.DoesNotContain(Chain[0], usable);
        Assert.Equal(2, usable.Count);
    }

    [Fact]
    public void UnauthorizedModel_IsSkipped_WhileCoolingDown()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new Dictionary<string, ModelHealth>
        {
            ["model-b"] = new ModelHealth { Id = "model-b", State = ModelState.Unauthorized, CooldownUntil = now.AddMinutes(30) }
        };

        var usable = ModelChain.UsableInOrder(Chain, health, now).ToList();

        Assert.DoesNotContain(Chain[1], usable);
    }

    /// <summary>The regression that killed AI translation in 0.3.1: a key Google had flagged
    /// marked all three models Unauthorized, that verdict was persisted, and the chain then
    /// yielded nothing forever - so the app never noticed the key working again.</summary>
    [Fact]
    public void UnauthorizedModel_IsRetried_AfterCooldownExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new Dictionary<string, ModelHealth>
        {
            ["model-b"] = new ModelHealth { Id = "model-b", State = ModelState.Unauthorized, CooldownUntil = now.AddSeconds(-1) }
        };

        var usable = ModelChain.UsableInOrder(Chain, health, now).ToList();

        Assert.Contains(Chain[1], usable);
    }

    [Fact]
    public void RetiredModel_IsRetried_AfterCooldownExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new Dictionary<string, ModelHealth>
        {
            ["model-a"] = new ModelHealth { Id = "model-a", State = ModelState.Retired, CooldownUntil = now.AddSeconds(-1) }
        };

        var usable = ModelChain.UsableInOrder(Chain, health, now).ToList();

        Assert.Contains(Chain[0], usable);
    }

    /// <summary>An unhealthy verdict with no expiry - which is what every persisted entry
    /// written by the old code looks like - must not strand the model forever.</summary>
    [Fact]
    public void UnhealthyModel_WithNoCooldown_IsUsable()
    {
        var health = new Dictionary<string, ModelHealth>
        {
            ["model-a"] = new ModelHealth { Id = "model-a", State = ModelState.Unauthorized, CooldownUntil = null }
        };

        var usable = ModelChain.UsableInOrder(Chain, health, DateTimeOffset.UtcNow).ToList();

        Assert.Contains(Chain[0], usable);
    }

    [Theory]
    [InlineData(ModelState.RateLimited)]
    [InlineData(ModelState.Unauthorized)]
    [InlineData(ModelState.Retired)]
    public void EveryUnhealthyState_HasAFiniteCooldown(ModelState state)
    {
        Assert.True(ModelChain.CooldownFor(state) > TimeSpan.Zero);
        Assert.True(ModelChain.CooldownFor(state) < TimeSpan.FromDays(30));
    }

    [Fact]
    public void RateLimitedModel_IsSkipped_WhileCoolingDown()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new Dictionary<string, ModelHealth>
        {
            ["model-a"] = new ModelHealth { Id = "model-a", State = ModelState.RateLimited, CooldownUntil = now.AddSeconds(30) }
        };

        var usable = ModelChain.UsableInOrder(Chain, health, now).ToList();

        Assert.DoesNotContain(Chain[0], usable);
    }

    [Fact]
    public void RateLimitedModel_IsUsableAgain_AfterCooldownExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new Dictionary<string, ModelHealth>
        {
            ["model-a"] = new ModelHealth { Id = "model-a", State = ModelState.RateLimited, CooldownUntil = now.AddSeconds(-1) }
        };

        var usable = ModelChain.UsableInOrder(Chain, health, now).ToList();

        Assert.Contains(Chain[0], usable);
    }

    [Fact]
    public void HealthyModel_StaysFirst()
    {
        var health = new Dictionary<string, ModelHealth>
        {
            ["model-a"] = new ModelHealth { Id = "model-a", State = ModelState.Healthy, LastLatencyMs = 700 }
        };

        var usable = ModelChain.UsableInOrder(Chain, health, DateTimeOffset.UtcNow).ToList();

        Assert.Equal("model-a", usable[0].Id);
    }

    [Fact]
    public void AllModelsCoolingDown_YieldsEmptySequence()
    {
        var now = DateTimeOffset.UtcNow;
        var health = Chain.ToDictionary(
            m => m.Id,
            m => new ModelHealth { Id = m.Id, State = ModelState.Retired, CooldownUntil = now.AddDays(7) });

        var usable = ModelChain.UsableInOrder(Chain, health, now).ToList();

        Assert.Empty(usable);
    }

    [Fact]
    public void AllModelsCoolingDown_RecoverTogether_OnceTheirCooldownsExpire()
    {
        var now = DateTimeOffset.UtcNow;
        var health = Chain.ToDictionary(
            m => m.Id,
            m => new ModelHealth { Id = m.Id, State = ModelState.Unauthorized, CooldownUntil = now.AddMinutes(30) });

        Assert.Empty(ModelChain.UsableInOrder(Chain, health, now));
        Assert.Equal(Chain, ModelChain.UsableInOrder(Chain, health, now.AddMinutes(31)).ToList());
    }
}
