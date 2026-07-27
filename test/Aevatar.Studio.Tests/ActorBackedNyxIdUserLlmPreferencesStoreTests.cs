using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ActorBackedNyxIdUserLlmPreferencesStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetForBindingAsync_WithoutCommittedSelection_ShouldIgnoreCompatibilityRoute(
        bool useUnspecifiedSelection)
    {
        var queryPort = new RecordingUserConfigQueryPort(new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/legacy",
            MaxToolRounds: 8,
            LlmSelection: useUnspecifiedSelection
                ? new UserLlmSelectionValue(
                    UserLlmSelectionKind.Unspecified,
                    "/api/v1/proxy/s/legacy",
                    "us-legacy",
                    "legacy")
                : null));
        var store = new ActorBackedNyxIdUserLlmPreferencesStore(queryPort);

        var preferences = await store.GetForBindingAsync("binding-alpha");

        preferences.PreferredRoute.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForBindingAsync_WithTypedGateway_ShouldReturnCanonicalGateway()
    {
        var queryPort = new RecordingUserConfigQueryPort(new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/legacy",
            MaxToolRounds: 8,
            LlmSelection: new UserLlmSelectionValue(
                UserLlmSelectionKind.Gateway,
                "/api/v1/proxy/s/legacy",
                "us-legacy",
                "legacy")));
        var store = new ActorBackedNyxIdUserLlmPreferencesStore(queryPort);

        var preferences = await store.GetForBindingAsync("binding-alpha");

        preferences.PreferredRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task GetForBindingAsync_ShouldReadTypedChannelBindingResource()
    {
        const string typedRoute = "/api/v1/proxy/s/chrono-llm-public";
        var queryPort = new RecordingUserConfigQueryPort(new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/legacy",
            MaxToolRounds: 8,
            LlmSelection: new UserLlmSelectionValue(
                UserLlmSelectionKind.NyxIdUserService,
                typedRoute,
                "us-chrono",
                "chrono-llm-public")));
        var store = new ActorBackedNyxIdUserLlmPreferencesStore(queryPort);

        var preferences = await store.GetForBindingAsync("binding-alpha");

        queryPort.Resources.Should().ContainSingle().Which.Should()
            .Be(UserConfigResourceKey.ForChannelBinding("binding-alpha"));
        preferences.DefaultModel.Should().Be("gpt-5.5");
        preferences.PreferredRoute.Should().Be(typedRoute);
        preferences.MaxToolRounds.Should().Be(8);
    }

    private sealed class RecordingUserConfigQueryPort(UserConfig config) : IUserConfigQueryPort
    {
        public List<UserConfigResourceKey> Resources { get; } = [];

        public Task<UserConfig> GetAsync(
            UserConfigResourceKey resource,
            CancellationToken ct = default)
        {
            Resources.Add(resource);
            return Task.FromResult(config);
        }

        public Task<UserConfig> GetAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
