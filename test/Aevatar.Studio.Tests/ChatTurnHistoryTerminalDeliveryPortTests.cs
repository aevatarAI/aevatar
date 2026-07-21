using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class ChatTurnHistoryTerminalDeliveryPortTests
{
    private const string DeliveryId = "chat-history-delivery-business-alpha";
    private const string WorkflowActorId = "workflow-actor-alpha";
    private const string WorkflowCommandId = "workflow-command-alpha";
    private const string WorkflowCorrelationId = "workflow-correlation-alpha";

    [Fact]
    public async Task ReserveAsync_ShouldGenerateConversationAndTurnForCreateIntent()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);

        var result = await port.ReserveAsync(ReservationRequest(WorkflowChatConversationIntent.Create()));

        result.Succeeded.Should().BeTrue();
        var reservation = result.Reservation;
        reservation.Should().NotBeNull();
        reservation!.DeliveryId.Should().Be(DeliveryId);
        reservation.DeliveryActorId.Should().StartWith("chat-history-delivery:");
        reservation.DeliveryActorId.Should().NotBe(DeliveryId);
        result.ChatContext.Should().NotBeNull();
        result.ChatContext!.ConversationId.Should().NotBeNullOrWhiteSpace();
        result.ChatContext.ConversationId.Should().NotBe("conversation-from-client");
        result.ChatContext.TurnId.Should().NotBeNullOrWhiteSpace();
        result.ChatContext.TurnId.Should().NotBe("turn-from-client");
        runtime.CreatedActors.Should().ContainSingle()
            .Which.Should().Be((typeof(ChatTurnHistoryDeliveryGAgent), reservation.DeliveryActorId));
        var call = dispatch.Calls.Should().ContainSingle().Which;
        call.ActorId.Should().Be(reservation.DeliveryActorId);
        var command = call.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryReserveRequested>();
        command.DeliveryId.Should().Be(DeliveryId);
        command.ConversationId.Should().Be(result.ChatContext.ConversationId);
        command.TurnId.Should().Be(result.ChatContext.TurnId);
        command.WorkflowActorId.Should().Be(WorkflowActorId);
        command.WorkflowCommandId.Should().Be(WorkflowCommandId);
        command.CreateConversationIfMissing.Should().BeTrue();
    }

    [Fact]
    public async Task ReserveAsync_ShouldReplayCreateIdentityFromRecoveryReadModel()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var recoveryReader = new RecordingChatCreateRecoveryReader();
        var createIdentity = new WorkflowChatCreateIdempotencyIdentity("create-alpha");
        var requestHash = createIdentity.BuildRequestHash("scope-alpha", "original user text", WorkflowActorId);
        recoveryReader.Seed(new ChatCreateRecoveryRecord(
            ScopeId: "scope-alpha",
            CreateIdempotencyKey: "create-alpha",
            ConversationId: "conversation-authoritative",
            TurnId: "turn-authoritative",
            Status: "append_committed",
            SourceVersion: 3,
            DeliveryActorId: "chat-history-delivery:existing",
            RequestHash: requestHash));
        var port = CreatePort(runtime, dispatch, recoveryReader: recoveryReader);

        var result = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Create(createIdentity)));

        result.Succeeded.Should().BeTrue();
        result.Replayed.Should().BeTrue();
        result.ChatContext.Should().BeEquivalentTo(
            new WorkflowChatContext("scope-alpha", "conversation-authoritative", "turn-authoritative"));
        result.Reservation!.DeliveryActorId.Should().Be("chat-history-delivery:existing");
        runtime.CreatedActors.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReserveAsync_ShouldRejectCreateIdentityConflictFromRecoveryReadModel()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var recoveryReader = new RecordingChatCreateRecoveryReader();
        recoveryReader.Seed(new ChatCreateRecoveryRecord(
            ScopeId: "scope-alpha",
            CreateIdempotencyKey: "create-alpha",
            ConversationId: "conversation-authoritative",
            TurnId: "turn-authoritative",
            Status: "reserved",
            SourceVersion: 1,
            DeliveryActorId: "chat-history-delivery:existing",
            RequestHash: "different-request-hash"));
        var port = CreatePort(runtime, dispatch, recoveryReader: recoveryReader);

        var result = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Create(
                new WorkflowChatCreateIdempotencyIdentity("create-alpha"))));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(WorkflowChatHistoryTerminalDeliveryReservationFailure.IdempotencyConflict);
        runtime.CreatedActors.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReserveAsync_ShouldUseStableConversationTurnAndDeliveryIdsForCreateIdentity()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var createIdentity = new WorkflowChatCreateIdempotencyIdentity("create-alpha");
        var port = CreatePort(runtime, dispatch, recoveryReader: new RecordingChatCreateRecoveryReader());

        var first = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Create(createIdentity)));
        var second = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Create(createIdentity)));

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.ChatContext.Should().BeEquivalentTo(first.ChatContext);
        second.Reservation!.DeliveryActorId.Should().Be(first.Reservation!.DeliveryActorId);
        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls
            .Select(call => call.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryReserveRequested>().CreateIdempotencyKey)
            .Should()
            .Equal("create-alpha", "create-alpha");
    }

    [Fact]
    public async Task ReserveAsync_ShouldContinueExistingConversationAndGenerateTurn()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var admissionReader = new RecordingChatConversationContinuationAdmissionReader();
        admissionReader.SeedExistingConversation("scope-alpha", "conversation-existing");
        var port = CreatePort(runtime, dispatch, admissionReader);

        var result = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Continue("conversation-existing")));

        result.Succeeded.Should().BeTrue();
        result.ChatContext.Should().BeEquivalentTo(
            new WorkflowChatContext(
                "scope-alpha",
                "conversation-existing",
                result.ChatContext!.TurnId));
        result.ChatContext!.TurnId.Should().NotBeNullOrWhiteSpace();
        result.ChatContext.TurnId.Should().NotBe("turn-from-client");
        var command = dispatch.Calls.Should().ContainSingle().Which.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryReserveRequested>();
        command.ConversationId.Should().Be("conversation-existing");
        command.TurnId.Should().Be(result.ChatContext.TurnId);
        command.CreateConversationIfMissing.Should().BeFalse();
        admissionReader.Calls.Should().ContainSingle()
            .Which.Should().Be(("scope-alpha", "conversation-existing"));
    }

    [Fact]
    public async Task ReserveAsync_ShouldReturnNotFound_WhenContinuingMissingConversation()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var admissionReader = new RecordingChatConversationContinuationAdmissionReader();
        var port = CreatePort(runtime, dispatch, admissionReader);

        var result = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Continue("conversation-missing")));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);
        runtime.CreatedActors.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
        admissionReader.Calls.Should().ContainSingle()
            .Which.Should().Be(("scope-alpha", "conversation-missing"));
    }

    [Fact]
    public async Task ReserveAsync_ShouldReturnNotFound_WhenContinuingDeletedConversation()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var admissionReader = new RecordingChatConversationContinuationAdmissionReader();
        admissionReader.SeedDeletedConversation("scope-alpha", "conversation-deleted");
        var port = CreatePort(runtime, dispatch, admissionReader);

        var result = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Continue("conversation-deleted")));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);
        runtime.CreatedActors.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
        admissionReader.Calls.Should().ContainSingle()
            .Which.Should().Be(("scope-alpha", "conversation-deleted"));
    }

    [Fact]
    public async Task ReserveAsync_ShouldContinueExistingConversation_WhenRuntimeExposesOnlyProxyAgent()
    {
        var runtime = new RecordingActorRuntime();
        runtime.SeedProxyBackedConversation("scope-alpha", "conversation-proxy");
        var dispatch = new RecordingActorDispatchPort();
        var admissionReader = new RecordingChatConversationContinuationAdmissionReader();
        admissionReader.SeedExistingConversation("scope-alpha", "conversation-proxy");
        var port = CreatePort(runtime, dispatch, admissionReader);

        var result = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Continue("conversation-proxy")));

        result.Succeeded.Should().BeTrue();
        result.ChatContext.Should().NotBeNull();
        result.ChatContext!.ConversationId.Should().Be("conversation-proxy");
        result.ChatContext.TurnId.Should().NotBeNullOrWhiteSpace();
        result.ChatContext.TurnId.Should().NotBe("turn-from-client");
        runtime.CreatedActors.Should().ContainSingle()
            .Which.Should().Be((typeof(ChatTurnHistoryDeliveryGAgent), result.Reservation!.DeliveryActorId));
        var command = dispatch.Calls.Should().ContainSingle().Which.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryReserveRequested>();
        command.ConversationId.Should().Be("conversation-proxy");
        command.TurnId.Should().Be(result.ChatContext.TurnId);
        command.CreateConversationIfMissing.Should().BeFalse();
        runtime.GetCalls.Should().BeEmpty();
        admissionReader.Calls.Should().ContainSingle()
            .Which.Should().Be(("scope-alpha", "conversation-proxy"));
    }

    [Fact]
    public async Task BindAcceptedAndAbandonAsync_ShouldDispatchToDeliveryActorWithBusinessDeliveryId()
    {
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(new RecordingActorRuntime(), dispatch);
        var reservation = new WorkflowChatHistoryTerminalDeliveryReservation(
            "chat-history-delivery:actor-address-alpha",
            DeliveryId,
            WorkflowActorId,
            WorkflowCommandId);

        await port.BindAcceptedAsync(
            reservation,
            new WorkflowChatRunAcceptedReceipt(
                WorkflowActorId,
                "direct",
                WorkflowCommandId,
                WorkflowCorrelationId));
        await port.AbandonAsync(reservation, "workflow dispatch rejected");

        dispatch.Calls.Should().HaveCount(2);
        var bindCall = dispatch.Calls[0];
        bindCall.ActorId.Should().Be(reservation.DeliveryActorId);
        bindCall.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryAcceptedBound>()
            .DeliveryId.Should().Be(DeliveryId);
        var abandonCall = dispatch.Calls[1];
        abandonCall.ActorId.Should().Be(reservation.DeliveryActorId);
        abandonCall.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryAbandonedEvent>()
            .DeliveryId.Should().Be(DeliveryId);
    }

    private static ChatTurnHistoryTerminalDeliveryPort CreatePort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IChatConversationContinuationAdmissionReader? admissionReader = null,
        IChatCreateRecoveryReader? recoveryReader = null) =>
        new(
            runtime,
            dispatchPort,
            admissionReader ?? new RecordingChatConversationContinuationAdmissionReader(),
            NullLogger<ChatTurnHistoryTerminalDeliveryPort>.Instance,
            createRecoveryReader: recoveryReader);

    private static WorkflowChatHistoryTerminalDeliveryReservationRequest ReservationRequest(
        WorkflowChatConversationIntent conversation) =>
        new(
            DeliveryId,
            "scope-alpha",
            conversation,
            "original user text",
            WorkflowActorId,
            WorkflowCommandId,
            WorkflowCorrelationId);

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _existing = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        public List<(Type AgentType, string? Id)> CreatedActors { get; } = [];
        public List<string> GetCalls { get; } = [];

        public void SeedProxyBackedConversation(string scopeId, string conversationId)
        {
            var actorId = ChatHistoryActorIds.Conversation(scopeId, conversationId);
            _existing.Add(actorId);
            _actors[actorId] = new NoopActor(actorId, new NoopAgent());
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreatedActors.Add((agentType, id));
            if (!string.IsNullOrWhiteSpace(id))
            {
                _existing.Add(id);
                _actors[id] = new NoopActor(id);
            }
            return Task.FromResult<IActor>(new NoopActor(id ?? string.Empty));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id)
        {
            GetCalls.Add(id);
            return Task.FromResult(_actors.GetValueOrDefault(id));
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_existing.Contains(id));

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

    private sealed class NoopActor(string id, IAgent? agent = null) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent ?? new NoopAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingChatConversationContinuationAdmissionReader
        : IChatConversationContinuationAdmissionReader
    {
        private readonly HashSet<(string ScopeId, string ConversationId)> _continuableConversations = [];

        public List<(string ScopeId, string ConversationId)> Calls { get; } = [];

        public void SeedExistingConversation(string scopeId, string conversationId) =>
            _continuableConversations.Add((scopeId, conversationId));

        public void SeedDeletedConversation(string scopeId, string conversationId) =>
            _continuableConversations.Remove((scopeId, conversationId));

        public Task<bool> CanContinueAsync(string scopeId, string conversationId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((scopeId, conversationId));
            return Task.FromResult(_continuableConversations.Contains((scopeId, conversationId)));
        }
    }

    private sealed class RecordingChatCreateRecoveryReader : IChatCreateRecoveryReader
    {
        private readonly Dictionary<(string ScopeId, string CreateIdempotencyKey), ChatCreateRecoveryRecord> _records = [];

        public void Seed(ChatCreateRecoveryRecord record) =>
            _records[(record.ScopeId, record.CreateIdempotencyKey)] = record;

        public Task<ChatCreateRecoveryRecord?> FindAsync(
            string scopeId,
            string createIdempotencyKey,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_records.GetValueOrDefault((scopeId, createIdempotencyKey)));
        }
    }

    private sealed class NoopAgent : IAgent
    {
        public string Id => "noop";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("noop");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
