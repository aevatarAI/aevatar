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
    public async Task ReserveAsync_ShouldContinueExistingConversationAndGenerateTurn()
    {
        var runtime = new RecordingActorRuntime();
        runtime.SeedExistingConversation("scope-alpha", "conversation-existing");
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);

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
    }

    [Fact]
    public async Task ReserveAsync_ShouldReturnNotFound_WhenContinuingMissingConversation()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);

        var result = await port.ReserveAsync(
            ReservationRequest(WorkflowChatConversationIntent.Continue("conversation-missing")));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);
        runtime.CreatedActors.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
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
        IActorDispatchPort dispatchPort) =>
        new(
            runtime,
            dispatchPort,
            NullLogger<ChatTurnHistoryTerminalDeliveryPort>.Instance);

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
        public List<(Type AgentType, string? Id)> CreatedActors { get; } = [];

        public void SeedExistingConversation(string scopeId, string conversationId) =>
            _existing.Add(ChatHistoryActorIds.Conversation(scopeId, conversationId));

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreatedActors.Add((agentType, id));
            if (!string.IsNullOrWhiteSpace(id))
                _existing.Add(id);
            return Task.FromResult<IActor>(new NoopActor(id ?? string.Empty));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

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

    private sealed class NoopActor(string id) : IActor
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
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
