using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class UserConfigServiceTests
{
    private static readonly NyxIdLlmService ChronoLlm = new(
        UserServiceId: "chrono-llm-service",
        ServiceSlug: "chrono-llm",
        DisplayName: "chrono-llm shared",
        RouteValue: "/api/v1/proxy/s/chrono-llm",
        DefaultModel: "gpt-5.4",
        Models: ["gpt-5.5", "gpt-5.4"],
        Status: "ready",
        Source: NyxIdLlmProviderSource.UserService,
        Allowed: true,
        Description: null);

    [Fact]
    public async Task SaveAsync_ShouldSplitPrefixedModel_WhenLegacyUserConfigEndpointCarriesRouteModel()
    {
        var commandService = new RecordingUserConfigCommandService();
        var service = CreateService(commandService: commandService);

        var receipt = await service.SaveAsync(
            "bearer",
            new SaveUserConfigCommand(DefaultModel: "chrono-llm/gpt-5.5"));

        receipt.AckStage.Should().Be(UserConfigCommandAckStage.Accepted);
        commandService.Saved.Should().ContainSingle().Which.Should().Match<UserConfig>(config =>
            config.DefaultModel == "gpt-5.5" &&
            config.PreferredLlmRoute == "/api/v1/proxy/s/chrono-llm");
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepPrefixedProviderModel_WhenCatalogDoesNotResolveRoute()
    {
        var commandService = new RecordingUserConfigCommandService();
        var service = CreateService(commandService: commandService);

        await service.SaveAsync(
            "bearer",
            new SaveUserConfigCommand(DefaultModel: "openai/gpt-5"));

        commandService.Saved.Should().ContainSingle().Which.Should().Match<UserConfig>(config =>
            config.DefaultModel == "openai/gpt-5" &&
            config.PreferredLlmRoute == UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepPrefixedProviderModel_WhenBearerTokenIsMissing()
    {
        var commandService = new RecordingUserConfigCommandService();
        var service = CreateService(commandService: commandService);

        await service.SaveAsync(new SaveUserConfigCommand(
            DefaultModel: "chrono-llm/gpt-5.5"));

        commandService.Saved.Should().ContainSingle().Which.Should().Match<UserConfig>(config =>
            config.DefaultModel == "chrono-llm/gpt-5.5" &&
            config.PreferredLlmRoute == UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task GetSettingsAsync_ShouldResolveLegacyPrefixedCurrentModelThroughCatalog()
    {
        var queryPort = new StubUserConfigQueryPort(new UserConfig(DefaultModel: "chrono-llm/gpt-5.5"));
        var catalogPort = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult([ChronoLlm], null));
        var service = new UserLlmPreferenceService(queryPort, catalogPort);

        var result = await service.GetSettingsAsync("bearer", CancellationToken.None);

        result.SavedRoute.Should().Be(ChronoLlm.RouteValue);
        result.EffectiveRoute.Should().Be(ChronoLlm.RouteValue);
        result.DefaultModel.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_ShouldResolveModelOnlyPrefixedValueThroughCatalog()
    {
        var commandService = new RecordingUserConfigCommandService();
        var service = CreateService(commandService: commandService);

        var receipt = await service.SaveLlmPreferenceAsync(
            "bearer",
            new SaveUserLlmPreferenceCommand(Model: "chrono-llm/gpt-5.5"));

        receipt.AckStage.Should().Be(UserConfigCommandAckStage.Accepted);
        commandService.Saved.Should().ContainSingle().Which.Should().Match<UserConfig>(config =>
            config.DefaultModel == "gpt-5.5" &&
            config.PreferredLlmRoute == ChronoLlm.RouteValue);
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_ShouldRejectUnknownPrefixedModelRoute()
    {
        var service = CreateService();

        var act = async () => await service.SaveLlmPreferenceAsync(
            "bearer",
            new SaveUserLlmPreferenceCommand(Model: "unknown-llm/gpt-5.5"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LLM service 'unknown-llm' is not routable for this user.");
    }

    [Fact]
    public async Task SaveLlmPreferenceAsync_ShouldStripMatchingServicePrefix_WhenServiceIdProvided()
    {
        var commandService = new RecordingUserConfigCommandService();
        var service = CreateService(commandService: commandService);

        await service.SaveLlmPreferenceAsync(
            "bearer",
            new SaveUserLlmPreferenceCommand(ServiceId: "chrono-llm", Model: "chrono-llm/gpt-5.5"));

        commandService.Saved.Should().ContainSingle().Which.Should().Match<UserConfig>(config =>
            config.DefaultModel == "gpt-5.5" &&
            config.PreferredLlmRoute == ChronoLlm.RouteValue);
    }

    [Fact]
    public async Task Preset_UseExistingService_WhenCatalogDoesNotContainService_ShouldRejectInsteadOfFabricatingReadyUserService()
    {
        var commandService = new RecordingUserConfigCommandService();
        var missingPreset = new UserLlmPreset(
            "chrono-shared",
            "Use chrono",
            "Use shared service",
            new UseExistingService(
                "missing-chrono-service",
                "/api/v1/proxy/s/missing-chrono",
                "gpt-5.4"));
        var service = CreateService(
            commandService: commandService,
            catalogResult: new NyxIdLlmServicesResult(
                [ChronoLlm],
                new UserLlmSetupHint("https://nyxid.example/services", [missingPreset])));

        var act = async () => await service.SaveLlmPreferenceAsync(
            "bearer",
            new SaveUserLlmPreferenceCommand(PresetId: "chrono-shared"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LLM preset service 'missing-chrono-service' is not routable for this user.");
        commandService.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task Preset_UseExistingService_WhenCatalogContainsSelectableService_ShouldWriteCatalogRouteAndModel()
    {
        var commandService = new RecordingUserConfigCommandService();
        var preset = new UserLlmPreset(
            "chrono-shared",
            "Use chrono",
            "Use shared service",
            new UseExistingService(
                "chrono-llm-service",
                "/api/v1/proxy/s/chrono-llm",
                "gpt-5.5"));
        var service = CreateService(
            commandService: commandService,
            catalogResult: new NyxIdLlmServicesResult(
                [ChronoLlm],
                new UserLlmSetupHint("https://nyxid.example/services", [preset])));

        var receipt = await service.SaveLlmPreferenceAsync(
            "bearer",
            new SaveUserLlmPreferenceCommand(PresetId: "chrono-shared"));

        receipt.AckStage.Should().Be(UserConfigCommandAckStage.Accepted);
        commandService.Saved.Should().ContainSingle().Which.Should().Match<UserConfig>(config =>
            config.PreferredLlmRoute == ChronoLlm.RouteValue &&
            config.DefaultModel == "gpt-5.5");
    }

    [Fact]
    public async Task ChannelPreferencePort_PresetProvisionThenUse_ShouldWriteProvisionedRouteAndPreserveCurrentModel()
    {
        var current = new UserConfig(
            DefaultModel: "current-model",
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway);
        var queryPort = new StubUserConfigQueryPort(current);
        var commandService = new RecordingUserConfigCommandService();
        var provisioned = ChronoLlm with
        {
            UserServiceId = "chrono-provisioned-service",
            ServiceSlug = "chrono-provisioned",
            DisplayName = "chrono provisioned",
            RouteValue = "/api/v1/proxy/s/chrono-provisioned",
            DefaultModel = null,
            Models = [],
        };
        var preset = new UserLlmPreset(
            "chrono-provision",
            "Provision chrono",
            "Provision shared service",
            new ProvisionThenUse("chrono/shared"));
        var catalogPort = new StubUserLlmCatalogPort(
            new NyxIdLlmServicesResult(
                [],
                new UserLlmSetupHint("https://nyxid.example/services", [preset])),
            provisioned);
        var writer = new UserLlmPreferenceWriter(queryPort, commandService, catalogPort);
        var preferencePort = new ChannelUserLlmPreferencePort(writer);

        var receipt = await preferencePort.SaveAsync(
            "bnd_sender",
            "bearer",
            new SaveUserLlmPreferenceCommand(PresetId: "chrono-provision"),
            CancellationToken.None);

        receipt.AckStage.Should().Be(UserConfigCommandAckStage.Accepted);
        catalogPort.ProvisionCalls.Should().ContainSingle()
            .Which.Should().Be(("bearer", "chrono/shared"));
        var saved = commandService.ScopedSaved.Should().ContainSingle().Subject;
        saved.ScopeId.Should().Be("bnd_sender");
        saved.Config.PreferredLlmRoute.Should().Be(provisioned.RouteValue);
        saved.Config.DefaultModel.Should().Be("current-model");
    }

    [Theory]
    [InlineData("")]
    [InlineData("cloud")]
    public async Task SaveAsync_ShouldRejectInvalidRuntimeMode(string runtimeMode)
    {
        var service = CreateService();

        var act = async () => await service.SaveAsync(new SaveUserConfigCommand(RuntimeMode: runtimeMode));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Runtime mode must be 'local' or 'remote'.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://runtime.example.com")]
    public async Task SaveAsync_ShouldRejectInvalidRuntimeUrl(string url)
    {
        var service = CreateService();

        var act = async () => await service.SaveAsync(new SaveUserConfigCommand(LocalRuntimeBaseUrl: url));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LocalRuntimeBaseUrl must be an absolute http(s) URL.");
    }

    [Fact]
    public async Task SaveAsync_ShouldNormalizeRuntimeWriteFields()
    {
        var commandService = new RecordingUserConfigCommandService();
        var service = CreateService(commandService: commandService);

        await service.SaveAsync(new SaveUserConfigCommand(
            RuntimeMode: " REMOTE ",
            LocalRuntimeBaseUrl: " http://127.0.0.1:5080/ ",
            RemoteRuntimeBaseUrl: " https://runtime.example.com/ "));

        commandService.Saved.Should().ContainSingle().Which.Should().Match<UserConfig>(config =>
            config.RuntimeMode == UserConfigRuntimeDefaults.RemoteMode &&
            config.LocalRuntimeBaseUrl == "http://127.0.0.1:5080" &&
            config.RemoteRuntimeBaseUrl == "https://runtime.example.com");
    }

    private static UserConfigService CreateService(
        UserConfig? current = null,
        RecordingUserConfigCommandService? commandService = null,
        NyxIdLlmServicesResult? catalogResult = null)
    {
        var queryPort = new StubUserConfigQueryPort(current ?? new UserConfig(DefaultModel: string.Empty));
        commandService ??= new RecordingUserConfigCommandService();
        var catalogPort = new StubUserLlmCatalogPort(catalogResult ?? new NyxIdLlmServicesResult([ChronoLlm], null));
        var writer = new UserLlmPreferenceWriter(queryPort, commandService, catalogPort);
        return new UserConfigService(queryPort, commandService, writer);
    }

    private sealed class StubUserConfigQueryPort(UserConfig config) : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(config);

        public Task<UserConfig> GetAsync(string scopeId, CancellationToken ct = default) => Task.FromResult(config);
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public List<UserConfig> Saved { get; } = [];
        public List<(string ScopeId, UserConfig Config)> ScopedSaved { get; } = [];

        public Task<UserConfigSaveReceipt> SaveAsync(UserConfig config, CancellationToken ct = default)
        {
            Saved.Add(config);
            return Task.FromResult(new UserConfigSaveReceipt(
                Accepted: true,
                CommandId: "command-1",
                AckStage: UserConfigCommandAckStage.Accepted,
                ActorId: "user-config-default",
                CorrelationId: "command-1",
                AckedAtUtc: DateTimeOffset.UtcNow));
        }

        public Task<UserConfigSaveReceipt> SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default)
        {
            ScopedSaved.Add((scopeId, config));
            return SaveAsync(config, ct);
        }

        public Task<UserConfigSaveReceipt> SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default) =>
            Task.FromResult(new UserConfigSaveReceipt(
                Accepted: true,
                CommandId: "command-github",
                AckStage: UserConfigCommandAckStage.Accepted,
                ActorId: "user-config-default",
                CorrelationId: "command-github",
                AckedAtUtc: DateTimeOffset.UtcNow));
    }

    private sealed class StubUserLlmCatalogPort(
        NyxIdLlmServicesResult result,
        NyxIdLlmService? provisionedService = null) : IUserLlmCatalogPort
    {
        public List<(string BearerToken, string ProvisionEndpointId)> ProvisionCalls { get; } = [];

        public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct) =>
            Task.FromResult(result);

        public Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct)
        {
            ProvisionCalls.Add((bearerToken, provisionEndpointId));
            return Task.FromResult(provisionedService ?? ChronoLlm);
        }
    }
}
