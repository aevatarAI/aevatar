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

        var result = await service.SaveAsync(
            "bearer",
            new SaveUserConfigCommand(DefaultModel: "chrono-llm/gpt-5.5"));

        result.DefaultModel.Should().Be("gpt-5.5");
        result.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/chrono-llm");
        commandService.Saved.Should().ContainSingle().Which.Should().Be(result);
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepPrefixedProviderModel_WhenCatalogDoesNotResolveRoute()
    {
        var commandService = new RecordingUserConfigCommandService();
        var service = CreateService(commandService: commandService);

        var result = await service.SaveAsync(
            "bearer",
            new SaveUserConfigCommand(DefaultModel: "openai/gpt-5"));

        result.DefaultModel.Should().Be("openai/gpt-5");
        result.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        commandService.Saved.Should().ContainSingle().Which.Should().Be(result);
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepPrefixedProviderModel_WhenBearerTokenIsMissing()
    {
        var commandService = new RecordingUserConfigCommandService();
        var service = CreateService(commandService: commandService);

        var result = await service.SaveAsync(new SaveUserConfigCommand(
            DefaultModel: "chrono-llm/gpt-5.5"));

        result.DefaultModel.Should().Be("chrono-llm/gpt-5.5");
        result.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        commandService.Saved.Should().ContainSingle().Which.Should().Be(result);
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

        var result = await service.SaveLlmPreferenceAsync(
            "bearer",
            new SaveUserLlmPreferenceCommand(Model: "chrono-llm/gpt-5.5"));

        result.DefaultModel.Should().Be("gpt-5.5");
        result.PreferredLlmRoute.Should().Be(ChronoLlm.RouteValue);
        commandService.Saved.Should().ContainSingle().Which.Should().Be(result);
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
        var service = CreateService();

        var result = await service.SaveLlmPreferenceAsync(
            "bearer",
            new SaveUserLlmPreferenceCommand(ServiceId: "chrono-llm", Model: "chrono-llm/gpt-5.5"));

        result.DefaultModel.Should().Be("gpt-5.5");
        result.PreferredLlmRoute.Should().Be(ChronoLlm.RouteValue);
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

        public Task SaveAsync(UserConfig config, CancellationToken ct = default)
        {
            Saved.Add(config);
            return Task.CompletedTask;
        }

        public Task SaveAsync(string scopeId, UserConfig config, CancellationToken ct = default) =>
            SaveAsync(config, ct);

        public Task SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubUserLlmCatalogPort(NyxIdLlmServicesResult result) : IUserLlmCatalogPort
    {
        public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct) =>
            Task.FromResult(result);

        public Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct) =>
            Task.FromResult(ChronoLlm);
    }
}
