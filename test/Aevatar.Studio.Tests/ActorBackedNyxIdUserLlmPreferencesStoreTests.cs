using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
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
                ? new LLMSelection
                {
                    ModelSelection = new LLMModelSelection
                    {
                        Kind = LLMModelSelectionKind.Unspecified,
                    },
                }
                : null));
        var store = new ActorBackedNyxIdUserLlmPreferencesStore(queryPort);

        var preferences = await store.GetForBindingAsync("binding-alpha");

        preferences.Status.Should().Be(LLMSelectionPersistenceStatus.LegacyRepairRequired);
    }

    [Fact]
    public async Task GetForBindingAsync_WithTypedGateway_ShouldReturnCanonicalGateway()
    {
        var queryPort = new RecordingUserConfigQueryPort(new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/legacy",
            MaxToolRounds: 8,
            LlmSelection: new LLMSelection
            {
                RouteKind = LLMRouteKind.Gateway,
                RouteValue = UserConfigLlmRouteDefaults.Gateway,
                ModelSelection = new LLMModelSelection
                {
                    Kind = LLMModelSelectionKind.ProviderDefault,
                },
            }));
        var store = new ActorBackedNyxIdUserLlmPreferencesStore(queryPort);

        var preferences = await store.GetForBindingAsync("binding-alpha");

        preferences.Selection.RouteValue.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        preferences.Status.Should().Be(LLMSelectionPersistenceStatus.Ready);
    }

    [Fact]
    public async Task GetForBindingAsync_ShouldReadTypedChannelBindingResource()
    {
        const string typedRoute = "/api/v1/proxy/s/chrono-llm-public";
        var queryPort = new RecordingUserConfigQueryPort(new UserConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/legacy",
            MaxToolRounds: 8,
            LlmSelection: new LLMSelection
            {
                RouteKind = LLMRouteKind.NyxIdUserService,
                RouteValue = typedRoute,
                NyxIdUserServiceId = "us-chrono",
                ServiceSlugSnapshot = "chrono-llm-public",
                ModelSelection = new LLMModelSelection
                {
                    Kind = LLMModelSelectionKind.ExplicitModel,
                    ModelId = "gpt-5.5",
                },
            }));
        var store = new ActorBackedNyxIdUserLlmPreferencesStore(queryPort);

        var preferences = await store.GetForBindingAsync("binding-alpha");

        queryPort.Resources.Should().ContainSingle().Which.Should()
            .Be(UserConfigResourceKey.ForChannelBinding("binding-alpha"));
        preferences.Selection.ModelSelection.ModelId.Should().Be("gpt-5.5");
        preferences.Selection.RouteValue.Should().Be(typedRoute);
        preferences.Status.Should().Be(LLMSelectionPersistenceStatus.Ready);
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
