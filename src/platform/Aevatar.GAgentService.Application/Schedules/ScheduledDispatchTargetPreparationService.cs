using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class ScheduledDispatchTargetPreparationService : IScheduledDispatchTargetPreparationService
{
    public Task<PreparedScheduledDispatchTarget> PrepareAsync(
        ScheduledDispatchConfiguration configuration,
        string commandId,
        string correlationId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.Target.Kind switch
        {
            ScheduledDispatchTargetKind.Envelope => Task.FromResult(PrepareEnvelopeTarget(configuration, commandId, correlationId)),
            ScheduledDispatchTargetKind.ServiceInvocation => Task.FromResult(PrepareServiceInvocationTarget(configuration, commandId, correlationId)),
            _ => throw new ArgumentException(
                $"Unsupported scheduled dispatch target kind '{configuration.Target.Kind}'.",
                nameof(configuration)),
        };
    }

    private static PreparedScheduledDispatchTarget PrepareEnvelopeTarget(
        ScheduledDispatchConfiguration configuration,
        string commandId,
        string correlationId)
    {
        var target = configuration.Target;
        var envelope = target.Envelope?.Clone()
            ?? throw new ArgumentException("Envelope scheduled dispatch target is required.", nameof(configuration));
        if (envelope.Payload == null)
            throw new ArgumentException("Envelope scheduled dispatch target requires a payload.", nameof(configuration));

        envelope.Id = string.IsNullOrWhiteSpace(envelope.Id) ? commandId : envelope.Id.Trim();
        envelope.Timestamp ??= Timestamp.FromDateTime(DateTime.UtcNow);
        var targetActorId = ResolveTargetActorId(target.ActorId, envelope);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(
            ResolvePublisherActorId(envelope, configuration.ScheduleId),
            targetActorId);
        var propagation = envelope.EnsurePropagation();
        if (string.IsNullOrWhiteSpace(propagation.CorrelationId))
            propagation.CorrelationId = correlationId;

        return new PreparedScheduledDispatchTarget(
            targetActorId,
            envelope,
            envelope.Payload.TypeUrl,
            configuration.Target);
    }

    private static PreparedScheduledDispatchTarget PrepareServiceInvocationTarget(
        ScheduledDispatchConfiguration configuration,
        string commandId,
        string correlationId)
    {
        var target = configuration.Target.ServiceInvocation
            ?? throw new ArgumentException("Service invocation scheduled dispatch target is required.", nameof(configuration));
        var invocation = new ServiceInvocationRequest
        {
            Identity = target.Identity.Clone(),
            EndpointId = target.EndpointId,
            Payload = target.Payload.Clone(),
            CommandId = commandId,
            CorrelationId = correlationId,
            RevisionId = target.RevisionId ?? string.Empty,
        };
        if (target.Caller != null)
            invocation.Caller = target.Caller.Clone();

        var envelope = CreateAdapterEnvelope(
            commandId,
            correlationId,
            ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            Any.Pack(invocation));

        return new PreparedScheduledDispatchTarget(
            ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            envelope,
            envelope.Payload.TypeUrl,
            configuration.Target);
    }

    private static EventEnvelope CreateAdapterEnvelope(
        string commandId,
        string correlationId,
        string targetActorId,
        Any payload) =>
        new()
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            Route = EnvelopeRouteSemantics.CreateDirect(targetActorId, targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
        };

    private static string ResolveTargetActorId(string? configuredActorId, EventEnvelope envelope)
    {
        if (!string.IsNullOrWhiteSpace(configuredActorId))
            return configuredActorId.Trim();
        if (!string.IsNullOrWhiteSpace(envelope.Route.GetTargetActorId()))
            return envelope.Route.GetTargetActorId().Trim();

        throw new ArgumentException("Envelope scheduled dispatch target requires an actor id.", nameof(envelope));
    }

    private static string ResolvePublisherActorId(EventEnvelope envelope, string scheduleId)
    {
        if (!string.IsNullOrWhiteSpace(envelope.Route?.PublisherActorId))
            return envelope.Route.PublisherActorId.Trim();

        return string.IsNullOrWhiteSpace(scheduleId)
            ? "scheduled.dispatch"
            : scheduleId.Trim();
    }
}
