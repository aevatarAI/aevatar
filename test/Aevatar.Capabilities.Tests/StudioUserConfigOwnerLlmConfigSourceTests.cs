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
            LlmSelection: new UserLlmSelectionValue(
                UserLlmSelectionKind.NyxIdUserService,
                "/api/v1/proxy/s/chrono-llm",
                "us-chrono-alpha",
                "chrono-llm"));

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.DefaultModel.Should().Be("gpt-5.5");
        result.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/chrono-llm");
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
            LlmSelection: useUnspecifiedSelection
                ? new UserLlmSelectionValue(
                    UserLlmSelectionKind.Unspecified,
                    "/api/v1/proxy/s/legacy-provider",
                    "us-legacy",
                    "legacy-provider")
                : null);

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.DefaultModel.Should().Be("gpt-5.5");
        result.PreferredLlmRoute.Should().BeNull();
        result.MaxToolRounds.Should().Be(9);
    }

    [Fact]
    public async Task GetForScopeAsync_ShouldReturnCanonicalRouteForTypedGateway()
    {
        var config = new UserConfig(
            DefaultModel: string.Empty,
            PreferredLlmRoute: "/api/v1/proxy/s/legacy-provider",
            MaxToolRounds: 0,
            LlmSelection: new UserLlmSelectionValue(
                UserLlmSelectionKind.Gateway,
                UserConfigLlmRouteDefaults.Gateway,
                string.Empty,
                string.Empty));

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task GetForScopeAsync_ShouldKeepPrefixedModel_WhenRouteIsGateway()
    {
        var config = new UserConfig(
            DefaultModel: "chrono-llm/gpt-5.5",
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            MaxToolRounds: 7,
            LlmSelection: new UserLlmSelectionValue(
                UserLlmSelectionKind.Gateway,
                UserConfigLlmRouteDefaults.Gateway,
                string.Empty,
                string.Empty));

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.DefaultModel.Should().Be("chrono-llm/gpt-5.5");
        result.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
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

    private sealed class NullQueryPort : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult<UserConfig>(null!);

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default) =>
            Task.FromResult<UserConfig>(null!);
    }
}
