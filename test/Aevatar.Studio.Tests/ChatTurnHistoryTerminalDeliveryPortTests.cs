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
    public async Task ReserveAsync_ShouldReturnSeparateDeliveryActorAddressAndBusinessDeliveryId()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);

        var reservation = await port.ReserveAsync(ReservationRequest());

        reservation.Should().NotBeNull();
        reservation!.DeliveryId.Should().Be(DeliveryId);
        reservation.DeliveryActorId.Should().StartWith("chat-history-delivery:");
        reservation.DeliveryActorId.Should().NotBe(DeliveryId);
        runtime.CreatedActors.Should().ContainSingle()
            .Which.Should().Be((typeof(ChatTurnHistoryDeliveryGAgent), reservation.DeliveryActorId));
        var call = dispatch.Calls.Should().ContainSingle().Which;
        call.ActorId.Should().Be(reservation.DeliveryActorId);
        var command = call.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryReserveRequested>();
        command.DeliveryId.Should().Be(DeliveryId);
        command.WorkflowActorId.Should().Be(WorkflowActorId);
        command.WorkflowCommandId.Should().Be(WorkflowCommandId);
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

    private static WorkflowChatHistoryTerminalDeliveryReservationRequest ReservationRequest() =>
        new(
            DeliveryId,
            "scope-alpha",
            "conversation-alpha",
            "turn-alpha",
            "original user text",
            WorkflowActorId,
            WorkflowCommandId,
            WorkflowCorrelationId);

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _existing = new(StringComparer.Ordinal);
        public List<(Type AgentType, string? Id)> CreatedActors { get; } = [];

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
