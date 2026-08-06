using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StudioConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class DefaultUserLlmOptionsServiceTests
{
    private const string SharedRoute = "/api/v1/proxy/s/shared-llm";

    [Theory]
    [InlineData(MissingTypedSelectionCase.NullConfig, "")]
    [InlineData(MissingTypedSelectionCase.NullConfig, SharedRoute)]
    [InlineData(MissingTypedSelectionCase.NullSelection, "")]
    [InlineData(MissingTypedSelectionCase.NullSelection, SharedRoute)]
    [InlineData(MissingTypedSelectionCase.Unspecified, "")]
    [InlineData(MissingTypedSelectionCase.Unspecified, SharedRoute)]
    public async Task GetOptionsAsync_WithoutAuthoritativeTypedSelection_ShouldIgnoreCompatibilityRoute(
        MissingTypedSelectionCase selectionCase,
        string compatibilityRoute)
    {
        var config = selectionCase switch
        {
            MissingTypedSelectionCase.NullConfig => null,
            MissingTypedSelectionCase.NullSelection => new StudioConfig(
                DefaultModel: "gpt-5.5",
                PreferredLlmRoute: compatibilityRoute,
                LlmSelection: null),
            MissingTypedSelectionCase.Unspecified => new StudioConfig(
                DefaultModel: "gpt-5.5",
                PreferredLlmRoute: compatibilityRoute,
                LlmSelection: new LLMSelection
                {
                    RouteValue = SharedRoute,
                    NyxIdUserServiceId = "us-alpha",
                    ServiceSlugSnapshot = "shared-llm",
                    ModelSelection = new LLMModelSelection(),
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(selectionCase)),
        };

        var view = await GetOptionsAsync(config, GatewayService(), InventoryService("us-alpha"));

        view.Current.Should().BeNull();
        view.CurrentRouteValue.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task GetOptionsAsync_WithTypedServiceMissingIdAndGatewayRoute_ShouldHaveNoCurrentOption()
    {
        var selection = ServiceSelection(" ", UserConfigLlmRouteDefaults.Gateway);

        var view = await GetOptionsAsync(Config(selection), GatewayService(), InventoryService("us-alpha"));

        view.Current.Should().BeNull();
    }

    [Fact]
    public async Task GetOptionsAsync_WithTypedServiceMissingIdAndDuplicateRoute_ShouldNotMatchByRoute()
    {
        var selection = ServiceSelection(string.Empty, SharedRoute);

        var view = await GetOptionsAsync(
            Config(selection),
            InventoryService("us-alpha"),
            InventoryService("us-beta"));

        view.Current.Should().BeNull();
    }

    [Fact]
    public async Task GetOptionsAsync_WithValidTypedGateway_ShouldResolveCanonicalGatewayOnly()
    {
        var selection = new LLMSelection
        {
            RouteKind = LLMRouteKind.Gateway,
            RouteValue = UserConfigLlmRouteDefaults.Gateway,
            ModelSelection = ProviderDefaultModel(),
        };

        var view = await GetOptionsAsync(Config(selection), GatewayService(), InventoryService("us-alpha"));

        view.Current.Should().NotBeNull();
        view.Current!.RouteValue.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        view.Current.Identity.Should().BeNull();
        view.CurrentRouteValue.Should().Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task GetOptionsAsync_WithValidTypedServiceAndDuplicateRoute_ShouldResolveExactInventoryId()
    {
        var selection = ServiceSelection("us-beta", SharedRoute);

        var view = await GetOptionsAsync(
            Config(selection),
            InventoryService("us-alpha"),
            InventoryService("us-beta"));

        view.Current.Should().NotBeNull();
        view.Current!.Identity!.NyxIdUserServiceId.Should().Be("us-beta");
    }

    private static StudioConfig Config(LLMSelection selection) => new(
        DefaultModel: "gpt-5.5",
        PreferredLlmRoute: selection.RouteValue,
        LlmSelection: selection);

    private static LLMSelection ServiceSelection(string userServiceId, string route) => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = route,
        NyxIdUserServiceId = userServiceId,
        ServiceSlugSnapshot = "shared-llm",
        ModelSelection = ProviderDefaultModel(),
    };

    private static LLMModelSelection ProviderDefaultModel() => new()
    {
        Kind = LLMModelSelectionKind.ProviderDefault,
    };

    private static async Task<UserLlmOptionsView> GetOptionsAsync(
        StudioConfig? config,
        params NyxIdLlmService[] services)
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IUserConfigQueryPort>(new StubUserConfigQueryPort(config))
            .BuildServiceProvider();
        var service = new DefaultUserLlmOptionsService(
            new StubCatalogClient(new NyxIdLlmServicesResult(services, null)),
            provider.GetRequiredService<IServiceScopeFactory>());

        return await service.GetOptionsAsync(
            new UserLlmOptionsQuery(
                new BindingId { Value = "bnd-alpha" },
                new ExternalSubjectRef
                {
                    Platform = "lark",
                    Tenant = "tenant-alpha",
                    ExternalUserId = "user-alpha",
                },
                "scope-alpha"),
            CancellationToken.None);
    }

    private static NyxIdLlmService GatewayService() => new(
        CatalogEntryId: null,
        ServiceSlug: "gateway",
        DisplayName: "NyxID Gateway",
        RouteValue: UserConfigLlmRouteDefaults.Gateway,
        ModelCatalog: EnumeratedCatalog("gpt-5.5"),
        Status: UserLlmRouteStatus.Ready,
        Source: UserLlmRouteSource.GatewayProvider,
        Allowed: true,
        Description: null);

    private static NyxIdLlmService InventoryService(string id) => new(
        CatalogEntryId: null,
        ServiceSlug: "shared-llm",
        DisplayName: $"Service {id}",
        RouteValue: SharedRoute,
        ModelCatalog: EnumeratedCatalog("gpt-5.5"),
        Status: UserLlmRouteStatus.Ready,
        Source: UserLlmRouteSource.UserService,
        Allowed: true,
        Description: null,
        Identity: new UserLlmServiceIdentity(
            UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            id));

    private static LLMModelCatalog EnumeratedCatalog(string modelId) => new()
    {
        Certainty = LLMModelCatalogCertainty.Enumerated,
        DefaultModelId = modelId,
        ModelIds = { modelId },
    };

    private sealed class StubCatalogClient(NyxIdLlmServicesResult result) : INyxIdLlmServiceCatalogClient
    {
        public Task<NyxIdLlmServicesResult> GetServicesAsync(
            UserLlmOptionsQuery query,
            string accessToken,
            CancellationToken ct) =>
            Task.FromResult(result);

        public Task<UserLlmSetupHint> GetSetupHintAsync(
            UserLlmOptionsQuery query,
            string accessToken,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<NyxIdLlmService> ProvisionAsync(
            UserLlmSelectionContext context,
            string accessToken,
            string provisionEndpointId,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubUserConfigQueryPort(StudioConfig? config) : IUserConfigQueryPort
    {
        public Task<StudioConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default) =>
            Task.FromResult(config!);

        public Task<StudioConfig> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(config!);
    }

    public enum MissingTypedSelectionCase
    {
        NullConfig,
        NullSelection,
        Unspecified,
    }
}
