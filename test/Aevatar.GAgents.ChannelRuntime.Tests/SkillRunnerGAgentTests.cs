using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerGAgentTests : IAsyncLifetime
{
    private InMemoryEventStore _store = null!;
    private ServiceProvider _serviceProvider = null!;
    private SkillRunnerGAgent _agent = null!;

    public async Task InitializeAsync()
    {
        _store = new InMemoryEventStore();

        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(_store);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));

        _serviceProvider = services.BuildServiceProvider();
        _agent = CreateAgent("skill-runner-test");
        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenSamplingFieldsAreOmitted_ShouldKeepThemUnset()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommand());

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.HasTemperature.Should().BeFalse();
        initialized.HasMaxTokens.Should().BeFalse();

        _agent.State.HasTemperature.Should().BeFalse();
        _agent.State.HasMaxTokens.Should().BeFalse();
        _agent.State.MaxToolRounds.Should().Be(SkillRunnerDefaults.DefaultMaxToolRounds);
        _agent.State.MaxHistoryMessages.Should().Be(SkillRunnerDefaults.DefaultMaxHistoryMessages);
        _agent.EffectiveConfig.Temperature.Should().BeNull();
        _agent.EffectiveConfig.MaxTokens.Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenTemperatureIsExplicitZero_ShouldPreserveIt()
    {
        var command = CreateInitializeCommand();
        command.Temperature = 0;

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.HasTemperature.Should().BeTrue();
        initialized.Temperature.Should().Be(0);

        _agent.State.HasTemperature.Should().BeTrue();
        _agent.State.Temperature.Should().Be(0);
        _agent.EffectiveConfig.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task HandleInitializeAsync_WhenMaxTokensIsExplicitZero_ShouldPreserveStateAndSuppressEffectiveConfig()
    {
        var command = CreateInitializeCommand();
        command.MaxTokens = 0;

        await _agent.HandleInitializeAsync(command);

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var initialized = persisted.Should().ContainSingle().Subject.EventData.Unpack<SkillRunnerInitializedEvent>();
        initialized.HasMaxTokens.Should().BeTrue();
        initialized.MaxTokens.Should().Be(0);

        _agent.State.HasMaxTokens.Should().BeTrue();
        _agent.State.MaxTokens.Should().Be(0);
        _agent.EffectiveConfig.MaxTokens.Should().BeNull();
    }

    [Fact]
    public async Task SendOutputAsync_ShouldUseTypedReceiveTarget_WhenLarkReceiveIdIsPopulated()
    {
        // Initialize with typed fields set (the shape AgentBuilderTool now writes for p2p flows).
        // Even though the legacy ConversationId is an `oc_*` chat id (which Lark would also accept
        // with chat_id), the typed open_id target should be sent verbatim — this is what fixes the
        // production 400 where the relay's ConversationId fell through to ou_*.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_legacy",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_user_1",
            LarkReceiveIdType = "open_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_1"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeSendOutputAsync(_agent, "scheduled report body");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=open_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("ou_user_1");
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldFallBackToConversationIdPrefixInference_ForLegacyState()
    {
        // Backward compatibility: state persisted before the typed lark_receive_id fields existed
        // still resolves through the prefix heuristic on ConversationId. The send still succeeds
        // (no exception); the sender emits a Debug breadcrumb that is not visible to xUnit.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "ou_legacy_user",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success"}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeSendOutputAsync(_agent, "legacy report body");

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=open_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("ou_legacy_user");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldThrow_WhenLarkBusinessCodeIsNonZero()
    {
        // Lark reports business errors as HTTP 200 with `code != 0`. Ignoring the response would
        // let HandleTriggerAsync persist SkillRunnerExecutionCompletedEvent on a silent failure.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_user_1",
            LarkReceiveIdType = "open_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":230002,"msg":"invalid receive_id"}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*code=230002*");
        assertion.WithMessage("*invalid receive_id*");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldThrow_WhenNyxProxyEnvelopeReportsError()
    {
        // HTTP non-2xx from NyxID gets packaged into a Nyx envelope that ProxyRequestAsync returns
        // verbatim. Ignoring it would mask transport / auth failures.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_user_1",
            LarkReceiveIdType = "open_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"error":true,"message":"upstream timeout"}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*upstream timeout*");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldIncludeRecreateHint_When_LarkRejectsAsCrossAppOpenId()
    {
        // PR #409 review (pulls/409#review-4175198266): after this fix new agents capture
        // union_id, but agents created before the fix still have `LarkReceiveIdType=open_id`
        // pinned to a relay-app-scoped `ou_*`. Their next scheduled run hits Lark
        // `99992361 open_id cross app` and the user sees the bare error in `/agent-status`'s
        // `last_error` with no clue what to do. Surface explicit "delete and recreate" guidance
        // so the failure becomes self-documenting.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_relay_app_user_1",
            LarkReceiveIdType = "open_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler(
            """{"code":99992361,"msg":"open_id cross app","error":{"message":"Refer to the documentation"}}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*code=99992361*");
        assertion.WithMessage("*open_id cross app*");
        // The hint must be actionable enough that the user can recover without reading source.
        assertion.WithMessage("*before cross-app union_id ingress existed*");
        assertion.WithMessage("*/agents*");
        assertion.WithMessage("*Delete*");
        assertion.WithMessage("*/daily*");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldRetryWithFallback_When_PrimaryRejectedAsBotNotInChat_ViaHttp400Envelope()
    {
        // Reviewer (PR #412 r3141700469): production failures arrive through
        // `NyxIdApiClient.SendAsync` as an HTTP-400 Nyx envelope:
        // `{"error": true, "status": 400, "body": "{\"code\":230002,...}"}`. The previous
        // `LarkProxyResponse.TryGetError` returned true for that shape but left
        // `larkCode=null` because it didn't parse the nested `body`, so the BotNotInChat
        // retry branch never fired in the actual production path. Pin the wrapped envelope
        // shape end-to-end.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "oc_dm_chat_1",
            LarkReceiveIdType = "chat_id",
            LarkReceiveIdFallback = "on_user_1",
            LarkReceiveIdTypeFallback = "union_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        // First (primary) attempt: NyxIdApiClient.SendAsync HTTP-400 envelope wrapping Lark
        // 230002. Second (fallback) attempt: clean success.
        var handler = new SequencedHandler(
            """{"error": true, "status": 400, "body": "{\"code\":230002,\"msg\":\"Bot is not in the chat\"}"}""",
            """{"code":0,"msg":"success"}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeSendOutputAsync(_agent, "report");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");
        handler.Bodies[1].Should().Contain("\"receive_id\":\"on_user_1\"");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldThrowCrossTenantHint_When_LarkCodeNestedInHttp400Body()
    {
        // Same envelope shape as the production /daily failure log: NyxID wraps the Lark
        // 99992364 as a string body inside an HTTP-400 Nyx envelope. The cross-tenant
        // recreate-the-agent hint (PR #412) only fires when the parser surfaces the nested
        // Lark code; previously it never did. Pin both the recovery hint and the nested-body
        // unwrap together.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "on_relay_tenant_user_1",
            LarkReceiveIdType = "union_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler(
            """{"error": true, "status": 400, "body": "{\"code\":99992364,\"msg\":\"user id cross tenant\"}"}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*99992364*");
        assertion.WithMessage("*different tenant*");
        assertion.WithMessage("*/agents*");
        assertion.WithMessage("*Delete*");
        assertion.WithMessage("*/daily*");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldRetryWithFallback_When_PrimaryRejectedAsBotNotInChat()
    {
        // Reviewer concern (codex-bot, P1, PR #412): chat_id-first regresses cross-app
        // same-tenant deployments where the outbound app is not a member of the inbound DM
        // chat — Lark returns `230002 bot not in chat` for chat_id-typed sends. Captured the
        // union_id at create time as a fallback; assert the runtime retries once with the
        // fallback typed pair when the primary attempt fails with 230002, and that the retry
        // body uses the fallback `receive_id` / `receive_id_type`.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "oc_dm_chat_1",
            LarkReceiveIdType = "chat_id",
            LarkReceiveIdFallback = "on_user_1",
            LarkReceiveIdTypeFallback = "union_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new SequencedHandler(
            """{"code":230002,"msg":"Bot is not in the chat"}""",
            """{"code":0,"msg":"success"}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeSendOutputAsync(_agent, "report");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Bodies[0].Should().Contain("\"receive_id\":\"oc_dm_chat_1\"");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");
        handler.Bodies[1].Should().Contain("\"receive_id\":\"on_user_1\"");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldNotRetry_When_PrimaryRejectedWithDifferentLarkCode()
    {
        // Only `230002 bot not in chat` triggers the fallback retry. Other Lark codes (e.g.
        // 99992364 cross_tenant) propagate immediately so the user sees the actionable
        // recovery hint for the actual failure mode rather than a misleading retry.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "oc_dm_chat_1",
            LarkReceiveIdType = "chat_id",
            LarkReceiveIdFallback = "on_user_1",
            LarkReceiveIdTypeFallback = "union_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new SequencedHandler(
            """{"code":99992364,"msg":"user id cross tenant"}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*99992364*");
        handler.Requests.Should().ContainSingle("only 230002 should trigger the fallback retry");
    }

    [Fact]
    public async Task SendOutputAsync_ShouldIncludeRecreateHint_When_LarkRejectsAsCrossTenantUserId()
    {
        // Production failure mode after PR #409 switched p2p to union_id: NyxID's relay-side
        // ingress and `s/api-lark-bot` proxy turned out to be in different Lark tenants, so even
        // union_id is rejected. This PR pivots to chat_id-first; the cross_tenant error code is
        // surfaced with the same recreate guidance so legacy agents (still pinned to union_id)
        // give users a way to recover without reading source.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "on_relay_tenant_user_1",
            LarkReceiveIdType = "union_id",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler(
            """{"code":99992364,"msg":"user id cross tenant","error":{"log_id":"L1"}}""");
        AttachNyxIdApiClient(_agent, handler);

        Func<Task> act = () => InvokeSendOutputAsync(_agent, "report");

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*code=99992364*");
        assertion.WithMessage("*user id cross tenant*");
        assertion.WithMessage("*different tenant*");
        assertion.WithMessage("*chat_id-preferred*");
        assertion.WithMessage("*/agents*");
        assertion.WithMessage("*Delete*");
        assertion.WithMessage("*/daily*");
    }

    private static void AttachNyxIdApiClient(SkillRunnerGAgent agent, HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var field = typeof(SkillRunnerGAgent).GetField(
            "_nyxIdApiClient",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        field!.SetValue(agent, client);
    }

    private static Task InvokeSendOutputAsync(SkillRunnerGAgent agent, string output)
    {
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "SendOutputAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(agent, [output, CancellationToken.None])!;
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// Returns a different response per request in the order given. Used to simulate the
    /// `bot not in chat` rejection on the primary attempt followed by a successful fallback
    /// retry.
    /// </summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();

        public SequencedHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var body = _responses.Count > 0 ? _responses.Dequeue() : """{"code":0,"msg":"success"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private SkillRunnerGAgent CreateAgent(string actorId)
    {
        var agent = new SkillRunnerGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<SkillRunnerState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static InitializeSkillRunnerCommand CreateInitializeCommand() => new()
    {
        SkillName = "daily_report",
        TemplateName = "daily_report",
        SkillContent = "You are a daily report runner.",
        ExecutionPrompt = "Run the report.",
        ScheduleCron = string.Empty,
        ScheduleTimezone = SkillRunnerDefaults.DefaultTimezone,
        Enabled = true,
        ScopeId = "scope-1",
        ProviderName = SkillRunnerDefaults.DefaultProviderName,
        OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
        },
    };

    private static void AssignActorId(GAgentBase agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
