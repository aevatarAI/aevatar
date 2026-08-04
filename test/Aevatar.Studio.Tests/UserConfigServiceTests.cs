using Aevatar.AI.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class UserConfigServiceTests
{
    [Fact]
    public async Task SaveAsync_WithGateway_ShouldUseFreshCatalogAndCommitWholeSelection()
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
            [GatewayService("gpt-5.5")],
            null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        await writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SelectGatewayUserLlmPreferenceIntent(
                new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault }),
            CancellationToken.None);

        catalog.FreshCalls.Should().Be(1);
        catalog.GetServicesCalls.Should().Be(0);
        commands.Updates.Single().Update.LlmSelection!.RouteKind.Should().Be(LLMRouteKind.Gateway);
    }

    [Fact]
    public async Task SaveAsync_WithReset_ShouldNotReadCatalog()
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult([], null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        await writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            bearerToken: null,
            new ResetUserLlmPreferenceIntent(),
            CancellationToken.None);

        catalog.FreshCalls.Should().Be(0);
        catalog.GetServicesCalls.Should().Be(0);
        var selection = commands.Updates.Single().Update.LlmSelection!;
        selection.RouteKind.Should().Be(LLMRouteKind.Unspecified);
        selection.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.Unspecified);
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_WithExactInventoryId_ShouldDispatchDeltaWithoutReadingConfig()
    {
        var commands = new RecordingUserConfigCommandService();
        var writer = CreateWriter(commands, InventoryService("us-beta", "shared-slug"));

        await writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(UserServiceId: "us-beta", Model: "gpt-5.5"),
            CancellationToken.None);

        var update = commands.Updates.Should().ContainSingle().Which.Update;
        update.LlmSelection!.NyxIdUserServiceId.Should().Be("us-beta");
        update.LlmSelection.ServiceSlugSnapshot.Should().Be("shared-slug");
        update.LlmSelection.ModelSelection.ModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_WithDuplicateSlug_ShouldSelectExactInventoryId()
    {
        var commands = new RecordingUserConfigCommandService();
        var writer = CreateWriter(
            commands,
            InventoryService("us-alpha", "shared"),
            InventoryService("us-beta", "shared"));

        await writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(UserServiceId: "us-beta"),
            CancellationToken.None);

        var selection = commands.Updates.Should().ContainSingle().Which.Update.LlmSelection;
        selection!.NyxIdUserServiceId.Should().Be("us-beta");
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_WithDiagnosticCatalogId_ShouldRejectWithoutDispatch()
    {
        var commands = new RecordingUserConfigCommandService();
        var diagnostic = InventoryService("unused", "shared") with
        {
            CatalogEntryId = "catalog-alpha",
            Identity = null,
        };
        var writer = CreateWriter(commands, diagnostic);

        var act = () => writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(UserServiceId: "catalog-alpha"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LLM user service 'catalog-alpha' is not selectable.");
        commands.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_WithRouteOnlyProxy_ShouldRejectWithoutDispatch()
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
            [InventoryService("us-alpha", "shared")],
            null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        var act = () => writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(RouteValue: "/api/v1/proxy/s/shared"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("userServiceId is required for a NyxID service selection.");
        catalog.GetServicesCalls.Should().Be(0);
        commands.Updates.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("gateway")]
    [InlineData(" GATEWAY ")]
    [InlineData(UserConfigLlmRouteDefaults.Gateway)]
    [InlineData(" /api/v1/llm/gateway/v1 ")]
    public async Task SaveLlmPreferenceAsync_WithGatewayAlias_ShouldUseFreshCatalog(string routeValue)
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
            [GatewayService("gpt-5.5")],
            null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        await writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            bearerToken: "bearer",
            new SaveUserLlmPreferenceCommand(RouteValue: routeValue, Model: " gpt-5.5 "),
            CancellationToken.None);

        catalog.GetServicesCalls.Should().Be(0);
        catalog.FreshCalls.Should().Be(1);
        var update = commands.Updates.Should().ContainSingle().Which.Update;
        update.LlmSelection!.Should().BeEquivalentTo(UserLlmPreferenceWriteCore.BuildGatewaySelection(
            new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = "gpt-5.5",
            }));
    }

    [Theory]
    [InlineData("https://evil.example.com/path")]
    [InlineData("//evil.example.com/path")]
    public async Task SaveLlmPreferenceAsync_WithExternalRouteWithoutId_ShouldRejectWithoutDispatch(string routeValue)
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult([], null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        var act = () => writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            bearerToken: null,
            new SaveUserLlmPreferenceCommand(RouteValue: routeValue),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("userServiceId is required for a NyxID service selection.");
        catalog.GetServicesCalls.Should().Be(0);
        commands.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_WithModelOnly_ShouldRejectWithoutDispatch()
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult([], null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        var act = () => writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            bearerToken: null,
            new SaveUserLlmPreferenceCommand(Model: " gpt-5.5 "),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A complete LLM route selection is required.");
        catalog.GetServicesCalls.Should().Be(0);
        commands.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_WithRoutePrefixedModel_ShouldRejectWithoutDispatch()
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult([], null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        var act = () => writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(Model: "chrono-llm-public/gpt-5.5"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A complete LLM route selection is required.");
        catalog.GetServicesCalls.Should().Be(0);
        commands.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_WithReset_ShouldDispatchUnspecifiedSelection()
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult([], null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        await writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            bearerToken: null,
            new SaveUserLlmPreferenceCommand(Reset: true),
            CancellationToken.None);

        catalog.GetServicesCalls.Should().Be(0);
        var update = commands.Updates.Should().ContainSingle().Which.Update;
        update.LlmSelection.Should().BeEquivalentTo(UserLlmPreferenceWriteCore.BuildResetSelection());
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(" GATEWAY ")]
    [InlineData(UserConfigLlmRouteDefaults.Gateway)]
    public async Task SaveLlmPreferenceAsync_WithUserServiceIdAndGateway_ShouldRejectWithoutCatalogCall(
        string routeValue)
    {
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult([], null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        var act = () => writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(
                UserServiceId: "us-alpha",
                RouteValue: routeValue),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        catalog.GetServicesCalls.Should().Be(0);
        commands.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Preset_UseExistingService_WithCatalogOnlyId_ShouldRejectWithoutDispatch()
    {
        var commands = new RecordingUserConfigCommandService();
        var preset = new UserLlmPreset(
            "shared",
            "Shared",
            "Shared service",
            new UseExistingService("catalog-alpha", "/api/v1/proxy/s/shared", "gpt-5.5"));
        var diagnostic = InventoryService("unused", "shared") with
        {
            CatalogEntryId = "catalog-alpha",
            Identity = null,
        };
        var writer = new UserLlmPreferenceWriter(
            commands,
            new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
                [diagnostic],
                new UserLlmSetupHint("https://nyxid.example/services", [preset]))));

        var act = () => writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(PresetId: "shared"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LLM user service 'catalog-alpha' is not selectable.");
        commands.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Preset_ProvisionThenUse_ShouldReloadInventoryAndOmitMissingModel()
    {
        var commands = new RecordingUserConfigCommandService();
        var refreshed = InventoryService("us-provisioned", "provisioned", defaultModel: null);
        var provisioned = refreshed with
        {
            CatalogEntryId = "us-provisioned",
            Identity = null,
        };
        var preset = new UserLlmPreset(
            "provision",
            "Provision",
            "Provision service",
            new ProvisionThenUse("catalog/provision"));
        var catalog = new StubUserLlmCatalogPort(
            new NyxIdLlmServicesResult(
                [],
                new UserLlmSetupHint("https://nyxid.example/services", [preset])),
            provisioned,
            new NyxIdLlmServicesResult([refreshed], null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        await writer.SaveAsync(
            UserConfigResourceKey.ForChannelBinding("binding-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(PresetId: "provision"),
            CancellationToken.None);

        catalog.FreshCalls.Should().Be(2);
        catalog.ProvisionCalls.Should().ContainSingle().Which.Should().Be(("bearer", "catalog/provision"));
        var recorded = commands.Updates.Should().ContainSingle().Which;
        recorded.Resource.Should().Be(UserConfigResourceKey.ForChannelBinding("binding-alpha"));
        recorded.Update.LlmSelection!.NyxIdUserServiceId.Should().Be("us-provisioned");
        recorded.Update.LlmSelection.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ProviderDefault);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("us-other")]
    public async Task Preset_ProvisionThenUse_WithoutExactRefreshedInventoryIdentity_ShouldRejectWithoutDispatch(
        string? refreshedId)
    {
        var commands = new RecordingUserConfigCommandService();
        var provisioned = InventoryService("ignored", "provisioned") with
        {
            CatalogEntryId = "us-provisioned",
            Identity = null,
        };
        var preset = new UserLlmPreset(
            "provision",
            "Provision",
            "Provision service",
            new ProvisionThenUse("catalog/provision"));
        var refreshedServices = refreshedId is null
            ? Array.Empty<NyxIdLlmService>()
            : [InventoryService(refreshedId, "provisioned")];
        var catalog = new StubUserLlmCatalogPort(
            new NyxIdLlmServicesResult(
                [],
                new UserLlmSetupHint("https://nyxid.example/services", [preset])),
            provisioned,
            new NyxIdLlmServicesResult(refreshedServices, null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);

        var act = () => writer.SaveAsync(
            UserConfigResourceKey.ForOwnerScope("scope-alpha"),
            "bearer",
            new SaveUserLlmPreferenceCommand(PresetId: "provision"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LLM user service 'us-provisioned' is not selectable.");
        catalog.FreshCalls.Should().Be(2);
        commands.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task ChannelPreferencePort_WithExactInventoryId_ShouldDispatchBindingScopedDelta()
    {
        var commands = new RecordingUserConfigCommandService();
        var preferencePort = new ChannelUserLlmPreferencePort(
            CreateWriter(commands, InventoryService("us-alpha", "shared")));

        await preferencePort.SaveAsync(
            "binding-alpha",
            "bearer",
            new SelectUserServiceUserLlmPreferenceIntent(
                "us-alpha",
                new LLMModelSelection
                {
                    Kind = LLMModelSelectionKind.ExplicitModel,
                    ModelId = "gpt-5.5",
                }),
            CancellationToken.None);

        var update = commands.Updates.Should().ContainSingle().Which;
        update.Resource.Should().Be(UserConfigResourceKey.ForChannelBinding("binding-alpha"));
        update.Update.LlmSelection!.NyxIdUserServiceId.Should().Be("us-alpha");
    }

    [Fact]
    public void SaveUserConfigCommand_ShouldNotExposeDefaultModel()
    {
        typeof(SaveUserConfigCommand).GetProperty("DefaultModel").Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_WithGenericFields_ShouldDispatchOwnerDeltaWithoutRouteSelection()
    {
        var commands = new RecordingUserConfigCommandService();
        var service = CreateService(commands);

        await service.SaveAsync(new SaveUserConfigCommand(
            RuntimeMode: " REMOTE ",
            LocalRuntimeBaseUrl: " http://127.0.0.1:5080/ ",
            RemoteRuntimeBaseUrl: " https://runtime.example.com/ ",
            GithubUsername: " octocat ",
            MaxToolRounds: 5));

        var recorded = commands.Updates.Should().ContainSingle().Which;
        recorded.Resource.Should().Be(UserConfigResourceKey.ForOwnerScope("scope-alpha"));
        recorded.Update.Should().BeEquivalentTo(new UserConfigUpdate(
            RuntimeMode: UserConfigRuntimeDefaults.RemoteMode,
            LocalRuntimeBaseUrl: "http://127.0.0.1:5080",
            RemoteRuntimeBaseUrl: "https://runtime.example.com",
            GithubUsername: "octocat",
            MaxToolRounds: 5));
        recorded.Update.LlmSelection.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenAuthenticatedCallerHasNoScope_ShouldThrowBeforeReadingConfig()
    {
        var fixture = CreateMissingScopeService(authenticatedWithoutScope: true);

        var act = () => fixture.Service.GetAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("HTTP request has no resolvable scope*");
        fixture.Query.GetCalls.Should().Be(0);
        fixture.Commands.Updates.Should().BeEmpty();
        fixture.Catalog.GetServicesCalls.Should().Be(0);
    }

    [Fact]
    public async Task GetRuntimeAsync_WhenAuthenticatedCallerHasNoScope_ShouldThrowBeforeReadingConfig()
    {
        var fixture = CreateMissingScopeService(authenticatedWithoutScope: true);

        var act = () => fixture.Service.GetRuntimeAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("HTTP request has no resolvable scope*");
        fixture.Query.GetCalls.Should().Be(0);
        fixture.Commands.Updates.Should().BeEmpty();
        fixture.Catalog.GetServicesCalls.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_WhenAuthenticatedCallerHasNoScope_ShouldThrowBeforeDispatchingConfig()
    {
        var fixture = CreateMissingScopeService(authenticatedWithoutScope: true);

        var act = () => fixture.Service.SaveAsync(new SaveUserConfigCommand(GithubUsername: "octocat"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("HTTP request has no resolvable scope*");
        fixture.Query.GetCalls.Should().Be(0);
        fixture.Commands.Updates.Should().BeEmpty();
        fixture.Catalog.GetServicesCalls.Should().Be(0);
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_WhenAuthenticatedCallerHasNoScope_ShouldThrowBeforeCatalogOrDispatch()
    {
        var fixture = CreateMissingScopeService(authenticatedWithoutScope: true);

        var act = () => fixture.Service.SaveLlmPreferenceAsync(
            "bearer",
            new SelectUserServiceUserLlmPreferenceIntent(
                "us-alpha",
                new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("HTTP request has no resolvable scope*");
        fixture.Query.GetCalls.Should().Be(0);
        fixture.Commands.Updates.Should().BeEmpty();
        fixture.Catalog.GetServicesCalls.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_WhenUnauthenticatedRequestHasNoScope_ShouldThrowBeforeReadingConfig()
    {
        var fixture = CreateMissingScopeService(authenticatedWithoutScope: false);

        var act = () => fixture.Service.GetAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("HTTP request has no resolvable scope*");
        fixture.Query.GetCalls.Should().Be(0);
        fixture.Commands.Updates.Should().BeEmpty();
        fixture.Catalog.GetServicesCalls.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_WhenUnauthenticatedRequestHasNoScope_ShouldThrowBeforeDispatchingConfig()
    {
        var fixture = CreateMissingScopeService(authenticatedWithoutScope: false);

        var act = () => fixture.Service.SaveAsync(new SaveUserConfigCommand(GithubUsername: "octocat"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("HTTP request has no resolvable scope*");
        fixture.Query.GetCalls.Should().Be(0);
        fixture.Commands.Updates.Should().BeEmpty();
        fixture.Catalog.GetServicesCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cloud")]
    public async Task SaveAsync_ShouldRejectInvalidRuntimeMode(string runtimeMode)
    {
        var service = CreateService(new RecordingUserConfigCommandService());

        var act = () => service.SaveAsync(new SaveUserConfigCommand(RuntimeMode: runtimeMode));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Runtime mode must be 'local' or 'remote'.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://runtime.example.com")]
    public async Task SaveAsync_ShouldRejectInvalidRuntimeUrl(string url)
    {
        var service = CreateService(new RecordingUserConfigCommandService());

        var act = () => service.SaveAsync(new SaveUserConfigCommand(LocalRuntimeBaseUrl: url));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LocalRuntimeBaseUrl must be an absolute http(s) URL.");
    }

    [Fact]
    public async Task GetSettingsAsync_WithDuplicateRoute_ShouldPreserveExactInventoryChoices()
    {
        var service = new UserLlmPreferenceService(
            new StubUserConfigQueryPort(new UserConfig(DefaultModel: "gpt-5.5")),
            new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
                [InventoryService("us-alpha", "shared"), InventoryService("us-beta", "shared")],
                null)));

        var result = await service.GetSettingsAsync("bearer", CancellationToken.None);

        result.RouteOptions
            .Where(option => option.RouteValue == "/api/v1/proxy/s/shared")
            .Select(option => option.UserServiceId)
            .Should().BeEquivalentTo(["us-alpha", "us-beta"]);
    }

    [Fact]
    public async Task GetSettingsAsync_WhenSavedIdDisappearsButSameRouteRemains_ShouldMarkSavedSelectionUnavailable()
    {
        var saved = new LLMSelection
        {
            RouteKind = LLMRouteKind.NyxIdUserService,
            RouteValue = "/api/v1/proxy/s/shared",
            NyxIdUserServiceId = "us-beta",
            ServiceSlugSnapshot = "shared",
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = "gpt-5.5",
            },
        };
        var service = new UserLlmPreferenceService(
            new StubUserConfigQueryPort(new UserConfig(
                DefaultModel: "gpt-5.5",
                PreferredLlmRoute: "/api/v1/proxy/s/shared",
                LlmSelection: saved)),
            new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
                [InventoryService("us-alpha", "shared")],
                null)));

        var result = await service.GetSettingsAsync("bearer", CancellationToken.None);

        result.SavedSelection.Should().BeEquivalentTo(saved);
        result.SelectionStatus.Should().Be(UserLlmSelectionStatus.NeedsRepair);
        result.Remediation.Should().Be(UserLlmRemediationKind.ChooseReplacement);
    }

    [Fact]
    public async Task GetSettingsAsync_WithCompatibilityOnlySelection_ShouldRequireLegacyRepair()
    {
        var service = new UserLlmPreferenceService(
            new StubUserConfigQueryPort(new UserConfig(
                DefaultModel: "shared/gpt-5.5",
                PreferredLlmRoute: "/api/v1/proxy/s/shared",
                LlmSelection: null)),
            new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
                [InventoryService("us-alpha", "shared")],
                null)));

        var result = await service.GetSettingsAsync("bearer", CancellationToken.None);

        result.SavedSelection.Should().BeNull();
        result.SelectionStatus.Should().Be(UserLlmSelectionStatus.LegacyRepairRequired);
        result.Remediation.Should().Be(UserLlmRemediationKind.Reselect);
    }

    [Fact]
    public async Task GetSettingsAsync_WithResetSelection_ShouldUseSystemDefault()
    {
        var selection = new LLMSelection
        {
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.Unspecified,
            },
        };
        var service = new UserLlmPreferenceService(
            new StubUserConfigQueryPort(new UserConfig(
                DefaultModel: string.Empty,
                PreferredLlmRoute: string.Empty,
                LlmSelection: selection)),
            new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
                [InventoryService("us-alpha", "shared")],
                null)));

        var result = await service.GetSettingsAsync("bearer", CancellationToken.None);

        result.SavedSelection.Should().BeEquivalentTo(selection);
        result.SelectionStatus.Should().Be(UserLlmSelectionStatus.SystemDefault);
    }

    private static UserLlmPreferenceWriter CreateWriter(
        RecordingUserConfigCommandService commands,
        params NyxIdLlmService[] services) =>
        new(commands, new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(services, null)));

    private static UserConfigService CreateService(RecordingUserConfigCommandService commands)
    {
        var query = new StubUserConfigQueryPort(new UserConfig(DefaultModel: string.Empty));
        var writer = CreateWriter(commands, InventoryService("us-alpha", "shared"));
        return new UserConfigService(query, commands, writer, new StubScopeResolver("scope-alpha"));
    }

    private static (
        UserConfigService Service,
        StubUserConfigQueryPort Query,
        RecordingUserConfigCommandService Commands,
        StubUserLlmCatalogPort Catalog) CreateMissingScopeService(bool authenticatedWithoutScope)
    {
        var query = new StubUserConfigQueryPort(new UserConfig(DefaultModel: string.Empty));
        var commands = new RecordingUserConfigCommandService();
        var catalog = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
            [InventoryService("us-alpha", "shared")],
            null));
        var writer = new UserLlmPreferenceWriter(commands, catalog);
        var service = new UserConfigService(
            query,
            commands,
            writer,
            new StubScopeResolver(
                scopeId: null,
                authenticatedWithoutScope,
                hasHttpRequestContext: true));
        return (service, query, commands, catalog);
    }

    private static NyxIdLlmService InventoryService(
        string id,
        string slug,
        string? defaultModel = "gpt-5.5") => new(
        CatalogEntryId: null,
        ServiceSlug: slug,
        DisplayName: slug,
        RouteValue: $"/api/v1/proxy/s/{slug}",
        ModelCatalog: defaultModel is null
            ? new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.NotVerifiable,
                DiagnosticKind = LLMModelCatalogDiagnosticKind.NotPublished,
            }
            : new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.Enumerated,
                DefaultModelId = defaultModel,
                ModelIds = { defaultModel },
            },
        Status: UserLlmRouteStatus.Ready,
        Source: UserLlmRouteSource.UserService,
        Allowed: true,
        Description: null,
        Identity: new UserLlmServiceIdentity(
            UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            id));

    private static NyxIdLlmService GatewayService(string model) => new(
        CatalogEntryId: null,
        ServiceSlug: "gateway",
        DisplayName: "Gateway",
        RouteValue: UserConfigLlmRouteDefaults.Gateway,
        ModelCatalog: new LLMModelCatalog
        {
            Certainty = LLMModelCatalogCertainty.Enumerated,
            DefaultModelId = model,
            ModelIds = { model },
        },
        Status: UserLlmRouteStatus.Ready,
        Source: UserLlmRouteSource.GatewayProvider,
        Allowed: true,
        Description: null);

    private sealed class StubScopeResolver(
        string? scopeId,
        bool authenticatedWithoutScope = false,
        bool hasHttpRequestContext = false) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) =>
            string.IsNullOrWhiteSpace(scopeId) ? null : new(scopeId, "test");

        public bool HasAuthenticatedRequestWithoutScope(
            Microsoft.AspNetCore.Http.HttpContext? httpContext = null) => authenticatedWithoutScope;

        public bool HasHttpRequestContext(
            Microsoft.AspNetCore.Http.HttpContext? httpContext = null) => hasHttpRequestContext;
    }

    private sealed class StubUserConfigQueryPort(UserConfig config) : IUserConfigQueryPort
    {
        public int GetCalls { get; private set; }

        public Task<UserConfig> GetAsync(CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(config);
        }

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(config);
        }
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public List<(UserConfigResourceKey Resource, UserConfigUpdate Update)> Updates { get; } = [];

        public Task<UserConfigSaveReceipt> UpdateAsync(
            UserConfigResourceKey resource,
            UserConfigUpdate update,
            CancellationToken ct = default)
        {
            Updates.Add((resource, update));
            return Task.FromResult(new UserConfigSaveReceipt(
                true,
                "command-alpha",
                UserConfigCommandAckStage.Accepted,
                resource.Value,
                "correlation-alpha",
                DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class StubUserLlmCatalogPort(
        NyxIdLlmServicesResult result,
        NyxIdLlmService? provisionedService = null,
        NyxIdLlmServicesResult? refreshedResult = null) : IUserLlmCatalogPort
    {
        private bool _provisioned;

        public int GetServicesCalls { get; private set; }
        public int FreshCalls { get; private set; }
        public List<(string BearerToken, string ProvisionEndpointId)> ProvisionCalls { get; } = [];

        public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct)
        {
            GetServicesCalls++;
            return Task.FromResult(_provisioned && refreshedResult is not null ? refreshedResult : result);
        }

        public Task<NyxIdLlmServicesResult> GetFreshServicesAsync(string bearerToken, CancellationToken ct)
        {
            FreshCalls++;
            return Task.FromResult(_provisioned && refreshedResult is not null ? refreshedResult : result);
        }

        public Task<NyxIdLlmService> ProvisionAsync(
            string bearerToken,
            string provisionEndpointId,
            CancellationToken ct)
        {
            ProvisionCalls.Add((bearerToken, provisionEndpointId));
            _provisioned = true;
            return Task.FromResult(provisionedService ?? throw new NotSupportedException());
        }
    }
}
