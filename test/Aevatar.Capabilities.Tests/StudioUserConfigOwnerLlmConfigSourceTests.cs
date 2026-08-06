using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.Capabilities.Tests;

public sealed class StudioUserConfigOwnerLlmConfigSourceTests
{
    [Fact]
    public async Task GetForScopeAsync_ShouldReturnTypedServiceRouteInsteadOfLegacyCompatibilityRoute()
    {
        var config = new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/legacy-provider",
            MaxToolRounds: 7,
            LlmSelection: UserServiceSelection("gpt-5.5"));

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.Selection.Should().BeEquivalentTo(UserServiceSelection("gpt-5.5"));
        result.Status.Should().Be(LLMSelectionPersistenceStatus.Ready);
        result.MaxToolRounds.Should().Be(7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetForScopeAsync_ShouldIgnoreLegacyRouteWithoutTypedSelection(bool useUnspecifiedSelection)
    {
        var config = new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/legacy-provider",
            MaxToolRounds: 9,
            LlmSelection: useUnspecifiedSelection ? new LLMSelection() : null);

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.Status.Should().Be(LLMSelectionPersistenceStatus.LegacyRepairRequired);
        result.MaxToolRounds.Should().Be(9);
    }

    [Fact]
    public async Task GetForScopeAsync_ShouldReturnNoExplicitRoute_WhenSavedSelectionIsMissing()
    {
        var config = new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: string.Empty,
            MaxToolRounds: 9,
            LlmSelection: null);
        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.Status.Should().Be(LLMSelectionPersistenceStatus.LegacyRepairRequired);
    }

    [Fact]
    public async Task GetForScopeAsync_ShouldReturnCanonicalRouteForTypedGateway()
    {
        var config = new UserConfig(
            DefaultModel: string.Empty,
            PreferredLlmRoute: "/api/v1/proxy/s/legacy-provider",
            MaxToolRounds: 0,
            LlmSelection: GatewaySelection(null));

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.Selection.Should().BeEquivalentTo(GatewaySelection(null));
        result.Status.Should().Be(LLMSelectionPersistenceStatus.Ready);
    }

    [Fact]
    public async Task GetForScopeAsync_ShouldKeepPrefixedModel_WhenRouteIsGateway()
    {
        var config = new UserConfig(
            DefaultModel: "chrono-llm/gpt-5.5",
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            MaxToolRounds: 7,
            LlmSelection: GatewaySelection("chrono-llm/gpt-5.5"));

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.Selection.Should().BeEquivalentTo(GatewaySelection("chrono-llm/gpt-5.5"));
        result.Status.Should().Be(LLMSelectionPersistenceStatus.Ready);
        result.MaxToolRounds.Should().Be(7);
    }

    [Fact]
    public async Task GetForScopeAsync_ShouldReturnEmpty_WhenQueryPortReturnsNull()
    {
        // Defensive — a future query-port impl might return null instead of a defaulted
        // record. The bridge falls through to OwnerLlmConfig.Empty so the applier no-ops.
        var source = new StudioUserConfigOwnerLlmConfigSource(new NullQueryPort());

        var result = await source.GetForScopeAsync("scope-1");

        result.Should().Be(OwnerLlmConfig.Empty);
    }

    private sealed class StubQueryPort(UserConfig config) : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(config);

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default) =>
            Task.FromResult(config);
    }

    private static LLMSelection UserServiceSelection(string modelId) => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = "/api/v1/proxy/s/chrono-llm",
        NyxIdUserServiceId = "us-chrono-alpha",
        ServiceSlugSnapshot = "chrono-llm",
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.ExplicitModel,
            ModelId = modelId,
        },
    };

    private static LLMSelection GatewaySelection(string? modelId) => new()
    {
        RouteKind = LLMRouteKind.Gateway,
        RouteValue = UserConfigLlmRouteDefaults.Gateway,
        ModelSelection = new LLMModelSelection
        {
            Kind = modelId is null
                ? LLMModelSelectionKind.ProviderDefault
                : LLMModelSelectionKind.ExplicitModel,
            ModelId = modelId ?? string.Empty,
        },
    };

    private sealed class NullQueryPort : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult<UserConfig>(null!);

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default) =>
            Task.FromResult<UserConfig>(null!);
    }
}
