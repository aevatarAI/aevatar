using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.Hosting.Tests;

public sealed class StudioUserConfigOwnerLlmConfigSourceTests
{
    [Fact]
    public async Task GetForScopeAsync_ShouldReturnExplicitlySavedRoute()
    {
        // The happy path: the bot owner saved a custom NyxID service route. The bridge passes
        // it through verbatim so OwnerLlmConfigApplier pins NyxIdRoutePreference and the LLM
        // provider proxies through the user's `chrono-llm` service.
        var config = new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/chrono-llm",
            MaxToolRounds: 7);

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.DefaultModel.Should().Be("gpt-5.5");
        result.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/chrono-llm");
        result.MaxToolRounds.Should().Be(7);
    }

    [Theory]
    [InlineData("")]                         // ProjectionUserConfigQueryPort default
    [InlineData("   ")]                      // whitespace
    [InlineData("gateway")]                  // explicit "use the gateway" sentinel
    [InlineData("auto")]                     // auto sentinel
    [InlineData("GATEWAY")]                  // case-insensitive
    [InlineData("//evil.example.com/path")]  // protocol-relative; Normalize rejects
    [InlineData("https://evil.example.com")] // absolute URI; Normalize rejects
    public async Task GetForScopeAsync_ShouldCollapseGatewaySentinelsToNull(string savedRoute)
    {
        // Codex flagged on PR #509 that ProjectionUserConfigQueryPort fills PreferredLlmRoute
        // with UserConfigLlmRouteDefaults.Gateway when the user has no saved route, and worried
        // the bridge would leak that sentinel into outbound metadata. The bridge runs the value
        // through Studio's UserConfigLlmRoute.Normalize so any "use the default gateway" form
        // — empty / whitespace / "auto" / "gateway" / invalid URI — collapses to null. The
        // applier's null-or-whitespace guard then leaves NyxIdRoutePreference unset and the
        // LLM provider's compile-time gateway path takes over.
        var config = new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: savedRoute,
            MaxToolRounds: 0);

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.PreferredLlmRoute.Should().BeNull();
    }

    [Fact]
    public async Task GetForScopeAsync_ShouldNormalizeBareSlugIntoProxyPath()
    {
        // UserConfigLlmRoute.Normalize turns a bare slug "chrono-llm" into the proxy path
        // "/api/v1/proxy/s/chrono-llm". The bridge passes that normalized form through so the
        // applier can pin a valid relative path against the NyxID authority — no sentinel,
        // no broken URI.
        var config = new UserConfig(
            DefaultModel: string.Empty,
            PreferredLlmRoute: "chrono-llm",
            MaxToolRounds: 0);

        var source = new StudioUserConfigOwnerLlmConfigSource(new StubQueryPort(config));

        var result = await source.GetForScopeAsync("scope-1");

        result.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/chrono-llm");
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

        public Task<UserConfig> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(config);
    }

    private sealed class NullQueryPort : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult<UserConfig>(null!);

        public Task<UserConfig> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<UserConfig>(null!);
    }
}
