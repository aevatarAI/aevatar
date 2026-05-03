using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.GAgents.NyxidChat.Slash;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
        handler.Aliases.Should().Equal("models", "llm", "route");
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
        reply.Text.Should().Contain("/route use");
        reply.Text.Should().Contain("✓");
    }

    [Fact]
    public async Task List_ReturnsFriendlyMessage_WhenCatalogLookupFails()
    {
        var catalog = new StubCatalogClient
        {
            GetServicesError = new InvalidOperationException("NyxID LLM catalog unavailable"),
        };
        var handler = CreateHandler(catalog);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("读取或更新 NyxID LLM service 设置失败");
        reply.Text.Should().NotContain("NyxID LLM catalog unavailable");
    }

    [Fact]
    public async Task List_RequestsProxyScope_ForNyxIdLlmApi()
    {
        var broker = new RecordingCapabilityBroker();
        var handler = CreateHandler(broker: broker);

        await handler.HandleAsync(Context(), default);

        broker.RequestedScopes.Should().ContainSingle().Which.Should().Be(AevatarOAuthClientScopes.Proxy);
    }

    [Fact]
    public async Task List_SelfHealsAndRebindsMessage_WhenBindingScopeMissing()
    {
        // NyxID rejects the binding's scope set: the binding was issued before
        // aevatar's DCR started requesting `proxy`, so the broker can no longer
        // mint LLM-API tokens for it. Self-heal by revoking the local actor so
        // /init is unblocked, AND tell the user.
        var actorRuntime = new RecordingActorRuntime();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingScopeMismatchException(Context().Subject)),
            actorRuntime: actorRuntime);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("缺少 LLM route 权限");
        reply.Text.Should().Contain("已自动清理");
        reply.Text.Should().Contain("/init");
        AssertRevokeBindingDispatched(actorRuntime, expectedReason: "auto_self_heal_scope_mismatch");
    }

    [Fact]
    public async Task List_SelfHealsAndRebindsMessage_WhenBindingRevokedRemotely()
    {
        // NyxID itself returned binding_revoked (e.g. user revoked at NyxID admin
        // or the binding tied to a re-DCR'd cluster client_id was invalidated).
        // Wipe the local readmodel so /init isn't blocked by stale state.
        var actorRuntime = new RecordingActorRuntime();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingRevokedException(Context().Subject)),
            actorRuntime: actorRuntime);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("失效");
        reply.Text.Should().Contain("已自动清理");
        reply.Text.Should().Contain("/init");
        AssertRevokeBindingDispatched(actorRuntime, expectedReason: "auto_self_heal_remote_revoked");
    }

    [Fact]
    public async Task List_SelfHealsAndRebindsMessage_WhenBindingNotFoundRemotely()
    {
        var actorRuntime = new RecordingActorRuntime();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingNotFoundException(Context().Subject)),
            actorRuntime: actorRuntime);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("不可用");
        reply.Text.Should().Contain("已自动清理");
        reply.Text.Should().Contain("/init");
        AssertRevokeBindingDispatched(actorRuntime, expectedReason: "auto_self_heal_remote_not_found");
    }

    [Fact]
    public async Task List_StillReturnsUserMessage_WhenSelfHealActorRuntimeMissing()
    {
        // Deployments without IActorRuntime registered (CLI playground, certain
        // demo hosts) should still surface the user-facing message — the
        // self-heal degrades to "tell the user, hope they /unbind" rather than
        // crashing the slash command.
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingRevokedException(Context().Subject)),
            actorRuntime: null);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("失效");
    }

    private static void AssertRevokeBindingDispatched(RecordingActorRuntime runtime, string expectedReason)
    {
        runtime.Dispatched.Should().ContainSingle("self-heal must dispatch exactly one local revoke");
        var (actorId, envelope) = runtime.Dispatched[0];
        actorId.Should().Be(Context().Subject.ToActorId());
        envelope.Route.Direct.TargetActorId.Should().Be(actorId);

        var revoke = envelope.Payload.Unpack<RevokeBindingCommand>();
        revoke.Reason.Should().Be(expectedReason);
        revoke.ExternalSubject.Platform.Should().Be("lark");
        revoke.ExternalSubject.Tenant.Should().Be("tenant");
        revoke.ExternalSubject.ExternalUserId.Should().Be("ou_user");
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
    public async Task Use_ServiceNameAndModel_WritesRouteAndModelOverride()
    {
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use chrono-llm gpt-5.5"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("chrono-llm shared");
        reply.Text.Should().Contain("gpt-5.5");
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.Config.PreferredLlmRoute.Should().Be(ChronoLlm.RouteValue);
        saved.Config.DefaultModel.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task Use_DisplayNameWithSpaces_WritesMatchingRouteWithoutModelOverride()
    {
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use OpenAI (work)"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("OpenAI (work)");
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.Config.PreferredLlmRoute.Should().Be(OpenAi.RouteValue);
        saved.Config.DefaultModel.Should().Be(OpenAi.DefaultModel);
    }

    [Fact]
    public async Task Use_NumberAndModel_WritesRouteAndModelOverride()
    {
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(commandService: commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use 2 gpt-5.5"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("OpenAI (work)");
        reply.Text.Should().Contain("gpt-5.5");
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.Config.PreferredLlmRoute.Should().Be(OpenAi.RouteValue);
        saved.Config.DefaultModel.Should().Be("gpt-5.5");
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
    public async Task Preset_ProvisionThenUse_PreservesCurrentModel_WhenProvisionedServiceHasNoDefaultModel()
    {
        var provisioned = ChronoLlm with { DefaultModel = null };
        var catalog = new StubCatalogClient
        {
            Services = [],
            ProvisionedService = provisioned,
            SetupHint = new UserLlmSetupHint(
                "https://nyxid.example/services",
                [
                    new UserLlmPreset(
                        "chrono-provision",
                        "Provision chrono",
                        "Provision shared service",
                        new ProvisionThenUse("chrono/shared")),
                ]),
        };
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "current-model", route: OpenAi.RouteValue) },
        };
        var commandService = new StubUserConfigCommandService();
        var handler = CreateHandler(catalog, queryPort, commandService);

        var reply = await handler.HandleAsync(Context(subAndArgs: "preset chrono-provision"), default);

        reply.Should().NotBeNull();
        var saved = commandService.SavedConfigs.Should().ContainSingle().Subject;
        saved.Config.PreferredLlmRoute.Should().Be(provisioned.RouteValue);
        saved.Config.DefaultModel.Should().Be("current-model");
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
        StubUserConfigCommandService? commandService = null,
        INyxIdCapabilityBroker? broker = null,
        IActorRuntime? actorRuntime = null)
    {
        catalog ??= new StubCatalogClient();
        queryPort ??= new StubUserConfigQueryPort();
        commandService ??= new StubUserConfigCommandService();

        var provider = new ServiceCollection()
            .AddSingleton<IUserConfigQueryPort>(queryPort)
            .AddSingleton<IUserConfigCommandService>(commandService)
            .BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = new DefaultUserLlmOptionsService(catalog, scopeFactory, broker);
        var selection = new DefaultUserLlmSelectionService(options, catalog, scopeFactory, broker);
        return new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance,
            options,
            selection,
            new TextUserLlmOptionsRenderer(),
            actorRuntime);
    }

    /// <summary>
    /// Records every <see cref="EventEnvelope"/> the handler dispatches so
    /// tests can assert the binding self-heal fires <c>RevokeBindingCommand</c>
    /// to the local actor when NyxID rejects the binding.
    /// </summary>
    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatched { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            var actor = Substitute.For<IActor>();
            actor.Id.Returns(id ?? string.Empty);
            actor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Dispatched.Add((id ?? string.Empty, call.Arg<EventEnvelope>()));
                    return Task.CompletedTask;
                });
            return Task.FromResult(actor);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IActor?> GetAsync(string id) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string id) => throw new NotImplementedException();
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => throw new NotImplementedException();
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
        public NyxIdLlmService ProvisionedService { get; init; } = ChronoLlm;
        public Exception? GetServicesError { get; init; }

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
            CancellationToken ct)
        {
            if (GetServicesError is not null)
                return Task.FromException<NyxIdLlmServicesResult>(GetServicesError);

            return Task.FromResult(new NyxIdLlmServicesResult(Services, SetupHint));
        }

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
            Task.FromResult(ProvisionedService);
    }

    private sealed class RecordingCapabilityBroker : INyxIdCapabilityBroker
    {
        public List<string> RequestedScopes { get; } = new();

        public Task<BindingChallenge> StartExternalBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default)
        {
            RequestedScopes.Add(scope.Value);
            return Task.FromResult(new CapabilityHandle
            {
                AccessToken = "token-for-model-list",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                Scope = scope.Value,
            });
        }
    }

    private sealed class ThrowingCapabilityBroker : INyxIdCapabilityBroker
    {
        private readonly Exception _exception;

        public ThrowingCapabilityBroker(Exception exception) => _exception = exception;

        public Task<BindingChallenge> StartExternalBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            Task.FromException<CapabilityHandle>(_exception);
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
