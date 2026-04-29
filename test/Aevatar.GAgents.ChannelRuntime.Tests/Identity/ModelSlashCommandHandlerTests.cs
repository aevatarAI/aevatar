using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.NyxidChat.Slash;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StudioConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins the user-visible behaviour of /model (issue #513 phase 5):
///   - <c>/model</c> shows current sender model + bot owner default
///   - <c>/model use &lt;name&gt;</c> writes user-config-&lt;binding-id&gt;
///   - <c>/model reset</c> clears the sender override
/// </summary>
public sealed class ModelSlashCommandHandlerTests
{
    private static ChannelSlashCommandContext Context(
        IServiceProvider services,
        string subAndArgs = "",
        string? bindingValue = "bnd_sender") => new()
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
        RegistrationScopeId = "owner-scope",
        SenderId = "ou_user",
        SenderName = "Eric",
        IsPrivateChat = true,
        Services = services,
    };

    private static IServiceProvider BuildServices(
        StubUserConfigQueryPort queryPort,
        StubUserConfigCommandService? commandService = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUserConfigQueryPort>(queryPort);
        if (commandService is not null)
            services.AddSingleton<IUserConfigCommandService>(commandService);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RequiresBinding_IsTrue()
    {
        var handler = new ModelChannelSlashCommandHandler(NullLogger<ModelChannelSlashCommandHandler>.Instance);
        handler.RequiresBinding.Should().BeTrue();
    }

    [Fact]
    public async Task List_ShowsSenderAndOwnerModels()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope =
            {
                ["bnd_sender"] = MakeConfig("sender-claude"),
            },
            Ambient = MakeConfig("owner-gpt"),
        };
        var handler = new ModelChannelSlashCommandHandler(NullLogger<ModelChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(BuildServices(queryPort), subAndArgs: "list"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("sender-claude");
        reply.Text.Should().Contain("owner-gpt");
        reply.Text.Should().Contain("/model use");
    }

    [Fact]
    public async Task List_HandlesMissingSenderConfig()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            // No sender override, just bot owner default.
            Ambient = MakeConfig("owner-gpt"),
        };
        var handler = new ModelChannelSlashCommandHandler(NullLogger<ModelChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(BuildServices(queryPort)), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("(未设置)");
        reply.Text.Should().Contain("owner-gpt");
    }

    [Fact]
    public async Task Use_WithoutModelName_ReturnsUsage()
    {
        var queryPort = new StubUserConfigQueryPort();
        var commandService = new StubUserConfigCommandService();
        var handler = new ModelChannelSlashCommandHandler(NullLogger<ModelChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(BuildServices(queryPort, commandService), subAndArgs: "use"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("/model use <model-name>");
        commandService.SavedConfigs.Should().BeEmpty();
    }

    [Fact]
    public async Task Use_WritesSenderModel()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(string.Empty) },
        };
        var commandService = new StubUserConfigCommandService();
        var handler = new ModelChannelSlashCommandHandler(NullLogger<ModelChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(
            Context(BuildServices(queryPort, commandService), subAndArgs: "use claude-opus-4-7"),
            default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("claude-opus-4-7");
        commandService.SavedConfigs.Should().ContainSingle();
        var saved = commandService.SavedConfigs[0];
        saved.ScopeId.Should().Be("bnd_sender");
        saved.Config.DefaultModel.Should().Be("claude-opus-4-7");
    }

    [Fact]
    public async Task Reset_ClearsSenderModel()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig("sender-old") },
        };
        var commandService = new StubUserConfigCommandService();
        var handler = new ModelChannelSlashCommandHandler(NullLogger<ModelChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(BuildServices(queryPort, commandService), subAndArgs: "reset"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("已清空");
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.ScopeId.Should().Be("bnd_sender");
        saved.Config.DefaultModel.Should().BeEmpty();
    }

    private static StudioConfig MakeConfig(string defaultModel) => new(
        DefaultModel: defaultModel,
        PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
        RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
        LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
        RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
        GithubUsername: null,
        MaxToolRounds: 0);

    private sealed class StubUserConfigQueryPort : IUserConfigQueryPort
    {
        public Dictionary<string, StudioConfig> ByScope { get; } = new(StringComparer.Ordinal);
        public StudioConfig Ambient { get; set; } = new(string.Empty);

        public Task<StudioConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(Ambient);

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
