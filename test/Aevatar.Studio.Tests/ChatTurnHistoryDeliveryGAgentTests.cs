using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class ChatTurnHistoryDeliveryGAgentTests
{
    private const string DeliveryActorId = "chat-history-delivery:actor-address-alpha";
    private const string DeliveryId = "chat-history-delivery-business-alpha";
    private const string WorkflowActorId = "workflow-actor";
    private const string WorkflowCommandId = "workflow-command";
    private const string SourceActorId = "source-actor";
    private const string SourceCommandId = "source-command";

    [Fact]
    public void SourceIdentityFields_ShouldRetainWireNumbersAndMessageTypeUrls()
    {
        var bytes = HandcraftedSourceIdentityBytes();

        var state = ChatTurnHistoryDeliveryState.Parser.ParseFrom(bytes);
        var reserve = ChatTurnHistoryDeliveryReserveRequested.Parser.ParseFrom(bytes);
        var reserved = ChatTurnHistoryDeliveryReservedEvent.Parser.ParseFrom(bytes);

        state.SourceActorId.Should().Be(SourceActorId);
        state.SourceCommandId.Should().Be(SourceCommandId);
        state.SourceCorrelationId.Should().Be("source-correlation");
        reserve.SourceActorId.Should().Be(SourceActorId);
        reserve.SourceCommandId.Should().Be(SourceCommandId);
        reserve.SourceCorrelationId.Should().Be("source-correlation");
        reserved.SourceActorId.Should().Be(SourceActorId);
        reserved.SourceCommandId.Should().Be(SourceCommandId);
        reserved.SourceCorrelationId.Should().Be("source-correlation");
        ChatTurnHistoryDeliveryState.Descriptor.FindFieldByName("source_actor_id").FieldNumber.Should().Be(6);
        ChatTurnHistoryDeliveryState.Descriptor.FindFieldByName("source_command_id").FieldNumber.Should().Be(7);
        ChatTurnHistoryDeliveryState.Descriptor.FindFieldByName("source_correlation_id").FieldNumber.Should().Be(8);
        Any.Pack(state).TypeUrl.Should().Be(
            "type.googleapis.com/aevatar.gagents.chathistory.ChatTurnHistoryDeliveryState");
        Any.Pack(reserve).TypeUrl.Should().Be(
            "type.googleapis.com/aevatar.gagents.chathistory.ChatTurnHistoryDeliveryReserveRequested");
    }

    [Theory]
    [InlineData(ChatTurnTerminalStatus.Completed, "safe terminal text", "", "safe terminal text", "")]
    [InlineData(ChatTurnTerminalStatus.Failed, "safe terminal text", "source_failed", "", "source_failed: safe terminal text")]
    [InlineData(ChatTurnTerminalStatus.Stopped, "safe terminal text", "source_stopped", "", "source_stopped")]
    [InlineData(ChatTurnTerminalStatus.Blocked, "Connect a private source to continue.", "source_blocked", "Connect a private source to continue.", "")]
    [InlineData(ChatTurnTerminalStatus.OutcomeUncertain, "The outcome could not be confirmed.", "SESSION_OUTCOME_UNCERTAIN", "The outcome could not be confirmed.", "SESSION_OUTCOME_UNCERTAIN: The outcome could not be confirmed.")]
    public async Task SourceTerminalNotification_ShouldMapClosedStatusToExactAppend(
        ChatTurnTerminalStatus status,
        string text,
        string errorCode,
        string expectedAssistantText,
        string expectedSanitizedError)
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(runtime, dispatch);
        await agent.HandleEventAsync(Envelope(SourceReserve(), "chat-history-command-port"));

        await agent.HandleEventAsync(Envelope(
            SourceTerminal(status, text, errorCode),
            SourceActorId));

        var append = dispatch.Calls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<AppendChatTurnCommand>();
        append.Turn.TerminalStatus.Should().Be(status);
        append.Turn.AssistantText.Should().Be(expectedAssistantText);
        append.Turn.SanitizedError.Should().Be(expectedSanitizedError);
    }

    [Fact]
    public async Task Reserve_WhenExactlyRetried_ShouldNoOp_ButConflictShouldFailClosed()
    {
        var agent = await CreateAgentAsync(new RecordingActorRuntime(), new RecordingActorDispatchPort());
        var reserve = SourceReserve();
        await agent.HandleEventAsync(Envelope(reserve, "chat-history-command-port"));

        await agent.HandleEventAsync(Envelope(reserve.Clone(), "chat-history-command-port"));
        var conflict = reserve.Clone();
        conflict.RequestFingerprint = "changed-fingerprint";
        var act = () => agent.HandleEventAsync(Envelope(conflict, "chat-history-command-port"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reservation conflicts*");
        agent.State.RequestFingerprint.Should().Be("fingerprint-original");
        agent.State.Status.Should().Be(ChatTurnHistoryDeliveryStatus.Reserved);
    }

    [Fact]
    public async Task Reserve_WhenCommittedReservationIsReusedWithMalformedPayload_ShouldPreserveCommittedState()
    {
        var agent = await CreateAgentAsync(new RecordingActorRuntime(), new RecordingActorDispatchPort());
        var reserve = SourceReserve();
        await agent.HandleEventAsync(Envelope(reserve, "chat-history-command-port"));

        var malformed = reserve.Clone();
        malformed.UserText = string.Empty;
        var act = () => agent.HandleEventAsync(Envelope(malformed, "chat-history-command-port"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reservation conflicts*");
        agent.State.Status.Should().Be(ChatTurnHistoryDeliveryStatus.Reserved);
        agent.State.RequestFingerprint.Should().Be("fingerprint-original");
        agent.State.ErrorCode.Should().BeEmpty();
        agent.State.ErrorSummary.Should().BeEmpty();
    }

    [Fact]
    public async Task SourceTerminal_WhenExactlyRetried_ShouldNoOp_ButConflictShouldFailClosed()
    {
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(new RecordingActorRuntime(), dispatch);
        await agent.HandleEventAsync(Envelope(SourceReserve(), "chat-history-command-port"));
        var terminal = SourceTerminal(
            ChatTurnTerminalStatus.Completed,
            "safe terminal text",
            string.Empty);
        await agent.HandleEventAsync(Envelope(terminal, SourceActorId));

        await agent.HandleEventAsync(Envelope(terminal.Clone(), SourceActorId));
        var conflict = terminal.Clone();
        conflict.Text = "different terminal text";
        var act = () => agent.HandleEventAsync(Envelope(conflict, SourceActorId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal conflicts*");
        dispatch.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(ChatTurnTerminalStatus.Completed)]
    [InlineData(ChatTurnTerminalStatus.Failed)]
    public async Task SourceTerminal_ShouldRedispatchStableDeliveryForAllowedUncertainReconciliation(
        ChatTurnTerminalStatus reconciledStatus)
    {
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(new RecordingActorRuntime(), dispatch);
        await agent.HandleEventAsync(Envelope(SourceReserve(), "chat-history-command-port"));
        await agent.HandleEventAsync(Envelope(SourceTerminal(
            ChatTurnTerminalStatus.OutcomeUncertain,
            "The outcome could not be confirmed.",
            "SESSION_OUTCOME_UNCERTAIN"), SourceActorId));
        await agent.HandleEventAsync(Envelope(AppendResult(true), "chat-conversation"));
        var reconciled = SourceTerminal(
            reconciledStatus,
            reconciledStatus == ChatTurnTerminalStatus.Completed ? "confirmed result" : "confirmed failure",
            reconciledStatus == ChatTurnTerminalStatus.Failed ? "CONFIRMED_FAILURE" : string.Empty);
        reconciled.ObservedAtUnixMs++;

        await agent.HandleEventAsync(Envelope(reconciled, SourceActorId));

        dispatch.Calls.Should().HaveCount(2);
        var first = dispatch.Calls[0].Envelope;
        var second = dispatch.Calls[1].Envelope;
        first.Payload.Unpack<AppendChatTurnCommand>().Turn.TerminalStatus.Should()
            .Be(ChatTurnTerminalStatus.OutcomeUncertain);
        second.Payload.Unpack<AppendChatTurnCommand>().Turn.TerminalStatus.Should().Be(reconciledStatus);
        first.EnsureRuntime().EnsureDeliveryIdentity().OperationId.Should()
            .Be($"chat-history-append:{DeliveryId}:1");
        second.EnsureRuntime().EnsureDeliveryIdentity().OperationId.Should()
            .Be($"chat-history-append:{DeliveryId}:2");
        agent.State.DeliveryId.Should().Be(DeliveryId);
        agent.State.AppendAttempt.Should().Be(2);
        agent.State.Status.Should().Be(ChatTurnHistoryDeliveryStatus.AppendDispatched);
        agent.State.TerminalStatus.Should().Be(reconciledStatus);
    }

    [Fact]
    public async Task DeliveryAgent_ShouldExposeCommittedStateProjectionContract()
    {
        var agent = await CreateAgentAsync(new RecordingActorRuntime(), new RecordingActorDispatchPort());

        typeof(IProjectedActor).IsAssignableFrom(agent.GetType()).Should().BeTrue();
    }

    [Fact]
    public async Task TerminalNotification_ShouldAppendFromWorkflowOutboxWithoutProjectionAttachment()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(runtime, dispatch);

        await agent.HandleEventAsync(Envelope(Reserve(createConversationIfMissing: true), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Bind(), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Terminal(), WorkflowActorId));

        runtime.CreateCalls.Should().ContainSingle()
            .Which.Should().Be(ChatHistoryActorIds.Conversation("scope-a", "conversation-a"));
        dispatch.Calls.Should().ContainSingle();
        var append = dispatch.Calls.Single().Envelope.Payload.Unpack<AppendChatTurnCommand>();
        append.ScopeId.Should().Be("scope-a");
        append.ConversationId.Should().Be("conversation-a");
        append.DeliveryActorId.Should().Be(DeliveryActorId);
        append.Turn.TurnId.Should().Be("turn-a");
        append.Turn.UserText.Should().Be("original user text");
        append.Turn.AssistantText.Should().Be("terminal output");
        append.Turn.TerminalStatus.Should().Be(ChatTurnTerminalStatus.Completed);
    }

    [Fact]
    public async Task TerminalNotification_ShouldNotCreateOrAppend_WhenContinueConversationIsMissing()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(runtime, dispatch);

        await agent.HandleEventAsync(Envelope(Reserve(createConversationIfMissing: false), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Bind(), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Terminal(), WorkflowActorId));

        runtime.CreateCalls.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, ChatTurnHistoryDeliveryStatus.AppendCommitted)]
    [InlineData(false, ChatTurnHistoryDeliveryStatus.AppendRejected)]
    public async Task Reserve_ShouldNotMutateDelivery_WhenAppendResultIsTerminal(
        bool appendAccepted,
        ChatTurnHistoryDeliveryStatus expectedStatus)
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(runtime, dispatch);

        await agent.HandleEventAsync(Envelope(Reserve(createConversationIfMissing: true), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Bind(), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Terminal(), WorkflowActorId));
        await agent.HandleEventAsync(Envelope(AppendResult(appendAccepted), "chat-conversation"));

        agent.State.Status.Should().Be(expectedStatus);

        var act = () => agent.HandleEventAsync(Envelope(
            Reserve(
                createConversationIfMissing: true,
                workflowActorId: "workflow-actor-retry",
                requestFingerprint: "fingerprint-retry"),
            "chat-history-terminal-delivery-port"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reservation conflicts*");

        agent.State.Status.Should().Be(expectedStatus);
        agent.State.SourceActorId.Should().Be(WorkflowActorId);
        agent.State.RequestFingerprint.Should().Be("fingerprint-original");
    }

    [Fact]
    public async Task SourceTerminal_ShouldAppendTheObservedOperationLedgerWithTheTurn()
    {
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(new RecordingActorRuntime(), dispatch);
        await agent.HandleEventAsync(Envelope(SourceReserve(), "chat-history-command-port"));
        var terminal = SourceTerminal(ChatTurnTerminalStatus.Completed, "done", string.Empty);
        terminal.Operations.Add(new ChatTurnOperation
        {
            OperationId = "op-model-1",
            Order = 1,
            Kind = ChatTurnOperationKind.Model,
            Title = "deepseek-v4-pro",
            Status = "done",
            TotalTokens = 512,
            OutputPreview = "plan",
            PreviewsTruncated = true,
            AvailableToolNames = { "github.get_issue", "nyxid.require_service" },
            ToolCatalogCaptured = true,
        });
        terminal.Operations.Add(new ChatTurnOperation
        {
            OperationId = "op-tool-1",
            Order = 2,
            Kind = ChatTurnOperationKind.Tool,
            Title = "service.reconnect",
            Status = "done",
        });

        await agent.HandleEventAsync(Envelope(terminal, SourceActorId));

        agent.State.TerminalOperations.Should().HaveCount(2);
        var append = dispatch.Calls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<AppendChatTurnCommand>();
        append.Turn.Operations.Select(operation => operation.OperationId)
            .Should().Equal("op-model-1", "op-tool-1");
        append.Turn.Operations[0].PreviewsTruncated.Should().BeTrue();
        append.Turn.Operations[0].TotalTokens.Should().Be(512);
        append.Turn.Operations[0].AvailableToolNames.Should()
            .Equal("github.get_issue", "nyxid.require_service");
        append.Turn.Operations[0].ToolCatalogCaptured.Should().BeTrue();
    }

    [Fact]
    public async Task SourceTerminal_WhenReconciledWithoutOperations_ShouldKeepTheObservedLedger()
    {
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(new RecordingActorRuntime(), dispatch);
        await agent.HandleEventAsync(Envelope(SourceReserve(), "chat-history-command-port"));
        var terminal = SourceTerminal(
            ChatTurnTerminalStatus.OutcomeUncertain,
            "The outcome could not be confirmed.",
            "SESSION_OUTCOME_UNCERTAIN");
        terminal.Operations.Add(new ChatTurnOperation
        {
            OperationId = "op-tool-1",
            Order = 1,
            Kind = ChatTurnOperationKind.Tool,
            Title = "service.reconnect",
            Status = "uncertain",
        });
        await agent.HandleEventAsync(Envelope(terminal, SourceActorId));
        await agent.HandleEventAsync(Envelope(AppendResult(accepted: true), "chat-conversation"));

        // A later reconciliation that carries no ledger must not erase the one the
        // source already reported.
        await agent.HandleEventAsync(Envelope(
            SourceTerminal(ChatTurnTerminalStatus.Completed, "done", string.Empty),
            SourceActorId));

        agent.State.TerminalOperations.Select(operation => operation.OperationId)
            .Should().Equal("op-tool-1");
        dispatch.Calls
            .Select(call => call.Envelope.Payload)
            .Where(payload => payload.Is(AppendChatTurnCommand.Descriptor))
            .Select(payload => payload.Unpack<AppendChatTurnCommand>())
            .Last()
            .Turn.Operations.Should().ContainSingle()
            .Which.OperationId.Should().Be("op-tool-1");
    }

    private static ChatTurnHistoryDeliveryReserveRequested Reserve(
        bool createConversationIfMissing,
        string workflowActorId = WorkflowActorId,
        string requestFingerprint = "fingerprint-original") => new()
    {
        DeliveryId = DeliveryId,
        ScopeId = "scope-a",
        ConversationId = "conversation-a",
        TurnId = "turn-a",
        UserText = "original user text",
        SourceActorId = workflowActorId,
        SourceCommandId = WorkflowCommandId,
        SourceCorrelationId = "workflow-correlation",
        CreateConversationIfMissing = createConversationIfMissing,
        RequestFingerprint = requestFingerprint,
    };

    private static ChatTurnHistoryDeliveryReserveRequested SourceReserve() => new()
    {
        DeliveryId = DeliveryId,
        ScopeId = "scope-a",
        ConversationId = "conversation-a",
        TurnId = "turn-a",
        UserText = "original user text",
        SourceActorId = SourceActorId,
        SourceCommandId = SourceCommandId,
        SourceCorrelationId = "source-correlation",
        CreateConversationIfMissing = true,
        RequestFingerprint = "fingerprint-original",
    };

    private static ChatTurnHistorySourceTerminalNotified SourceTerminal(
        ChatTurnTerminalStatus status,
        string text,
        string errorCode) => new()
        {
            DeliveryId = DeliveryId,
            SourceActorId = SourceActorId,
            SourceCommandId = SourceCommandId,
            Status = status,
            Text = text,
            ErrorCode = errorCode,
            ObservedAtUnixMs = DateTimeOffset.Parse("2026-07-16T00:00:00Z")
                .ToUnixTimeMilliseconds(),
        };

    private static byte[] HandcraftedSourceIdentityBytes()
    {
        using var stream = new MemoryStream();
        WriteLengthDelimited(stream, 50, SourceActorId);
        WriteLengthDelimited(stream, 58, SourceCommandId);
        WriteLengthDelimited(stream, 66, "source-correlation");
        return stream.ToArray();
    }

    private static void WriteLengthDelimited(Stream stream, byte tag, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        stream.WriteByte(tag);
        stream.WriteByte(checked((byte)bytes.Length));
        stream.Write(bytes);
    }

    private static ChatTurnHistoryDeliveryAcceptedBound Bind() => new()
    {
        DeliveryId = DeliveryId,
        SourceActorId = WorkflowActorId,
        SourceCommandId = WorkflowCommandId,
        SourceCorrelationId = "workflow-correlation",
    };

    private static WorkflowRunTerminalNotification Terminal() => new()
    {
        DeliveryId = DeliveryId,
        WorkflowActorId = WorkflowActorId,
        WorkflowRunId = "workflow-run",
        WorkflowCommandId = WorkflowCommandId,
        WorkflowCorrelationId = "workflow-correlation",
        Status = WorkflowRunTerminalStatus.Completed,
        Output = " terminal output ",
        TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-16T00:00:00Z")),
    };

    private static ChatTurnHistoryDeliveryAppendResultObserved AppendResult(bool accepted) => new()
    {
        DeliveryActorId = DeliveryActorId,
        ConversationId = "conversation-a",
        TurnId = "turn-a",
        Accepted = accepted,
        RejectionReason = accepted
            ? ChatTurnAppendRejectionReason.Unspecified
            : ChatTurnAppendRejectionReason.Conflict,
        ObservedAtUnixMs = DateTimeOffset.Parse("2026-07-16T00:00:01Z").ToUnixTimeMilliseconds(),
    };

    private static EventEnvelope Envelope(IMessage payload, string publisherActorId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, DeliveryActorId),
        };

    private static async Task<ChatTurnHistoryDeliveryGAgent> CreateAgentAsync(
        RecordingActorRuntime runtime,
        RecordingActorDispatchPort dispatch)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore, RecordingEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ChatTurnHistoryDeliveryGAgent(
            runtime,
            dispatch,
            NullLogger<ChatTurnHistoryDeliveryGAgent>.Instance)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChatTurnHistoryDeliveryState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [DeliveryActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            CreateCalls.Add(id ?? string.Empty);
            return Task.FromResult<IActor>(new RecordingActor(id ?? string.Empty));
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateAsync<NoopAgent>(id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var existing))
            {
                existing = [];
                _events[agentId] = existing;
            }

            if (existing.Count != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, existing.Count);

            var committed = new List<StateEvent>();
            foreach (var stateEvent in events)
            {
                var copy = stateEvent.Clone();
                copy.Version = existing.Count + 1;
                existing.Add(copy);
                committed.Add(copy.Clone());
            }

            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = existing.Count,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<StateEvent> result = _events.TryGetValue(agentId, out var events)
                ? events
                    .Where(e => fromVersion is null || e.Version >= fromVersion)
                    .Select(e => e.Clone())
                    .ToList()
                : [];
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_events.TryGetValue(agentId, out var events) ? (long)events.Count : 0);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var events))
                return Task.FromResult(0L);

            var deleted = events.RemoveAll(e => e.Version <= toVersion);
            return Task.FromResult((long)deleted);
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new NoopAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopAgent : IAgent
    {
        public string Id => "noop";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("noop");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
