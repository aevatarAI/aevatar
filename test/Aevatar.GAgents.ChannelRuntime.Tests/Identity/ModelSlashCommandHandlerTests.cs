using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.GAgents.NyxidChat.Slash;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StudioConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins the deterministic /model selection path for issue #556.
/// </summary>
public sealed class ModelSlashCommandHandlerTests
{
    private static readonly NyxIdLlmService ChronoLlm = new(
        UserServiceId: "svc-chrono",
        ServiceSlug: "chrono-llm",
        DisplayName: "chrono-llm shared",
        RouteValue: "/api/v1/proxy/s/chrono-llm",
        DefaultModel: "gpt-5.4",
        Models: ["gpt-5.4"],
        Status: "ready",
        Source: "shared",
        Allowed: true,
        Description: "Shared service");

    private static readonly NyxIdLlmService OpenAi = new(
        UserServiceId: "svc-openai",
        ServiceSlug: "openai-work",
        DisplayName: "OpenAI (work)",
        RouteValue: "/api/v1/proxy/s/openai-work",
        DefaultModel: "gpt-4o",
        Models: ["gpt-4o"],
        Status: "ready",
        Source: "user",
        Allowed: true,
        Description: "Work key");

    private static ChannelSlashCommandContext Context(
        string subAndArgs = "",
        string? bindingValue = "bnd_sender",
        string registrationScopeId = "owner-scope") => new()
    {
        CommandName = "model",
        ArgumentText = subAndArgs,
        Subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant",
            ExternalUserId = "ou_user",
        },
        BindingIdValue = bindingValue,
        RegistrationId = "reg",
        RegistrationScopeId = registrationScopeId,
        SenderId = "ou_user",
        SenderName = "Eric",
        IsPrivateChat = true,
    };

    [Fact]
    public void RequiresBinding_AndAliases_AreDeclared()
    {
        var handler = CreateHandler();

        handler.RequiresBinding.Should().BeTrue();
        handler.Aliases.Should().Equal("models", "llm");
        handler.Usage.ArgumentSyntax.Should().Contain("use");
    }

    [Fact]
    public async Task List_RendersAvailableServices()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "gpt-5.4", route: ChronoLlm.RouteValue) },
        };
        var handler = CreateHandler(queryPort: queryPort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "list"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("chrono-llm shared");
        reply.Text.Should().Contain("OpenAI (work)");
        reply.Text.Should().Contain("/model use");
        reply.Text.Should().Contain("✓");
    }

    [Fact]
    public async Task List_RendersSetupHint_WhenCatalogIsEmpty()
    {
        var catalog = new StubCatalogClient { Services = [] };
        var handler = CreateHandler(catalog);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("还没接入任何 LLM service");
        reply.Text.Should().Contain("/model preset");
        reply.Text.Should().Contain("chrono-llm");
    }

    [Fact]
    public async Task Use_Number_WritesRouteAndModel()
    {
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use 2"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("OpenAI (work)");
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.ScopeId.Should().Be("bnd_sender");
        saved.Config.PreferredLlmRoute.Should().Be(OpenAi.RouteValue);
        saved.Config.DefaultModel.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task Use_ServiceName_WritesMatchingRoute()
    {
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use openai"), default);

        reply.Should().NotBeNull();
        commandService.SavedConfigs.Should().ContainSingle()
            .Subject.Config.PreferredLlmRoute.Should().Be(OpenAi.RouteValue);
    }

    [Fact]
    public async Task Use_RawModel_WritesModelOnlyAndPreservesRoute()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "old-model", route: ChronoLlm.RouteValue) },
        };
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(queryPort: queryPort, commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use claude-sonnet-4"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("claude-sonnet-4");
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.Config.PreferredLlmRoute.Should().Be(ChronoLlm.RouteValue);
        saved.Config.DefaultModel.Should().Be("claude-sonnet-4");
    }

    [Fact]
    public async Task Preset_UseExistingService_WritesRouteAndModel()
    {
        var catalog = new StubCatalogClient { Services = [] };
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(catalog, commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "preset chrono-shared"), default);

        reply.Should().NotBeNull();
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.Config.PreferredLlmRoute.Should().Be(ChronoLlm.RouteValue);
        saved.Config.DefaultModel.Should().Be(ChronoLlm.DefaultModel);
    }

    [Fact]
    public async Task Reset_ClearsSenderRouteAndModel()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "old-model", route: ChronoLlm.RouteValue) },
        };
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(queryPort: queryPort, commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "reset"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("已清空");
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.Config.DefaultModel.Should().BeEmpty();
        saved.Config.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task Use_NumberOutsideAvailableRange_ReturnsFriendlyMessage()
    {
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use 7"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("没有编号 7");
        commandService.SavedConfigs.Should().BeEmpty();
    }

    private static ModelChannelSlashCommandHandler CreateHandler(
        StubCatalogClient? catalog = null,
        StubUserConfigQueryPort? queryPort = null,
        StubUserConfigCommandService? commandService = null)
    {
        catalog ??= new StubCatalogClient();
        queryPort ??= new StubUserConfigQueryPort();
        commandService ??= new StubUserConfigCommandService();

        var options = new DefaultUserLlmOptionsService(catalog, queryPort);
        var selection = new DefaultUserLlmSelectionService(options, catalog, queryPort, commandService);
        return new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance,
            options,
            selection,
            new TextUserLlmOptionsRenderer());
    }

    private static StudioConfig MakeConfig(
        string defaultModel,
        string route = UserConfigLlmRouteDefaults.Gateway) => new(
        DefaultModel: defaultModel,
        PreferredLlmRoute: route,
        RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
        LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
        RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
        GithubUsername: null,
        MaxToolRounds: 0);

    private sealed class StubCatalogClient : INyxIdLlmServiceCatalogClient
    {
        public IReadOnlyList<NyxIdLlmService> Services { get; init; } = [ChronoLlm, OpenAi];

        public UserLlmSetupHint SetupHint { get; init; } = new(
            "https://nyxid.example/services",
            [
                new UserLlmPreset(
                    "chrono-shared",
                    "使用 chrono-llm 共享额度",
                    "无需自带 key",
                    new UseExistingService(ChronoLlm.UserServiceId, ChronoLlm.RouteValue, ChronoLlm.DefaultModel)),
            ]);

        public Task<NyxIdLlmServicesResult> GetServicesAsync(
            UserLlmOptionsQuery query,
            string accessToken,
            CancellationToken ct) =>
            Task.FromResult(new NyxIdLlmServicesResult(Services, SetupHint));

        public Task<UserLlmSetupHint> GetSetupHintAsync(
            UserLlmOptionsQuery query,
            string accessToken,
            CancellationToken ct) =>
            Task.FromResult(SetupHint);

        public Task<NyxIdLlmService> ProvisionAsync(
            UserLlmSelectionContext context,
            string accessToken,
            string provisionEndpointId,
            CancellationToken ct) =>
            Task.FromResult(ChronoLlm);
    }

    private sealed class StubUserConfigQueryPort : IUserConfigQueryPort
    {
        public Dictionary<string, StudioConfig> ByScope { get; } = new(StringComparer.Ordinal);

        public Task<StudioConfig> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new StudioConfig(string.Empty));

        public Task<StudioConfig> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(ByScope.TryGetValue(scopeId, out var cfg) ? cfg : new StudioConfig(string.Empty));
    }

    private sealed class StubUserConfigCommandService : IUserConfigCommandService
    {
        public List<(string ScopeId, StudioConfig Config)> SavedConfigs { get; } = new();

        public Task SaveAsync(StudioConfig config, CancellationToken ct = default) =>
            SaveAsync(string.Empty, config, ct);

        public Task SaveAsync(string scopeId, StudioConfig config, CancellationToken ct = default)
        {
            SavedConfigs.Add((scopeId, config));
            return Task.CompletedTask;
        }

        public Task SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
