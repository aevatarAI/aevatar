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
            ScheduledDispatchTargetKind.ServiceInvocation => Task.FromResult(PrepareServiceInvocationTarget(configuration, commandId, correlationId)),
            ScheduledDispatchTargetKind.Envelope => throw new ArgumentException(
                "Raw envelope scheduled dispatch targets are not supported by target preparation.",
                nameof(configuration)),
            _ => throw new ArgumentException(
                $"Unsupported scheduled dispatch target kind '{configuration.Target.Kind}'.",
                nameof(configuration)),
        };
    }

    private static PreparedScheduledDispatchTarget PrepareServiceInvocationTarget(
        ScheduledDispatchConfiguration configuration,
        string commandId,
        string correlationId)
    {
        var target = configuration.Target.ServiceInvocation
            ?? throw new ArgumentException("Service invocation scheduled dispatch target is required.", nameof(configuration));
        var safePayload = ScheduledServiceInvocationPayloadPolicy.StripScheduleOwnedCredentialFields(target.Payload);
        var invocation = new ServiceInvocationRequest
        {
            Identity = target.Identity.Clone(),
            EndpointId = target.EndpointId,
            Payload = safePayload.Clone(),
            CommandId = commandId,
            CorrelationId = correlationId,
            RevisionId = target.RevisionId ?? string.Empty,
            ScheduleId = configuration.ScheduleId ?? string.Empty,
        };
        if (target.Caller != null)
            invocation.Caller = target.Caller.Clone();

        var envelope = CreateAdapterEnvelope(
            commandId,
            correlationId,
            ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            Any.Pack(invocation));
        var safeDescriptor = configuration.Target with
        {
            ServiceInvocation = target with { Payload = safePayload },
        };

        return new PreparedScheduledDispatchTarget(
            ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            envelope,
            envelope.Payload.TypeUrl,
            safeDescriptor);
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
}
