using System.Net;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class WorkflowRunDeliveryGAgentTests
{
    private const string DeliveryId = "delivery-alpha";
    private static readonly string DeliveryActorId = WorkflowRunDeliveryActorIds.FromDeliveryId(DeliveryId);
    private const string WorkflowActorId = "workflow-actor-2675";
    private const string WorkflowCommandId = "workflow-command-2675";
    private const string AgentKey = "nyxid-agent-key-2675";
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task ReserveBeforeStartTerminal_ShouldUseVerifiedNotificationAsAcceptedIdentityAndDeliver()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler);

        await context.Agent.HandleEventAsync(Envelope(Reserve(context.TimeProvider), "registration-port"));
        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));

        AssertDelivered(context.Agent, handler, "completed output");

        await context.Agent.HandleEventAsync(Envelope(Start(), "registration-port"));

        AssertDelivered(context.Agent, handler, "completed output");
    }

    [Fact]
    public async Task StartBeforeTerminal_ShouldDeliverWhenNotificationArrives()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler);
        await ReserveAndStartAsync(context);

        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));

        AssertDelivered(context.Agent, handler, "completed output");
        context.Scheduler.PurgedActorIds.Should().ContainSingle().Which.Should().Be(DeliveryActorId);
    }

    [Fact]
    public async Task CompletedTerminalWithoutOutput_ShouldDeliverExplicitNoResultMessage()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler);
        await ReserveAndStartAsync(context);
        var terminal = Terminal(context.TimeProvider);
        terminal.Output = "   ";

        await context.Agent.HandleEventAsync(Envelope(terminal, WorkflowActorId));

        AssertDelivered(context.Agent, handler, "Workflow run completed without a result to display.");
    }

    [Fact]
    public async Task TerminalBeforeReserve_ShouldSurviveUntilReservationAndStart()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler);

        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));
        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Unspecified);
        context.Agent.State.PendingTerminalNotification.Should().NotBeNull();

        await context.Agent.HandleEventAsync(Envelope(Reserve(context.TimeProvider), "registration-port"));

        AssertDelivered(context.Agent, handler, "completed output");
    }

    [Fact]
    public async Task TerminalBeforeReserve_WithWrongCommand_ShouldBeDiscardedAndWaitForMatchingTerminal()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler);
        var stale = Terminal(context.TimeProvider);
        stale.WorkflowCommandId = "stale-command";

        await context.Agent.HandleEventAsync(Envelope(stale, WorkflowActorId));
        await context.Agent.HandleEventAsync(Envelope(Reserve(context.TimeProvider), "registration-port"));
        await context.Agent.HandleEventAsync(Envelope(Start(), "registration-port"));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Started);
        context.Agent.State.PendingTerminalNotification.Should().BeNull();
        handler.Requests.Should().BeEmpty();

        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));
        AssertDelivered(context.Agent, handler, "completed output");
    }

    [Fact]
    public async Task IdentityAndCommandMismatches_ShouldNeverDeliver()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler);
        await ReserveAndStartAsync(context);

        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), "attacker-actor"));

        var wrongActor = Terminal(context.TimeProvider);
        wrongActor.WorkflowActorId = "other-workflow-actor";
        await context.Agent.HandleEventAsync(Envelope(wrongActor, "other-workflow-actor"));

        var wrongCommand = Terminal(context.TimeProvider);
        wrongCommand.WorkflowCommandId = "other-workflow-command";
        await context.Agent.HandleEventAsync(Envelope(wrongCommand, WorkflowActorId));

        var wrongDelivery = Terminal(context.TimeProvider);
        wrongDelivery.DeliveryId = "other-delivery";
        await context.Agent.HandleEventAsync(Envelope(wrongDelivery, WorkflowActorId));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Started);
        context.Agent.State.PendingTerminalNotification.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DuplicateOrConflictingTerminalStatus_ShouldNotRepeatOutboundDelivery()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler);
        await ReserveAndStartAsync(context);
        var failed = Terminal(context.TimeProvider, WorkflowRunTerminalStatus.Failed);

        await context.Agent.HandleEventAsync(Envelope(failed, WorkflowActorId));
        await context.Agent.HandleEventAsync(Envelope(failed.Clone(), WorkflowActorId));
        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        context.Agent.State.TerminalOutcome.Should().Be(WorkflowRunTerminalStatus.Failed);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Body.Should().Contain("Workflow failed (workflow_run_error): execution failed");
    }

    [Fact]
    public async Task ToolApprovalNotification_ShouldDeliverTypedInteractiveCardExactlyOnce()
    {
        var context = await CreateContextAsync(new RecordingJsonHandler());
        await ReserveAndStartAsync(context);
        var notification = ToolApproval(context.TimeProvider);

        await context.Agent.HandleEventAsync(Envelope(notification, WorkflowActorId));
        await context.Agent.HandleEventAsync(Envelope(notification.Clone(), WorkflowActorId));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Started);
        context.Agent.State.PendingToolApprovalNotification.Should().BeNull();
        context.Agent.State.DeliveredToolApprovalRequestIds.Should().ContainSingle()
            .Which.Should().Be("approval-request-2675");
        var dispatch = context.InteractiveDispatcher.Requests.Should().ContainSingle().Subject;
        dispatch.Channel.Value.Should().Be("lark");
        dispatch.MessageId.Should().Be("reply-message-2675");
        dispatch.RelayToken.Should().Be(AgentKey);
        var card = dispatch.Intent.Cards.Should().ContainSingle().Subject;
        card.Title.Should().Be("Workflow Tool Approval Required");
        card.Actions.Should().HaveCount(2);
        card.Actions.Should().OnlyContain(action => action.Kind == ActionElementKind.FormSubmit);
        AssertApprovalAction(card.Actions[0], approved: true);
        AssertApprovalAction(card.Actions[1], approved: false);
    }

    [Fact]
    public async Task ToolApprovalNotification_FromMismatchedPublisher_ShouldNotDeliver()
    {
        var context = await CreateContextAsync(new RecordingJsonHandler());
        await ReserveAndStartAsync(context);

        await context.Agent.HandleEventAsync(Envelope(ToolApproval(context.TimeProvider), "attacker-workflow"));

        context.Agent.State.PendingToolApprovalNotification.Should().BeNull();
        context.Agent.State.DeliveredToolApprovalRequestIds.Should().BeEmpty();
        context.InteractiveDispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingToolApprovalDelivery_ShouldRecoverOnActivation()
    {
        var eventStore = new InMemoryEventStore();
        var timeProvider = new FakeTimeProvider(Now);
        var scheduler = new RecordingCallbackScheduler();
        var failingDispatcher = new RecordingInteractiveReplyDispatcher();
        failingDispatcher.Results.Enqueue(new InteractiveReplyDispatchResult(
            false,
            null,
            null,
            ComposeCapability.Exact,
            false,
            "relay unavailable"));
        var first = await CreateContextAsync(
            new RecordingJsonHandler(),
            eventStore: eventStore,
            timeProvider: timeProvider,
            scheduler: scheduler,
            interactiveDispatcher: failingDispatcher);
        await ReserveAndStartAsync(first);

        await first.Agent.HandleEventAsync(Envelope(ToolApproval(timeProvider), WorkflowActorId));

        first.Agent.State.PendingToolApprovalNotification.Should().NotBeNull();
        first.Agent.State.ToolApprovalDeliveryAttempt.Should().Be(1);
        scheduler.Timeouts.Should().Contain(x =>
            x.TriggerEnvelope.Payload.Is(WorkflowRunDeliveryToolApprovalNotificationRetryReached.Descriptor));

        var recoveredDispatcher = new RecordingInteractiveReplyDispatcher();
        var recovered = await CreateContextAsync(
            new RecordingJsonHandler(),
            eventStore: eventStore,
            timeProvider: timeProvider,
            scheduler: scheduler,
            interactiveDispatcher: recoveredDispatcher);

        recovered.Agent.State.PendingToolApprovalNotification.Should().BeNull();
        recovered.Agent.State.DeliveredToolApprovalRequestIds.Should().Contain("approval-request-2675");
        recoveredDispatcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task TerminalNotification_ShouldSupersedePendingToolApprovalRetry()
    {
        var handler = new RecordingJsonHandler();
        var dispatcher = new RecordingInteractiveReplyDispatcher();
        dispatcher.Results.Enqueue(new InteractiveReplyDispatchResult(
            false,
            null,
            null,
            ComposeCapability.Exact,
            false,
            "relay unavailable"));
        var context = await CreateContextAsync(handler, interactiveDispatcher: dispatcher);
        await ReserveAndStartAsync(context);
        await context.Agent.HandleEventAsync(Envelope(ToolApproval(context.TimeProvider), WorkflowActorId));
        var retry = context.Scheduler.Timeouts
            .Single(x => x.TriggerEnvelope.Payload.Is(WorkflowRunDeliveryToolApprovalNotificationRetryReached.Descriptor))
            .TriggerEnvelope.Clone();

        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));
        await context.Agent.HandleEventAsync(retry);

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        context.Agent.State.PendingToolApprovalNotification.Should().BeNull();
        dispatcher.Requests.Should().ContainSingle();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolApprovalNotification_WhenChannelFallsBackToText_ShouldFailWithoutRetry()
    {
        var dispatcher = new RecordingInteractiveReplyDispatcher();
        dispatcher.Results.Enqueue(new InteractiveReplyDispatchResult(
            true,
            "approval-reply-2675",
            "approval-platform-2675",
            ComposeCapability.Unsupported,
            true,
            "interactive composer unavailable"));
        var context = await CreateContextAsync(
            new RecordingJsonHandler(),
            interactiveDispatcher: dispatcher);
        await ReserveAndStartAsync(context);

        await context.Agent.HandleEventAsync(Envelope(ToolApproval(context.TimeProvider), WorkflowActorId));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        context.Agent.State.PendingToolApprovalNotification.Should().BeNull();
        context.Agent.State.ErrorCode.Should().Be("workflow_tool_approval_interactive_delivery_unsupported");
        context.Agent.State.ErrorSummary.Should().Be(
            "The channel did not accept an interactive workflow approval card.");
        context.Scheduler.Timeouts.Should().NotContain(x =>
            x.TriggerEnvelope.Payload.Is(WorkflowRunDeliveryToolApprovalNotificationRetryReached.Descriptor));
        dispatcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SameTerminalDuplicate_WhenPendingAttemptWasInterrupted_ShouldResumeDelivery()
    {
        var handler = new RecordingJsonHandler { CancelNextRequest = true };
        var context = await CreateContextAsync(handler);
        await ReserveAndStartAsync(context);
        var terminal = Terminal(context.TimeProvider);

        var firstAttempt = () => context.Agent.HandleEventAsync(Envelope(terminal, WorkflowActorId));
        await firstAttempt.Should().ThrowAsync<OperationCanceledException>();
        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Started);
        context.Agent.State.PendingTerminalNotification.Should().NotBeNull();

        await context.Agent.HandleEventAsync(Envelope(terminal.Clone(), WorkflowActorId));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        context.Agent.State.PendingTerminalNotification.Should().BeNull();
        handler.Requests.Should().HaveCount(2);
        context.Scheduler.PurgedActorIds.Should().ContainSingle().Which.Should().Be(DeliveryActorId);
    }

    [Fact]
    public async Task PendingTerminal_ShouldRetryOnActivationWithoutProjectionState()
    {
        var eventStore = new InMemoryEventStore();
        var timeProvider = new FakeTimeProvider(Now);
        var scheduler = new RecordingCallbackScheduler();
        var first = await CreateContextAsync(
            new RecordingJsonHandler(),
            eventStore: eventStore,
            timeProvider: timeProvider,
            scheduler: scheduler);
        await ReserveAndStartAsync(first);
        var buffered = new WorkflowRunDeliveryTerminalNotificationBufferedEvent
        {
            DeliveryId = DeliveryId,
            WorkflowCommandId = WorkflowCommandId,
            PublisherActorId = WorkflowActorId,
            Notification = Terminal(timeProvider),
            BufferedAtUnixMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };
        await eventStore.AppendAsync(
            DeliveryActorId,
            [new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
                Version = 3,
                EventType = WorkflowRunDeliveryTerminalNotificationBufferedEvent.Descriptor.FullName,
                EventData = Any.Pack(buffered),
                AgentId = DeliveryActorId,
            }],
            expectedVersion: 2);

        var recoveredHandler = new RecordingJsonHandler();
        var recovered = await CreateContextAsync(
            recoveredHandler,
            eventStore: eventStore,
            timeProvider: timeProvider,
            scheduler: scheduler);

        AssertDelivered(recovered.Agent, recoveredHandler, "completed output");
    }

    [Fact]
    public async Task MissingCredentialResolution_ShouldFailClosedWithoutHttp()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler, new StaticCredentialResolver(null));
        await ReserveAndStartAsync(context);

        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        context.Agent.State.ErrorCode.Should().Be("credential_handle_missing");
        handler.Requests.Should().BeEmpty();
        context.Scheduler.PurgedActorIds.Should().ContainSingle().Which.Should().Be(DeliveryActorId);
    }

    [Fact]
    public async Task CredentialResolverFailure_ShouldFailClosedWithoutHttp()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(
            handler,
            new StaticCredentialResolver(exception: new InvalidOperationException("vault unavailable")));
        await ReserveAndStartAsync(context);

        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        context.Agent.State.ErrorCode.Should().Be("resolver_unavailable");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedReservationCredential_ShouldPersistProductFailure()
    {
        var context = await CreateContextAsync(new RecordingJsonHandler());
        var reserve = Reserve(context.TimeProvider);
        reserve.WorkflowResultDeliveryCredential = new ChannelWorkflowResultDeliveryCredential();

        await context.Agent.HandleEventAsync(Envelope(reserve, "registration-port"));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        context.Agent.State.ErrorCode.Should().Be("channel_workflow_delivery_unavailable");
        context.Agent.State.ErrorSummary.ToLowerInvariant().Should().NotContain("credential");
    }

    [Fact]
    public async Task ReservationExpiry_ShouldUseDurableSelfTimeoutAndRejectStaleCallbacks()
    {
        var context = await CreateContextAsync(new RecordingJsonHandler());
        await context.Agent.HandleEventAsync(Envelope(Reserve(context.TimeProvider), "registration-port"));
        var scheduled = context.Scheduler.Timeouts.Should().ContainSingle().Which;
        var expiry = scheduled.TriggerEnvelope.Payload.Unpack<WorkflowRunDeliveryReservationExpiryReached>();

        await context.Agent.HandleEventAsync(scheduled.TriggerEnvelope.Clone());
        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Reserved);

        context.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var stale = expiry.Clone();
        stale.WorkflowCommandId = "stale-command";
        await context.Agent.HandleEventAsync(SelfEnvelope(stale));
        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Reserved);

        await context.Agent.HandleEventAsync(SelfEnvelope(expiry));
        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Failed);
        context.Agent.State.ErrorCode.Should().Be("workflow_run_delivery_reservation_expired");
        context.Scheduler.PurgedActorIds.Should().ContainSingle().Which.Should().Be(DeliveryActorId);
    }

    [Fact]
    public async Task LongReservationExpiry_ShouldScheduleInRuntimeSafeSegments()
    {
        var context = await CreateContextAsync(new RecordingJsonHandler());
        var reserve = Reserve(context.TimeProvider);
        reserve.ExpiresAtUnixMs = context.TimeProvider.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds();

        await context.Agent.HandleEventAsync(Envelope(reserve, "registration-port"));

        var first = context.Scheduler.Timeouts.Should().ContainSingle().Which;
        first.DueTime.TotalMilliseconds.Should().BeLessThanOrEqualTo(int.MaxValue);
        first.DueTime.Should().BeLessThan(TimeSpan.FromDays(30));

        context.TimeProvider.Advance(first.DueTime + TimeSpan.FromMilliseconds(1));
        await context.Agent.HandleEventAsync(first.TriggerEnvelope.Clone());

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Reserved);
        context.Scheduler.Timeouts.Should().HaveCount(2);
        context.Scheduler.Timeouts[1].DueTime.Should().BePositive();
        context.Scheduler.Timeouts[1].DueTime.TotalMilliseconds.Should().BeLessThanOrEqualTo(int.MaxValue);
    }

    [Fact]
    public async Task AbandonAndLateTimeoutOrTerminal_ShouldBeIdempotent()
    {
        var handler = new RecordingJsonHandler();
        var context = await CreateContextAsync(handler);
        await context.Agent.HandleEventAsync(Envelope(Reserve(context.TimeProvider), "registration-port"));
        var timeout = context.Scheduler.Timeouts.Should().ContainSingle().Which.TriggerEnvelope.Clone();

        await context.Agent.HandleEventAsync(Envelope(new WorkflowRunDeliveryAbandonRequested
        {
            DeliveryId = DeliveryId,
            WorkflowCommandId = WorkflowCommandId,
            Reason = "workflow command rejected",
        }, "registration-port"));
        context.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        await context.Agent.HandleEventAsync(timeout);
        await context.Agent.HandleEventAsync(Envelope(Terminal(context.TimeProvider), WorkflowActorId));

        context.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Abandoned);
        handler.Requests.Should().BeEmpty();
        context.Scheduler.PurgedActorIds.Should().ContainSingle().Which.Should().Be(DeliveryActorId);
    }

    [Fact]
    public async Task TerminalActivation_ShouldRetryCallbackPurgeAfterPriorFailure()
    {
        var eventStore = new InMemoryEventStore();
        var timeProvider = new FakeTimeProvider(Now);
        var scheduler = new RecordingCallbackScheduler
        {
            PurgeFailure = new InvalidOperationException("scheduler unavailable"),
        };
        var first = await CreateContextAsync(
            new RecordingJsonHandler(),
            eventStore: eventStore,
            timeProvider: timeProvider,
            scheduler: scheduler);
        await ReserveAndStartAsync(first);
        await first.Agent.HandleEventAsync(Envelope(Terminal(timeProvider), WorkflowActorId));

        first.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        scheduler.PurgeAttempts.Should().ContainSingle().Which.Should().Be(DeliveryActorId);
        scheduler.PurgedActorIds.Should().BeEmpty();

        scheduler.PurgeFailure = null;
        var recovered = await CreateContextAsync(
            new RecordingJsonHandler(),
            eventStore: eventStore,
            timeProvider: timeProvider,
            scheduler: scheduler);

        recovered.Agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        scheduler.PurgeAttempts.Should().HaveCount(2);
        scheduler.PurgedActorIds.Should().ContainSingle().Which.Should().Be(DeliveryActorId);
    }

    private static async Task ReserveAndStartAsync(TestContext context)
    {
        await context.Agent.HandleEventAsync(Envelope(Reserve(context.TimeProvider), "registration-port"));
        await context.Agent.HandleEventAsync(Envelope(Start(), "registration-port"));
    }

    private static void AssertDelivered(
        WorkflowRunDeliveryGAgent agent,
        RecordingJsonHandler handler,
        string text)
    {
        agent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        agent.State.PendingTerminalNotification.Should().BeNull();
        agent.State.TerminalOutcome.Should().Be(WorkflowRunTerminalStatus.Completed);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        handler.Requests[0].Authorization.Should().Be($"Bearer {AgentKey}");
        handler.Requests[0].Body.Should().Contain($"\"text\":\"{text}\"");
    }

    private static async Task<TestContext> CreateContextAsync(
        HttpMessageHandler handler,
        IWorkflowResultDeliveryCredentialResolver? credentialResolver = null,
        InMemoryEventStore? eventStore = null,
        FakeTimeProvider? timeProvider = null,
        RecordingCallbackScheduler? scheduler = null,
        RecordingInteractiveReplyDispatcher? interactiveDispatcher = null)
    {
        eventStore ??= new InMemoryEventStore();
        timeProvider ??= new FakeTimeProvider(Now);
        scheduler ??= new RecordingCallbackScheduler();
        interactiveDispatcher ??= new RecordingInteractiveReplyDispatcher();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new WorkflowRunDeliveryGAgent(
            CreateOutboundPort(handler),
            interactiveDispatcher,
            credentialResolver ?? new StaticCredentialResolver(AgentKey),
            scheduler,
            NullLogger<WorkflowRunDeliveryGAgent>.Instance,
            timeProvider)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunDeliveryGAgentState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [DeliveryActorId]);
        await agent.ActivateAsync();
        return new TestContext(agent, timeProvider, scheduler, interactiveDispatcher);
    }

    private static WorkflowRunDeliveryReserveRequested Reserve(FakeTimeProvider timeProvider) => new()
    {
        DeliveryId = DeliveryId,
        ExpectedWorkflowCommandId = WorkflowCommandId,
        ChannelPlatform = "lark",
        ReplyMessageId = "reply-message-2675",
        PlatformMessageId = "platform-message-2675",
        RegistrationScopeId = "scope-2675",
        WorkflowResultDeliveryCredential = Credential(),
        BotRegistrationId = "bot-registration-2675",
        ExpiresAtUnixMs = timeProvider.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds(),
    };

    private static WorkflowRunDeliveryStartRequested Start() => new()
    {
        DeliveryId = DeliveryId,
        WorkflowActorId = WorkflowActorId,
        WorkflowRunId = "workflow-run-2675",
        WorkflowCommandId = WorkflowCommandId,
        WorkflowCorrelationId = "workflow-correlation-2675",
        StreamTopic = "aevatar://actors/workflow-actor-2675/runs/workflow-command-2675",
    };

    private static WorkflowRunTerminalNotification Terminal(
        FakeTimeProvider timeProvider,
        WorkflowRunTerminalStatus status = WorkflowRunTerminalStatus.Completed) =>
        new()
        {
            DeliveryId = DeliveryId,
            WorkflowActorId = WorkflowActorId,
            WorkflowRunId = "workflow-run-2675",
            WorkflowCommandId = WorkflowCommandId,
            WorkflowCorrelationId = "workflow-correlation-2675",
            Status = status,
            Output = status == WorkflowRunTerminalStatus.Completed ? "completed output" : string.Empty,
            Error = status == WorkflowRunTerminalStatus.Completed ? string.Empty : "execution failed",
            TerminalAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
        };

    private static WorkflowRunToolApprovalNotification ToolApproval(FakeTimeProvider timeProvider) =>
        new()
        {
            DeliveryId = DeliveryId,
            WorkflowActorId = WorkflowActorId,
            WorkflowRunId = "workflow-run-2675",
            WorkflowCommandId = WorkflowCommandId,
            WorkflowCorrelationId = "workflow-correlation-2675",
            StepId = "tool-step-2675",
            ExecutionId = "execution-2675",
            ToolName = "lark_contact_batch_resolution",
            ToolCallId = "tool-call-2675",
            ApprovalRequestId = "approval-request-2675",
            Prompt = "Approve contact batch resolution?",
            RequestedAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
        };

    private static void AssertApprovalAction(ActionElement action, bool approved)
    {
        action.WorkflowResume.Should().NotBeNull();
        action.WorkflowResume.ActorId.Should().Be(WorkflowActorId);
        action.WorkflowResume.RunId.Should().Be("workflow-run-2675");
        action.WorkflowResume.StepId.Should().Be("tool-step-2675");
        action.WorkflowResume.HasApproved.Should().BeTrue();
        action.WorkflowResume.Approved.Should().Be(approved);
        action.WorkflowResume.ToolApproval.Should().NotBeNull();
        action.WorkflowResume.ToolApproval.ExecutionId.Should().Be("execution-2675");
        action.WorkflowResume.ToolApproval.ToolCallId.Should().Be("tool-call-2675");
        action.WorkflowResume.ToolApproval.ApprovalRequestId.Should().Be("approval-request-2675");
    }

    private static EventEnvelope Envelope<T>(T payload, string publisherActorId)
        where T : IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(Now),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, DeliveryActorId),
        };

    private static EventEnvelope SelfEnvelope<T>(T payload)
        where T : IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(Now),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(DeliveryActorId, TopologyAudience.Self),
        };

    private static ChannelWorkflowResultDeliveryCredential Credential() => new()
    {
        SecretReference = new SecretReference
        {
            Ref = "sec-delivery-2675",
            Purpose = "channel.workflow-result-delivery-agent-key",
            OwnerScopeKey = "scope-2675",
        },
        SubjectId = "nyx-key-2675",
    };

    private static NyxIdRelayOutboundPort CreateOutboundPort(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") },
            NullLogger<NyxIdApiClient>.Instance);
        return new NyxIdRelayOutboundPort(
            client,
            NullLogger<NyxIdRelayOutboundPort>.Instance,
            [new PlainTextComposer("lark")]);
    }

    private sealed record TestContext(
        WorkflowRunDeliveryGAgent Agent,
        FakeTimeProvider TimeProvider,
        RecordingCallbackScheduler Scheduler,
        RecordingInteractiveReplyDispatcher InteractiveDispatcher);

    private sealed class RecordingInteractiveReplyDispatcher : IInteractiveReplyDispatcher
    {
        public List<(ChannelId Channel, string MessageId, string RelayToken, MessageContent Intent)> Requests { get; } = [];
        public Queue<InteractiveReplyDispatchResult> Results { get; } = [];

        public Task<InteractiveReplyDispatchResult> DispatchAsync(
            ChannelId channel,
            string messageId,
            string relayToken,
            MessageContent intent,
            ComposeContext context,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((channel, messageId, relayToken, intent.Clone()));
            return Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : new InteractiveReplyDispatchResult(
                    true,
                    "approval-reply-2675",
                    "approval-platform-2675",
                    ComposeCapability.Exact,
                    false,
                    null));
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];
        public List<string> PurgeAttempts { get; } = [];
        public List<string> PurgedActorIds { get; } = [];
        public Exception? PurgeFailure { get; set; }

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            PurgeAttempts.Add(actorId);
            if (PurgeFailure is not null)
                return Task.FromException(PurgeFailure);

            PurgedActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticCredentialResolver : IWorkflowResultDeliveryCredentialResolver
    {
        private readonly string? _agentKey;
        private readonly Exception? _exception;

        public StaticCredentialResolver(string? agentKey = null, Exception? exception = null)
        {
            _agentKey = agentKey;
            _exception = exception;
        }

        public Task<string?> ResolveAsync(
            ChannelWorkflowResultDeliveryCredential credential,
            CancellationToken ct = default) =>
            _exception is null
                ? Task.FromResult(_agentKey)
                : Task.FromException<string?>(_exception);
    }

    private sealed class RecordingJsonHandler : HttpMessageHandler
    {
        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];
        public bool CancelNextRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (CancelNextRequest)
            {
                CancelNextRequest = false;
                throw new OperationCanceledException("relay request interrupted");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"message_id":"reply-2675","platform_message_id":"platform-2675"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class PlainTextComposer(string platform) : IMessageComposer<PlainTextPayload>
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);

        public PlainTextPayload Compose(MessageContent intent, ComposeContext context) =>
            new(intent.Text ?? string.Empty);

        object IMessageComposer.Compose(MessageContent intent, ComposeContext context) =>
            Compose(intent, context);

        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) =>
            ComposeCapability.Exact;
    }

    private sealed record PlainTextPayload(string PlainText) : IPlainTextComposedMessage;
}
