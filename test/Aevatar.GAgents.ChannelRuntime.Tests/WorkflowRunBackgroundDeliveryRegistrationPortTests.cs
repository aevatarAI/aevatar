using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class WorkflowRunBackgroundDeliveryRegistrationPortTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 1, 2, 3, TimeSpan.Zero);
    private const string DeliveryId = "delivery-alpha";
    private static readonly string DeliveryActorId = WorkflowRunDeliveryActorIds.FromDeliveryId(DeliveryId);
    private const string WorkflowCommandId = "wf-command-2675";

    [Fact]
    public async Task ReserveAsync_ShouldCreateActorAndDispatchTypedReservationBeforeReturningReceipt()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);

        var receipt = await port.ReserveAsync(Reservation());

        runtime.CreatedActors.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowRunDeliveryGAgent), DeliveryActorId));
        DeliveryActorId.Should().StartWith("workflow-run-delivery:").And.NotBe(DeliveryId);
        var call = dispatch.Dispatches.Should().ContainSingle().Which;
        call.ActorId.Should().Be(DeliveryActorId);
        call.Envelope.Runtime.DeliveryIdentity.OperationId.Should().Be($"workflow-run-delivery-reserve:{DeliveryActorId}");
        var command = call.Envelope.Payload.Unpack<WorkflowRunDeliveryReserveRequested>();
        command.DeliveryId.Should().Be(DeliveryId);
        command.ExpectedWorkflowCommandId.Should().Be(WorkflowCommandId);
        command.WorkflowResultDeliveryCredential.SecretReference.Ref.Should().Be("sec-delivery-2675");
        command.ExpiresAtUnixMs.Should().Be(Now.AddMinutes(5).ToUnixTimeMilliseconds());
        receipt.Should().Be(ReservationReceipt());
    }

    [Fact]
    public async Task RegisterAsync_ShouldOnlyBindAcceptedWorkflowReceiptIdentity()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists(DeliveryActorId);
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);

        var receipt = await port.RegisterAsync(ReservationReceipt(), Registration());

        var call = dispatch.Dispatches.Should().ContainSingle().Which;
        var command = call.Envelope.Payload.Unpack<WorkflowRunDeliveryStartRequested>();
        command.DeliveryId.Should().Be(DeliveryId);
        command.WorkflowActorId.Should().Be("workflow-actor-2675");
        command.WorkflowCommandId.Should().Be(WorkflowCommandId);
        command.WorkflowRunId.Should().Be("workflow-run-2675");
        receipt.DeliveryActorId.Should().Be(DeliveryActorId);
        receipt.WorkflowActorId.Should().Be("workflow-actor-2675");
    }

    [Theory]
    [InlineData("", "workflow-actor-2675", WorkflowCommandId)]
    [InlineData(DeliveryId, "", WorkflowCommandId)]
    [InlineData(DeliveryId, "workflow-actor-2675", "")]
    public async Task RegisterAsync_ShouldRejectMalformedReceiptIdentityBeforeDispatch(
        string deliveryId,
        string workflowActorId,
        string workflowCommandId)
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists(DeliveryActorId);
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);
        var registration = Registration() with
        {
            DeliveryId = deliveryId,
            WorkflowActorId = workflowActorId,
            WorkflowCommandId = workflowCommandId,
        };

        Func<Task> act = async () => { _ = await port.RegisterAsync(ReservationReceipt(), registration); };

        await act.Should().ThrowAsync<ArgumentException>();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task AbandonAsync_ShouldDispatchTypedCompensation()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists(DeliveryActorId);
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);

        await port.AbandonAsync(
            ReservationReceipt(),
            "workflow dispatch rejected");

        var call = dispatch.Dispatches.Should().ContainSingle().Which;
        var command = call.Envelope.Payload.Unpack<WorkflowRunDeliveryAbandonRequested>();
        command.DeliveryId.Should().Be(DeliveryId);
        command.WorkflowCommandId.Should().Be(WorkflowCommandId);
        command.Reason.Should().Be("workflow dispatch rejected");
    }

    [Fact]
    public async Task RegisterAsync_ShouldRejectBusinessDeliveryIdMasqueradingAsActorAddress()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists(DeliveryId);
        var dispatch = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatch);
        var invalidReceipt = new WorkflowRunBackgroundDeliveryReservationReceipt(
            DeliveryId,
            DeliveryId,
            WorkflowCommandId);

        Func<Task> act = async () => { _ = await port.RegisterAsync(invalidReceipt, Registration()); };

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*actor id does not match its business id*");
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("register")]
    [InlineData("abandon")]
    public async Task RejectedAdmission_ShouldNeverReturnAReceipt(string operation)
    {
        var runtime = new RecordingActorRuntime();
        if (operation != "reserve")
            runtime.MarkExists(DeliveryActorId);
        var port = CreatePort(runtime, new RecordingActorDispatchPort(accepted: false));

        Func<Task> act = operation switch
        {
            "reserve" => async () => { _ = await port.ReserveAsync(Reservation()); },
            "register" => async () => { _ = await port.RegisterAsync(ReservationReceipt(), Registration()); },
            _ => () => port.AbandonAsync(
                ReservationReceipt(),
                "dispatch rejected"),
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not accepted*");
        if (operation == "reserve")
            runtime.DestroyedActors.Should().ContainSingle().Which.Should().Be(DeliveryActorId);
    }

    [Fact]
    public void Reservation_ShouldRejectMissingCommandOrTypedCredential()
    {
        Action missingCommand = () => _ = Reservation(expectedWorkflowCommandId: " ");
        Action missingCredential = () => _ = Reservation(credential: new ChannelWorkflowResultDeliveryCredential());

        missingCommand.Should().Throw<ArgumentException>();
        missingCredential.Should().Throw<ArgumentException>();
    }

    private static WorkflowRunBackgroundDeliveryRegistrationPort CreatePort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort) =>
        new(
            runtime,
            dispatchPort,
            NullLogger<WorkflowRunBackgroundDeliveryRegistrationPort>.Instance,
            new FakeTimeProvider(Now));

    private static WorkflowRunBackgroundDeliveryReservation Reservation(
        string expectedWorkflowCommandId = WorkflowCommandId,
        ChannelWorkflowResultDeliveryCredential? credential = null) =>
        new(
            DeliveryId,
            expectedWorkflowCommandId,
            "lark",
            "reply-message-2675",
            "platform-message-2675",
            credential ?? Credential(),
            "scope-2675",
            "bot-2675",
            Now.AddMinutes(5).ToUnixTimeMilliseconds());

    private static WorkflowRunBackgroundDeliveryRegistration Registration() =>
        new(
            DeliveryId,
            "workflow-actor-2675",
            "workflow-run-2675",
            WorkflowCommandId,
            "workflow-correlation-2675",
            "aevatar://actors/workflow-actor-2675/runs/workflow-command-2675",
            "ignored-after-reservation",
            "ignored-after-reservation",
            "ignored-after-reservation",
            Credential(),
            "ignored-after-reservation",
            "ignored-after-reservation");

    private static WorkflowRunBackgroundDeliveryReservationReceipt ReservationReceipt() =>
        new(DeliveryActorId, DeliveryId, WorkflowCommandId);

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

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _existing = new(StringComparer.Ordinal);
        public List<(Type AgentType, string? Id)> CreatedActors { get; } = [];
        public List<string> DestroyedActors { get; } = [];

        public void MarkExists(string actorId) => _existing.Add(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreatedActors.Add((agentType, id));
            if (!string.IsNullOrWhiteSpace(id))
                _existing.Add(id);
            return Task.FromResult<IActor>(new NoopActor(id ?? string.Empty));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedActors.Add(id);
            _existing.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(_existing.Contains(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort(bool accepted = true) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            var acceptedReceipt = DispatchAdmissionFactory.Create(actorId, envelope);
            return Task.FromResult(accepted
                ? acceptedReceipt
                : acceptedReceipt with { Accepted = false });
        }
    }

    private sealed class NoopActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new NoopAgent(id);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
