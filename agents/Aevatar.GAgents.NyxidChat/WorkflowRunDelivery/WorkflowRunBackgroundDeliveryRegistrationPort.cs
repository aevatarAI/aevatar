using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;

public sealed class WorkflowRunBackgroundDeliveryRegistrationPort
    : IWorkflowRunBackgroundDeliveryRegistrationPort
{
    private const string PublisherActorId = "workflow-run-background-delivery-registration";
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowRunBackgroundDeliveryRegistrationPort> _logger;

    public WorkflowRunBackgroundDeliveryRegistrationPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ILogger<WorkflowRunBackgroundDeliveryRegistrationPort> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowRunBackgroundDeliveryReservationReceipt> ReserveAsync(
        WorkflowRunBackgroundDeliveryReservation reservation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.ExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
            throw new InvalidOperationException("Workflow run background delivery reservation has already expired.");

        var actorId = WorkflowRunDeliveryActorIds.FromDeliveryId(reservation.DeliveryId);
        var actorCreated = false;
        if (!await _actorRuntime.ExistsAsync(actorId).ConfigureAwait(false))
        {
            await _actorRuntime.CreateAsync<WorkflowRunDeliveryGAgent>(actorId, ct).ConfigureAwait(false);
            actorCreated = true;
        }

        var command = new WorkflowRunDeliveryReserveRequested
        {
            DeliveryId = reservation.DeliveryId,
            ExpectedWorkflowCommandId = reservation.ExpectedWorkflowCommandId,
            ChannelPlatform = reservation.ChannelPlatform,
            ReplyMessageId = reservation.ReplyMessageId,
            PlatformMessageId = reservation.PlatformMessageId,
            RegistrationScopeId = reservation.RegistrationScopeId,
            WorkflowResultDeliveryCredential = reservation.WorkflowResultDeliveryCredential.Clone(),
            BotRegistrationId = reservation.BotRegistrationId,
            ExpiresAtUnixMs = reservation.ExpiresAtUnixMs,
        };

        try
        {
            await DispatchAcceptedAsync(
                    actorId,
                    command,
                    reservation.ExpectedWorkflowCommandId,
                    $"workflow-run-delivery-reserve:{actorId}",
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            if (actorCreated)
                await _actorRuntime.DestroyAsync(actorId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "Reserved workflow run background delivery: deliveryActorId={DeliveryActorId} commandId={CommandId}",
            actorId,
            reservation.ExpectedWorkflowCommandId);
        return new WorkflowRunBackgroundDeliveryReservationReceipt(
            actorId,
            reservation.DeliveryId,
            reservation.ExpectedWorkflowCommandId);
    }

    public async Task<WorkflowRunBackgroundDeliveryReceipt> RegisterAsync(
        WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt,
        WorkflowRunBackgroundDeliveryRegistration registration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservationReceipt);
        ArgumentNullException.ThrowIfNull(registration);

        var actorId = reservationReceipt.DeliveryActorId?.Trim();
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Workflow run background delivery actor id is required.", nameof(reservationReceipt));
        if (string.IsNullOrWhiteSpace(reservationReceipt.DeliveryId) ||
            !string.Equals(reservationReceipt.DeliveryId.Trim(), registration.DeliveryId?.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Workflow run background delivery business id does not match its reservation.", nameof(registration));
        }
        if (!string.Equals(
                actorId,
                WorkflowRunDeliveryActorIds.FromDeliveryId(reservationReceipt.DeliveryId),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Workflow run background delivery actor id does not match its business id.", nameof(reservationReceipt));
        }
        if (string.IsNullOrWhiteSpace(registration.WorkflowActorId))
            throw new ArgumentException("Workflow run background delivery requires a workflow actor id.", nameof(registration));
        if (string.IsNullOrWhiteSpace(registration.WorkflowCommandId))
            throw new ArgumentException("Workflow run background delivery requires a workflow command id.", nameof(registration));
        if (!string.Equals(
                reservationReceipt.WorkflowCommandId?.Trim(),
                registration.WorkflowCommandId.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Workflow run background delivery command id does not match its reservation.", nameof(registration));
        }
        if (!await _actorRuntime.ExistsAsync(actorId).ConfigureAwait(false))
            throw new InvalidOperationException($"Workflow run delivery actor '{actorId}' was not reserved.");

        var command = new WorkflowRunDeliveryStartRequested
        {
            DeliveryId = reservationReceipt.DeliveryId.Trim(),
            WorkflowActorId = registration.WorkflowActorId ?? string.Empty,
            WorkflowRunId = registration.WorkflowRunId ?? string.Empty,
            WorkflowCommandId = registration.WorkflowCommandId ?? string.Empty,
            WorkflowCorrelationId = registration.WorkflowCorrelationId ?? string.Empty,
            StreamTopic = registration.StreamTopic ?? string.Empty,
        };

        _logger.LogInformation(
            "Registering workflow run background delivery: deliveryActorId={DeliveryActorId} workflowActorId={WorkflowActorId} commandId={CommandId}",
            actorId,
            registration.WorkflowActorId,
            registration.WorkflowCommandId);
        await DispatchAcceptedAsync(
                actorId,
                command,
                string.IsNullOrWhiteSpace(registration.WorkflowCorrelationId)
                    ? registration.WorkflowCommandId
                    : registration.WorkflowCorrelationId,
                $"workflow-run-delivery-start:{actorId}",
                ct)
            .ConfigureAwait(false);

        return new WorkflowRunBackgroundDeliveryReceipt
        {
            DeliveryActorId = actorId,
            WorkflowActorId = registration.WorkflowActorId ?? string.Empty,
            WorkflowRunId = registration.WorkflowRunId ?? string.Empty,
            WorkflowCommandId = registration.WorkflowCommandId ?? string.Empty,
            WorkflowCorrelationId = registration.WorkflowCorrelationId ?? string.Empty,
            StreamTopic = registration.StreamTopic ?? string.Empty,
            ChannelPlatform = registration.ChannelPlatform ?? string.Empty,
            ReplyMessageId = registration.ReplyMessageId ?? string.Empty,
            PlatformMessageId = registration.PlatformMessageId ?? string.Empty,
            RegistrationScopeId = registration.RegistrationScopeId ?? string.Empty,
        };
    }

    public async Task AbandonAsync(
        WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservationReceipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationReceipt.DeliveryActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationReceipt.DeliveryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationReceipt.WorkflowCommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var actorId = reservationReceipt.DeliveryActorId.Trim();
        if (!string.Equals(
                actorId,
                WorkflowRunDeliveryActorIds.FromDeliveryId(reservationReceipt.DeliveryId),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Workflow run background delivery actor id does not match its business id.", nameof(reservationReceipt));
        }
        if (!await _actorRuntime.ExistsAsync(actorId).ConfigureAwait(false))
            throw new InvalidOperationException($"Workflow run delivery actor '{actorId}' does not exist.");

        await DispatchAcceptedAsync(
                actorId,
                new WorkflowRunDeliveryAbandonRequested
                {
                    DeliveryId = reservationReceipt.DeliveryId.Trim(),
                    WorkflowCommandId = reservationReceipt.WorkflowCommandId.Trim(),
                    Reason = reason.Trim(),
                },
                reservationReceipt.WorkflowCommandId,
                $"workflow-run-delivery-abandon:{actorId}:{reservationReceipt.WorkflowCommandId.Trim()}",
                ct)
            .ConfigureAwait(false);
    }

    private async Task DispatchAcceptedAsync(
        string actorId,
        IMessage command,
        string? correlationId,
        string operationId,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId?.Trim() ?? string.Empty,
            },
        };
        envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId = operationId;

        var admission = await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
        if (!admission.Accepted)
            throw new InvalidOperationException($"Workflow run delivery command '{command.Descriptor.Name}' was not accepted.");
    }
}
