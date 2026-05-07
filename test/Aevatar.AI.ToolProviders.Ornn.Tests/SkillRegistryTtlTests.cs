using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

/// <summary>
/// TTL semantics for the skill registry. The whole point of the cache is to let curators
/// update SKILL.md on Ornn and have aevatar pick up the new version within a bounded window
/// without a redeploy — so these tests pin both the "still fresh" and "stale, refetch wanted"
/// branches around the configured TTL.
/// </summary>
public sealed class SkillRegistryTtlTests
{
    [Fact]
    public void TryGet_WithinTtl_ReturnsCachedSkill()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
        var registry = new SkillRegistry(time);
        registry.Register(MakeSkill("nyxid", instructions: "v1"));

        time.Advance(TimeSpan.FromMinutes(4));

        registry.TryGet("nyxid", out var skill, maxAge: TimeSpan.FromMinutes(5))
            .Should().BeTrue();
        skill!.Instructions.Should().Be("v1");
    }

    [Fact]
    public void TryGet_BeyondTtl_ReturnsFalseSoCallerCanRefetch()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
        var registry = new SkillRegistry(time);
        registry.Register(MakeSkill("nyxid", instructions: "v1"));

        time.Advance(TimeSpan.FromMinutes(6));

        registry.TryGet("nyxid", out var skill, maxAge: TimeSpan.FromMinutes(5))
            .Should().BeFalse("stale entries must miss so use_skill drops to the remote fetcher");
        skill.Should().BeNull();
    }

    [Fact]
    public void Register_AfterStale_RefreshesFetchedAt()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
        var registry = new SkillRegistry(time);
        registry.Register(MakeSkill("nyxid", instructions: "v1"));

        time.Advance(TimeSpan.FromMinutes(6));
        // Simulate UseSkillTool's refetch-on-stale path: fetcher returns a fresher skill,
        // registry replaces the entry with a new FetchedAt at "now".
        registry.Register(MakeSkill("nyxid", instructions: "v2"));

        // Within 5 min of the re-register, lookup must hit the new entry.
        time.Advance(TimeSpan.FromMinutes(4));
        registry.TryGet("nyxid", out var skill, maxAge: TimeSpan.FromMinutes(5))
            .Should().BeTrue();
        skill!.Instructions.Should().Be("v2");
    }

    [Fact]
    public void TryGet_WithoutMaxAge_TreatsCacheAsAlwaysFresh()
    {
        // Local skills (scanned per-turn from disk) have no remote refresh story. Calling
        // TryGet without a maxAge must not impose a TTL — otherwise local skills would
        // disappear after the first window and need to be re-scanned to be visible.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
        var registry = new SkillRegistry(time);
        registry.Register(MakeSkill("translate-pro"));

        time.Advance(TimeSpan.FromHours(24));

        registry.TryGet("translate-pro", out var skill).Should().BeTrue();
        skill.Should().NotBeNull();
    }

    [Fact]
    public void TryGet_StaleEntryByRemoteId_AlsoMisses()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
        var registry = new SkillRegistry(time);
        registry.Register(MakeSkill(
            name: "translate-pro",
            instructions: "v1",
            remoteId: "skill-guid-1"));

        time.Advance(TimeSpan.FromMinutes(10));

        // RemoteId fallback path must respect the TTL too — otherwise stale skills could
        // sneak through when the LLM passes the GUID instead of the friendly name.
        registry.TryGet("skill-guid-1", out _, maxAge: TimeSpan.FromMinutes(5))
            .Should().BeFalse();
    }

    private static SkillDefinition MakeSkill(string name, string instructions = "body", string? remoteId = null)
    {
        return new SkillDefinition
        {
            Name = name,
            Description = $"{name} description",
            Instructions = instructions,
            Source = remoteId is null ? SkillSource.Local : SkillSource.Remote,
            RemoteId = remoteId,
        };
    }
}
