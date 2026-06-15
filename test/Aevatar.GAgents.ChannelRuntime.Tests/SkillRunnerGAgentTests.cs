using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerGAgentTests : IAsyncLifetime
{
    private InMemoryEventStore _store = null!;
    private ServiceProvider _serviceProvider = null!;
    private SkillRunnerGAgent _agent = null!;

    public async Task InitializeAsync()
    {
        _store = new InMemoryEventStore();
        _serviceProvider = BuildServiceProvider(_store);
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
    public async Task HandleInitializeAsync_NonFetchTemplate_DoesNotDeriveProxySuccessFromTemplate()
    {
        // The legacy default applies only to known fetch-and-summarize templates. Skills
        // that don't depend on tool data (future pure-LLM transformations) must not be
        // falsely failed when they legitimately fan out zero nyxid_proxy calls.
        var command = CreateInitializeCommand();
        command.RequiresNyxidProxySuccess = false;
        command.TemplateName = "future_pure_llm_template";

        await _agent.HandleInitializeAsync(command);

        _agent.State.RequiresNyxidProxySuccess.Should().BeFalse();
    }

    [Fact]
    public async Task TryCreateStreamingSink_WhenRequiresNyxidProxySuccess_ReturnsNull()
    {
        // PR #569 review (codex P1 on SkillRunnerGAgent.cs:351): when the run is gated by
        // EnsureToolStatusAllowsCompletion, streaming each delta to Lark would post the
        // hallucinated text live before the guard ran, then repost it on each retry.
        // TryCreateStreamingSink must short-circuit so chunked dispatch (which only fires
        // AFTER the guard) is the only path that reaches Lark for fanout-gated runs.
        AttachNyxIdApiClient(_agent, new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_success"}}"""));
        var command = CreateInitializeCommand();
        command.RequiresNyxidProxySuccess = true;
        await _agent.HandleInitializeAsync(command);
        _agent.State.RequiresNyxidProxySuccess.Should().BeTrue();

        var method = typeof(SkillRunnerGAgent).GetMethod(
            "TryCreateStreamingSink",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        var sink = method!.Invoke(_agent, []);

        sink.Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeAsync_DispatchesMembershipOnly_AndRunnerCommittedStateFeedsExecutionProjection()
    {
        // Refactor (iter1/cluster-001):
        //   Old pattern: the runner dispatched UserAgentCatalogExecutionUpdateCommand after membership upsert.
        //   New principle: runner committed state is sufficient for the catalog projector to materialize execution fields.
        var captured = new List<EventEnvelope>();
        var scheduler = Substitute.For<Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler>();
        scheduler
            .ScheduleTimeoutAsync(
                Arg.Any<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimeoutRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var req = call.Arg<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackTimeoutRequest>();
                return Task.FromResult(new Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease(
                    req.ActorId,
                    req.CallbackId,
                    1L,
                    Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));
            });
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        var dispatch = Substitute.For<IActorDispatchPort>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
                services.AddSingleton(scheduler);
            });
        var agent = CreateAgent("skill-runner-projection-regression", provider);
        await agent.ActivateAsync();
        var init = CreateInitializeCommand();
        init.ScheduleCron = "0 9 * * *";
        init.ScheduleTimezone = "UTC";

        await agent.HandleInitializeAsync(init);

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        var persisted = await provider.GetRequiredService<IEventStore>().GetEventsAsync("skill-runner-projection-regression");
        persisted.Should().HaveCount(2);

        var runnerState = agent.State.Clone();
        var writeDispatcher = new RecordingExecutionWriteDispatcher();
        var projector = new SkillRunnerExecutionProjector(
            writeDispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero)));

        await projector.ProjectAsync(
            new UserAgentCatalogMaterializationContext
            {
                RootActorId = "skill-runner-projection-regression",
                ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
            },
            new EventEnvelope
            {
                Id = "runner-state-2",
                Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero)),
                Route = EnvelopeRouteSemantics.CreateObserverPublication("skill-runner-projection-regression"),
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = persisted[^1],
                    StateRoot = Google.Protobuf.WellKnownTypes.Any.Pack(runnerState),
                }),
            },
            CancellationToken.None);

        writeDispatcher.Upserts.Should().ContainSingle();
        var doc = writeDispatcher.Upserts[0];
        doc.Id.Should().Be("skill-runner-projection-regression");
        doc.ActorId.Should().Be("skill-runner-projection-regression");
        doc.Status.Should().Be(SkillRunnerDefaults.StatusRunning);
        doc.NextRunAtUtc.Should().NotBeNull();
        doc.StateVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleTriggerAsync_WhenDisabled_PersistsRunnerOwnedRejectedEvent()
    {
        await _agent.HandleInitializeAsync(CreateInitializeCommand());
        await _agent.HandleDisableAsync(new DisableSkillRunnerCommand { Reason = "test" });

        await _agent.HandleTriggerAsync(new TriggerSkillRunnerExecutionCommand { Reason = "run_agent" });

        var persisted = await _store.GetEventsAsync("skill-runner-test");
        var rejected = persisted
            .Select(x => x.EventData)
            .Where(x => x.Is(SkillRunnerExecutionRejectedEvent.Descriptor))
            .Select(x => x.Unpack<SkillRunnerExecutionRejectedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        rejected.Reason.Should().Be(SkillRunnerDefaults.RejectionReasonRunnerDisabled);

        _agent.State.Enabled.Should().BeFalse();
        _agent.State.LastError.Should().Be(SkillRunnerDefaults.RejectionReasonRunnerDisabled);
        _agent.State.ErrorCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleInitializeAsync_ShouldDispatchCatalogCommandsThroughDispatchPort()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-dispatch-test", provider);
        await agent.ActivateAsync();

        await agent.HandleInitializeAsync(CreateInitializeCommand());

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        captured.Should().OnlyContain(envelope =>
            envelope.Route.PublisherActorId == "skill-runner-dispatch-test" &&
            envelope.Route.Direct.TargetActorId == UserAgentCatalogGAgent.WellKnownId);
        await catalogActor.DidNotReceive()
            .HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInitializeAsync_WithOwnerScope_DispatchesOwnerScopeOnlyCatalogCommand()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-owner-scope-only", provider);
        await agent.ActivateAsync();

        var ownerScope = OwnerScope.ForNyxIdNative("user-1");
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.OwnerScope = ownerScope;
#pragma warning disable CS0612 // stale legacy fields must not be emitted when owner_scope exists
        initialize.OutboundConfig.Platform = "nyxid";
        initialize.OutboundConfig.OwnerNyxUserId = "user-1";
#pragma warning restore CS0612

        await agent.HandleInitializeAsync(initialize);

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        var command = captured[0].Payload.Unpack<UserAgentCatalogUpsertCommand>();
        command.OwnerScope.Should().NotBeNull();
        command.OwnerScope!.MatchesStrictly(ownerScope).Should().BeTrue();
#pragma warning disable CS0612
        command.Platform.Should().BeEmpty();
        command.OwnerNyxUserId.Should().BeEmpty();
#pragma warning restore CS0612
    }

    [Fact]
    public async Task HandleInitializeAsync_WithLegacyOwnershipFields_DerivesOwnerScopeAndPreservesLegacyFields()
    {
        var catalogActor = Substitute.For<IActor>();
        var runtime = Substitute.For<IActorRuntime>();
        runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(catalogActor));

        var dispatch = Substitute.For<IActorDispatchPort>();
        var captured = new List<EventEnvelope>();
        dispatch.DispatchAsync(
                UserAgentCatalogGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(captured.Add),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var provider = BuildServiceProvider(
            new InMemoryEventStore(),
            services =>
            {
                services.AddSingleton(runtime);
                services.AddSingleton(dispatch);
            });
        var agent = CreateAgent("skill-runner-legacy-owner-fallback", provider);
        await agent.ActivateAsync();

        var initialize = CreateInitializeCommand();
#pragma warning disable CS0612 // legacy fallback branch must keep backwards-compatible writes
        initialize.OutboundConfig.OwnerNyxUserId = "legacy-user-1";
        initialize.OutboundConfig.Platform = "nyxid";
#pragma warning restore CS0612

        await agent.HandleInitializeAsync(initialize);

        captured.Should().ContainSingle();
        captured[0].Payload.Is(UserAgentCatalogUpsertCommand.Descriptor).Should().BeTrue();
        var command = captured[0].Payload.Unpack<UserAgentCatalogUpsertCommand>();
        command.OwnerScope.Should().NotBeNull();
        command.OwnerScope!.MatchesStrictly(OwnerScope.ForNyxIdNative("legacy-user-1")).Should().BeTrue();
#pragma warning disable CS0612
        command.Platform.Should().Be("nyxid");
        command.OwnerNyxUserId.Should().Be("legacy-user-1");
#pragma warning restore CS0612
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

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
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
            """{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
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
        // Same envelope shape as the production /summary failure log: NyxID wraps the Lark
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
            """{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
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
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldUseFailureNotificationSlug_WhenSetAndDistinctFromPrimary()
    {
        // Issue #423 §C: when a primary outbound delivery has just been rejected (e.g. cross-
        // tenant 99992364), retrying the failure notification through the SAME slug also
        // fails. The fix routes failure-notifications through the inbound channel-bot's slug
        // captured at agent-create time — by definition reachable, since the user just
        // messaged it. This test pins that the routing actually changes the proxy slug in
        // the outbound URL while the receive_id, body, and api key stay identical.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_user_1",
            LarkReceiveIdType = "open_id",
            FailureNotificationProviderSlug = "api-lark-bot-channel-loning",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_failure"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeTrySendFailureAsync(_agent, "Lark message delivery rejected (code=99992364): user id cross tenant.");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot-channel-loning/open-apis/im/v1/messages?receive_id_type=open_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("ou_user_1");
        // The failure-notification message itself should carry the original error so the user
        // sees the actionable hint in chat, not just an unsubscribe ping.
        var contentJson = body.RootElement.GetProperty("content").GetString();
        contentJson.Should().Contain("99992364");
        contentJson.Should().Contain("Skill runner failed");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldFallBackToPrimarySlug_WhenFailureNotificationSlugRejects()
    {
        // Defense-in-depth: the failure-notification slug MAY have been valid at create time
        // (in user-services) but become inactive (token revoked, bot uninstalled, etc.) by
        // the time the agent fires. If its send rejects, we still try the primary slug as
        // a last-resort attempt — better than the user seeing nothing.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_user_1",
            LarkReceiveIdType = "open_id",
            FailureNotificationProviderSlug = "api-lark-bot-channel-revoked",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new SequencedHandler(
            // Failure-notification slug rejects with a Nyx envelope error (e.g. 401 on the proxy)
            """{"error":true,"message":"upstream auth failed"}""",
            // Primary slug succeeds — best-effort recovery.
            """{"code":0,"msg":"success","data":{"message_id":"om_primary_failure"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeTrySendFailureAsync(_agent, "report rejected at primary");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.ToString()
            .Should().Contain("/proxy/s/api-lark-bot-channel-revoked/");
        handler.Requests[1].RequestUri!.ToString()
            .Should().Contain("/proxy/s/api-lark-bot/");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldSkipFailureSlug_WhenEqualToPrimary()
    {
        // No-op fallback: if the inbound slug equals the primary slug there is no recovery
        // benefit — same proxy = same rejection mode. AgentBuilderTool leaves the field
        // empty in this case, but pin the runtime guard too so a future
        // mis-capture doesn't pay double-POST cost just to fail twice.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_user_1",
            LarkReceiveIdType = "open_id",
            FailureNotificationProviderSlug = "api-lark-bot",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeTrySendFailureAsync(_agent, "primary failed");

        // Exactly one POST — no double-attempt against the same slug.
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Contain("/proxy/s/api-lark-bot/");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldUsePrimary_WhenFailureNotificationSlugIsEmpty()
    {
        // Backwards compat: agents created before #423 §C have an empty
        // FailureNotificationProviderSlug. The runtime must transparently fall back to the
        // existing single-attempt behavior — this test is the regression guard against the
        // failure-notification fallback ever introducing a hidden dependency on the new
        // field being populated.
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_user_1",
            LarkReceiveIdType = "open_id",
            // FailureNotificationProviderSlug intentionally not set (legacy state shape).
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
        AttachNyxIdApiClient(_agent, handler);

        await InvokeTrySendFailureAsync(_agent, "primary failed");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Contain("/proxy/s/api-lark-bot/");
    }

    [Fact]
    public async Task TrySendFailureAsync_ShouldSwallow_WhenBothSlugsReject()
    {
        // Final guarantee: TrySendFailureAsync MUST NOT throw, ever. HandleTriggerAsync is
        // already in the failure-event-persist path; an exception here would mask the
        // SkillRunnerExecutionFailedEvent persist (which surfaces last_error in
        // /agent-status, the one path users have to recover regardless of Lark visibility).
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            NyxApiKey = "nyx-api-key",
            LarkReceiveId = "ou_user_1",
            LarkReceiveIdType = "open_id",
            FailureNotificationProviderSlug = "api-lark-bot-channel-loning",
        };
        await _agent.HandleInitializeAsync(initialize);

        var handler = new SequencedHandler(
            """{"error":true,"message":"failure-slug down"}""",
            """{"code":99992364,"msg":"user id cross tenant"}""");
        AttachNyxIdApiClient(_agent, handler);

        // Should complete (not throw) even though both attempts fail.
        Func<Task> act = () => InvokeTrySendFailureAsync(_agent, "both broken");

        await act.Should().NotThrowAsync();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task BuildExecutionLlmControl_ShouldPinOwnerLlmConfigOverrides_WhenSourceReturnsConfig()
    {
        // Regression for the "/summary failed: Provider 'openai' not connected" report:
        // skill runners must honor the bot owner's pre-configured model + NyxID route + tool
        // cap — same shape AgentRunGAgent applies for nyxid-chat. Without it,
        // every scheduled run falls through to NyxIdLLMProvider's compile-time `gpt-5.4` +
        // gateway default, which the gateway routes to OpenAI and 400s for bot owners who
        // wired a custom NyxID service like `chrono-llm` at `/api/v1/proxy/s/chrono-llm`.
        var source = new StubOwnerLlmConfigSource(new OwnerLlmConfig(
            DefaultModel: "gpt-5.5",
            PreferredLlmRoute: "/api/v1/proxy/s/chrono-llm",
            MaxToolRounds: 7));

        var agent = CreateAgent("skill-runner-userconfig", _serviceProvider, source);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());

        var control = await InvokeBuildExecutionLlmControlAsync(agent);

        control.ModelOverride.Should().Be("gpt-5.5");
        control.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
        control.MaxToolRoundsOverride.Should().Be(7);
        control.NyxIdAccessToken.Should().Be("nyx-api-key");
        source.RequestedScopeIds.Should().ContainSingle().Which.Should().Be("scope-1");
    }

    [Fact]
    public async Task BuildExecutionLlmControl_ShouldOmitOverrides_WhenOwnerLlmConfigSourceIsAbsent()
    {
        // No host wiring (e.g. tests that don't compose Studio + the bridge): valid metadata
        // still comes out, no override keys leak, NyxIdLLMProvider falls through to its
        // compile-time defaults.
        await _agent.HandleInitializeAsync(CreateInitializeCommand());

        var control = await InvokeBuildExecutionLlmControlAsync(_agent);

        control.ModelOverride.Should().BeNull();
        control.NyxIdRoutePreference.Should().BeNull();
        control.MaxToolRoundsOverride.Should().BeNull();
        control.NyxIdAccessToken.Should().Be("nyx-api-key");
    }

    [Fact]
    public async Task BuildExecutionLlmControl_ShouldOmitOverrides_WhenOwnerLlmConfigFieldsAreEmpty()
    {
        // Bot owners who haven't saved any LLM preference get OwnerLlmConfig.Empty (or empty
        // strings via the host adapter). The applier must NOT pin empty values onto metadata,
        // because NyxIdLLMProvider treats a non-empty NyxIdRoutePreference of "" as a relative
        // path against the authority and produces an invalid URL.
        var source = new StubOwnerLlmConfigSource(OwnerLlmConfig.Empty);

        var agent = CreateAgent("skill-runner-userconfig-empty", _serviceProvider, source);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());

        var control = await InvokeBuildExecutionLlmControlAsync(agent);

        control.ModelOverride.Should().BeNull();
        control.NyxIdRoutePreference.Should().BeNull();
        control.MaxToolRoundsOverride.Should().BeNull();
    }

    [Fact]
    public async Task BuildExecutionLlmControl_ShouldFallBackQuietly_WhenOwnerLlmConfigSourceThrows()
    {
        // The source can throw on transient projection failures. The agent's execution turn
        // must still proceed with provider defaults — the applier catches and logs the
        // failure, never bubbles it up to the trigger handler.
        var source = new ThrowingOwnerLlmConfigSource();

        var agent = CreateAgent("skill-runner-userconfig-throws", _serviceProvider, source);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());

        var control = await InvokeBuildExecutionLlmControlAsync(agent);

        control.ModelOverride.Should().BeNull();
        control.NyxIdRoutePreference.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteSkillAsync_StreamingFinalizesBeforeCompletion()
    {
        var provider = new StubStreamingProviderFactory("a", "b", "c");
        var agent = CreateAgent("skill-runner-stream-coalesce", providerFactory: provider);
        await agent.ActivateAsync();
        var initialize = CreateInitializeCommand();
        initialize.OutboundConfig.LarkReceiveId = "oc_chat_1";
        initialize.OutboundConfig.LarkReceiveIdType = "chat_id";
        await agent.HandleInitializeAsync(initialize);
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"message_id":"om_stream"}}""",
            """{"code":0,"msg":"success","data":{}}""");
        AttachNyxIdApiClient(agent, handler);

        var output = await InvokeExecuteSkillAsync(agent);

        output.Should().Be("abc");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Method.Should().Be("POST");
        handler.Requests[1].Method.Method.Should().Be("PUT");
        ExtractLarkText(handler.Bodies[0]!).Should().Be("a");
        ExtractLarkText(handler.Bodies[1]!).Should().Be("abc");
    }

    [Fact]
    public async Task ExecuteSkillAsync_ShouldKeepOwnedScopeOutOfMetadata_AndCarryTypedToolContext()
    {
        var provider = new StubStreamingProviderFactory("done");
        var agent = CreateAgent("skill-runner-typed-context", providerFactory: provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeAsync(CreateInitializeCommand());

        await InvokeExecuteSkillAsync(agent);

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Metadata.Should().ContainKey(ChannelMetadataKeys.ConversationId);
        request.Metadata.Should().NotContainKey("scope_id");
        request.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        request.ToolContext.Should().NotBeNull();
        request.ToolContext!.Request.RequestId.Should().NotBeNullOrWhiteSpace();
        request.ToolContext.Caller.ScopeId.Should().Be("scope-1");
        request.ToolContext.Channel.RegistrationScopeId.Should().Be("scope-1");
        request.ToolContext.Credentials.NyxIdAccessToken.Should().Be("nyx-api-key");
        request.ToolContext.ExternalMetadata.Should().ContainKey(ChannelMetadataKeys.ConversationId);
        request.ToolContext.ExternalMetadata.Should().NotContainKey("scope_id");
    }

    [Fact]
    public async Task SkillRunnerStreamingRunState_CoalescesInsideThrottleAndDispatchesAfterThrottle()
    {
        var handler = new SequencedHandler(
            """{"code":0,"msg":"success","data":{"message_id":"om_stream"}}""",
            """{"code":0,"msg":"success","data":{}}""");
        var sink = CreateStreamingSink(handler);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero));
        var runState = CreateStreamingRunState(sink, TimeSpan.FromMilliseconds(300), time);

        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", "a");
        time.Advance(TimeSpan.FromMilliseconds(100));
        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", "ab");
        time.Advance(TimeSpan.FromMilliseconds(250));
        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", "abc");

        handler.Requests.Should().HaveCount(2);
        ExtractLarkText(handler.Bodies[0]!).Should().Be("a");
        ExtractLarkText(handler.Bodies[1]!).Should().Be("abc");
    }

    [Fact]
    public async Task SkillRunnerStreamingRunState_TruncatesBeforeDedupeAndSuppressesDuplicateFinal()
    {
        var handler = new SequencedHandler("""{"code":0,"msg":"success","data":{"message_id":"om_truncated"}}""");
        using var sink = CreateStreamingSink(handler);
        var runState = CreateStreamingRunState(sink, TimeSpan.Zero, TimeProvider.System);
        var longText = new string('x', SkillRunnerStreamingReplySink.MaxLarkTextLength + 100);

        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", longText);
        await InvokeStreamingRunStateAsync(runState, "OnDeltaAsync", longText + "more");
        await InvokeStreamingRunStateAsync(runState, "FinalizeAsync", longText + "final");

        handler.Requests.Should().ContainSingle("all three snapshots cap to the same Lark body");
        ExtractLarkText(handler.Bodies[0]!).Should().Be(SkillRunnerStreamingReplySink.TruncateForLark(longText));
    }

    private static async Task<LLMControlContext> InvokeBuildExecutionLlmControlAsync(
        SkillRunnerGAgent agent)
    {
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "BuildExecutionLlmControlAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        var task = (Task<LLMControlContext>)method!.Invoke(agent, [CancellationToken.None])!;
        return await task;
    }

    private static async Task<string> InvokeExecuteSkillAsync(SkillRunnerGAgent agent)
    {
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "ExecuteSkillAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        var task = (Task<string>)method!.Invoke(
            agent,
            [new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero), "test", CancellationToken.None])!;
        return await task;
    }

    private static object CreateStreamingRunState(
        SkillRunnerStreamingReplySink sink,
        TimeSpan throttle,
        TimeProvider timeProvider)
    {
        var type = typeof(SkillRunnerGAgent).GetNestedType(
            "SkillRunnerStreamingRunState",
            BindingFlags.NonPublic);
        type.Should().NotBeNull();
        return Activator.CreateInstance(type!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
            args: [sink, throttle, timeProvider],
            culture: null)!;
    }

    private static SkillRunnerStreamingReplySink CreateStreamingSink(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        return new SkillRunnerStreamingReplySink(
            new LarkOutboundDispatcher(client, NullLogger<LarkOutboundDispatcher>.Instance),
            new LarkSendNewMessageRequest(
                "nyx-api-key",
                "api-lark-bot",
                MessageType: "text",
                ContentJson: string.Empty,
                PrimaryTarget: new LarkReceiveTarget("oc_chat_1", "chat_id", FellBackToPrefixInference: false)),
            (_, detail) => detail,
            logger: null,
            editClient: client);
    }

    private static Task InvokeStreamingRunStateAsync(object runState, string methodName, string text)
    {
        var method = runState.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(runState, [text, CancellationToken.None])!;
    }

    private static string ExtractLarkText(string body)
    {
        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("content").GetString();
        content.Should().NotBeNull();
        using var contentDocument = JsonDocument.Parse(content!);
        return contentDocument.RootElement.GetProperty("text").GetString()!;
    }

    internal sealed class StubOwnerLlmConfigSource(OwnerLlmConfig config) : IOwnerLlmConfigSource
    {
        public List<string> RequestedScopeIds { get; } = new();

        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default)
        {
            RequestedScopeIds.Add(scopeId);
            return Task.FromResult(config);
        }
    }

    internal sealed class ThrowingOwnerLlmConfigSource : IOwnerLlmConfigSource
    {
        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default) =>
            throw new InvalidOperationException("projection unavailable");
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
        // Disambiguate against the 3-arg `SendOutputAsync(output, providerSlugOverride, ct)`
        // overload introduced for the failure-notification fallback (#423 §C). The 2-arg
        // overload still routes through the primary `NyxProviderSlug`, which is what every
        // existing test exercises.
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "SendOutputAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(CancellationToken)],
            modifiers: null);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(agent, [output, CancellationToken.None])!;
    }

    private static Task InvokeTrySendFailureAsync(SkillRunnerGAgent agent, string error)
    {
        var method = typeof(SkillRunnerGAgent).GetMethod(
            "TrySendFailureAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(agent, [error, CancellationToken.None])!;
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

    private sealed class RecordingExecutionWriteDispatcher : IProjectionWriteDispatcher<SkillRunnerExecutionDocument>
    {
        public List<SkillRunnerExecutionDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            SkillRunnerExecutionDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : Aevatar.CQRS.Projection.Core.Abstractions.IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
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
            var body = _responses.Count > 0 ? _responses.Dequeue() : """{"code":0,"msg":"success","data":{"message_id":"om_success"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private SkillRunnerGAgent CreateAgent(
        string actorId,
        ServiceProvider? serviceProvider = null,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        ILLMProviderFactory? providerFactory = null)
    {
        var resolvedServices = serviceProvider ?? _serviceProvider;
        var agent = new SkillRunnerGAgent(
            llmProviderFactory: providerFactory,
            ownerLlmConfigSource: ownerLlmConfigSource)
        {
            Services = resolvedServices,
            EventSourcingBehaviorFactory =
                resolvedServices.GetRequiredService<IEventSourcingBehaviorFactory<SkillRunnerState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static ServiceProvider BuildServiceProvider(
        IEventStore eventStore,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventStore);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static InitializeSkillRunnerCommand CreateInitializeCommand() => new()
    {
        SkillName = "summary",
        TemplateName = "summary",
        SkillContent = "You are a summary report runner.",
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

    private sealed class StubStreamingProviderFactory(params string[] deltas) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "stub";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var delta in deltas)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new LLMStreamChunk { DeltaContent = delta };
            }

            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];
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
