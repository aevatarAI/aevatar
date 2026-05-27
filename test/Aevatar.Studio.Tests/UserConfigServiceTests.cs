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
        RecordingUserConfigCommandService? commandService = null)
    {
        var queryPort = new StubUserConfigQueryPort(current ?? new UserConfig(DefaultModel: string.Empty));
        commandService ??= new RecordingUserConfigCommandService();
        var catalogPort = new StubUserLlmCatalogPort(new NyxIdLlmServicesResult([ChronoLlm], null));
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

        public Task<UserConfigSaveReceipt> SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default) =>
            SaveAsync(config, ct);

        public Task<UserConfigSaveReceipt> SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default) =>
            Task.FromResult(new UserConfigSaveReceipt(
                Accepted: true,
                CommandId: "command-github",
                AckStage: UserConfigCommandAckStage.Accepted,
                ActorId: "user-config-default",
                CorrelationId: "command-github",
                AckedAtUtc: DateTimeOffset.UtcNow));
    }

    private sealed class StubUserLlmCatalogPort(NyxIdLlmServicesResult result) : IUserLlmCatalogPort
    {
        public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct) =>
            Task.FromResult(result);

        public Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct) =>
            Task.FromResult(ChronoLlm);
    }
}
