using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.NyxidChat.Slash;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
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
    public void RequiresBinding_IsTrue()
    {
        var handler = new ModelChannelSlashCommandHandler(NullLogger<ModelChannelSlashCommandHandler>.Instance);
        handler.RequiresBinding.Should().BeTrue();
    }

    [Fact]
    public async Task List_ShowsSenderAndOwnerModels()
    {
        // Owner default lives under the registration scope, NOT the ambient
        // overload — channel inbound has no Studio HTTP request behind it,
        // so the ambient resolver would return `default` / unrelated state
        // (PR #521 codex review #11). The handler now reads
        // queryPort.GetAsync(context.RegistrationScopeId, ct).
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope =
            {
                ["bnd_sender"] = MakeConfig("sender-claude"),
                ["owner-scope"] = MakeConfig("owner-gpt"),
            },
        };
        var handler = new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance, queryPort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "list"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("sender-claude");
        reply.Text.Should().Contain("owner-gpt");
        reply.Text.Should().Contain("/model use");
        // Pin the scope semantics: ambient overload must NOT be used for
        // owner-default lookup on the channel inbound path.
        queryPort.AmbientCalls.Should().Be(0);
        queryPort.ScopedCalls.Should().Contain("owner-scope");
    }

    [Fact]
    public async Task List_HandlesMissingSenderConfig()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope =
            {
                // No sender override — only bot owner default under the
                // registration scope.
                ["owner-scope"] = MakeConfig("owner-gpt"),
            },
        };
        var handler = new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance, queryPort);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("(未设置)");
        reply.Text.Should().Contain("owner-gpt");
    }

    [Fact]
    public async Task List_FallsBackToAmbient_WhenRegistrationScopeIdEmpty()
    {
        // Defence: a misconfigured registration with an empty scope must
        // not throw IUserConfigQueryPort.GetAsync(string) on a blank id;
        // fall back to the ambient overload so /model list still renders
        // *something* useful instead of a stack trace.
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig("sender-claude") },
            Ambient = MakeConfig("owner-gpt"),
        };
        var handler = new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance, queryPort);

        var reply = await handler.HandleAsync(
            Context(subAndArgs: "list", registrationScopeId: string.Empty),
            default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("owner-gpt");
        queryPort.AmbientCalls.Should().Be(1);
    }

    [Fact]
    public async Task Use_WithoutModelName_ReturnsUsage()
    {
        var queryPort = new StubUserConfigQueryPort();
        var commandService = new StubUserConfigCommandService();
        var handler = new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance, queryPort, commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use"), default);

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
        var handler = new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance, queryPort, commandService);

        var reply = await handler.HandleAsync(
            Context(subAndArgs: "use claude-opus-4-7"),
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
        var handler = new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance, queryPort, commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "reset"), default);

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
        public int AmbientCalls { get; private set; }
        public List<string> ScopedCalls { get; } = new();

        public Task<StudioConfig> GetAsync(CancellationToken ct = default)
        {
            AmbientCalls++;
            return Task.FromResult(Ambient);
        }

        public Task<StudioConfig> GetAsync(string scopeId, CancellationToken ct = default)
        {
            ScopedCalls.Add(scopeId);
            return Task.FromResult(ByScope.TryGetValue(scopeId, out var cfg) ? cfg : new StudioConfig(string.Empty));
        }
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
