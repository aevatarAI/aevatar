using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class LLMModelSourceResolverTests
{
    [Fact]
    public void ResolveTargets_ShouldUseConfiguredExactUserServiceIdentityWithoutNameHeuristics()
    {
        var configured = UserSource("user-chrono", "runtime-without-llm-in-name");

        var targets = LLMModelSourceResolver.ResolveTargets([configured]);

        targets.Should().ContainSingle();
        targets[0].Source.Should().Be(new NyxIdResolvedUserModelSource(
            "user-chrono",
            "runtime-without-llm-in-name"));
    }

    [Fact]
    public void ResolveTargets_ShouldUseConfiguredCatalogIdentityForPortablePlatformDefault()
    {
        var targets = LLMModelSourceResolver.ResolveTargets(
            [CatalogSource("catalog-chrono", "chrono-runtime")]);

        targets.Should().ContainSingle();
        targets[0].Source.Should().Be(new NyxIdResolvedCatalogModelSource(
            "catalog-chrono",
            "chrono-runtime"));
    }

    [Fact]
    public void ResolveTargets_ShouldRejectPolicySourceWithoutRouteSlug()
    {
        var act = () => LLMModelSourceResolver.ResolveTargets(
            [UserSource("user-chrono", null)]);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ResolveTargets_ShouldRejectSourcesWithTheSamePublicSlug()
    {
        var act = () => LLMModelSourceResolver.ResolveTargets(
            [
                UserSource("user-one", "shared-runtime"),
                UserSource("user-two", "shared-runtime"),
            ]);

        act.Should().Throw<InvalidDataException>(
            "slug/model cannot encode two exact service identities");
    }

    [Fact]
    public async Task ReadEffectiveSourcesAsync_ShouldHonorExplicitEmptyScopePolicy()
    {
        var policy = new RecordingPolicyQueryPort
        {
            Scope = Snapshot(
                LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
                LLMModelCatalogPolicyMode.Custom),
            Platform = Snapshot(
                LLMModelCatalogPolicyOwner.Platform,
                LLMModelCatalogPolicyMode.Custom,
                CatalogSource("catalog-platform", "platform-runtime")),
        };
        var resolver = new LLMModelSourceResolver(policy);

        var sources = await resolver.ReadEffectiveSourcesAsync("scope-alpha", CancellationToken.None);

        sources.Should().BeEmpty();
        policy.Owners.Should().ContainSingle()
            .Which.Should().Be(LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));
    }

    [Fact]
    public async Task ReadEffectiveSourcesAsync_ShouldUsePlatformPolicyWhenScopeInherits()
    {
        var platformSource = CatalogSource("catalog-platform", "platform-runtime");
        var policy = new RecordingPolicyQueryPort
        {
            Scope = Snapshot(
                LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
                LLMModelCatalogPolicyMode.InheritPlatform),
            Platform = Snapshot(
                LLMModelCatalogPolicyOwner.Platform,
                LLMModelCatalogPolicyMode.Custom,
                platformSource),
        };
        var resolver = new LLMModelSourceResolver(policy);

        var sources = await resolver.ReadEffectiveSourcesAsync("scope-alpha", CancellationToken.None);

        sources.Should().Equal(platformSource);
        policy.Owners.Should().Equal(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
            LLMModelCatalogPolicyOwner.Platform);
    }

    [Fact]
    public async Task ReadEffectiveSourcesAsync_ShouldUsePlatformPolicyWhenScopeProjectionIsAbsent()
    {
        var platformSource = CatalogSource("catalog-platform", "platform-runtime");
        var policy = new RecordingPolicyQueryPort
        {
            Platform = Snapshot(
                LLMModelCatalogPolicyOwner.Platform,
                LLMModelCatalogPolicyMode.Custom,
                platformSource),
        };
        var resolver = new LLMModelSourceResolver(policy);

        var sources = await resolver.ReadEffectiveSourcesAsync("scope-alpha", CancellationToken.None);

        sources.Should().Equal(platformSource);
        policy.Owners.Should().Equal(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
            LLMModelCatalogPolicyOwner.Platform);
    }

    [Fact]
    public async Task ReadEffectiveSourcesAsync_WhenInheritedPlatformProjectionIsMissing_ShouldThrow()
    {
        var policy = new RecordingPolicyQueryPort
        {
            Scope = Snapshot(
                LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
                LLMModelCatalogPolicyMode.InheritPlatform),
        };
        var resolver = new LLMModelSourceResolver(policy);

        var act = () => resolver.ReadEffectiveSourcesAsync("scope-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*platform model catalog policy projection is unavailable*");
    }

    [Fact]
    public async Task ReadEffectiveSourcesAsync_WhenScopeAndPlatformProjectionsAreMissing_ShouldThrow()
    {
        var resolver = new LLMModelSourceResolver(new RecordingPolicyQueryPort());

        var act = () => resolver.ReadEffectiveSourcesAsync("scope-alpha", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*platform model catalog policy projection is unavailable*");
    }

    private static LLMModelCatalogPolicySource UserSource(
        string userServiceId,
        string? slugSnapshot) =>
        new(
            new NyxIDUserServiceModelSourceIdentity(userServiceId),
            slugSnapshot,
            new ExplicitLLMModels(["model-a"]));

    private static LLMModelCatalogPolicySource CatalogSource(
        string catalogServiceId,
        string? slugSnapshot) =>
        new(
            new NyxIDCatalogServiceModelSourceIdentity(catalogServiceId),
            slugSnapshot,
            new ExplicitLLMModels(["model-a"]));

    private static LLMModelCatalogPolicySnapshot Snapshot(
        LLMModelCatalogPolicyOwner owner,
        LLMModelCatalogPolicyMode mode,
        params LLMModelCatalogPolicySource[] sources) =>
        new(owner, mode, sources, 1, DateTimeOffset.UnixEpoch);

    private sealed class RecordingPolicyQueryPort : ILLMModelCatalogPolicyQueryPort
    {
        public LLMModelCatalogPolicySnapshot? Scope { get; init; }

        public LLMModelCatalogPolicySnapshot? Platform { get; init; }

        public List<LLMModelCatalogPolicyOwner> Owners { get; } = [];

        public Task<LLMModelCatalogPolicySnapshot?> GetAsync(
            LLMModelCatalogPolicyOwner owner,
            CancellationToken ct = default)
        {
            Owners.Add(owner);
            return Task.FromResult(
                owner.Kind == LLMModelCatalogPolicyOwnerKind.Platform ? Platform : Scope);
        }
    }
}
