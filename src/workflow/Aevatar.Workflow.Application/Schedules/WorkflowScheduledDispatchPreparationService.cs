using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Schedules;

internal sealed class WorkflowScheduledDispatchPreparationService : IWorkflowScheduledDispatchPreparationService
{
    public Task<ScheduledDispatchPreparation> PrepareAsync(
        WorkflowScheduleConfiguration configuration,
        string commandId,
        string correlationId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(configuration);

        var payload = new WorkflowScheduledDispatchStartRequest
        {
            ScheduleId = configuration.ScheduleId,
            WorkflowName = configuration.WorkflowName,
            Prompt = configuration.Prompt,
            ScopeId = string.IsNullOrWhiteSpace(configuration.ScopeId) ? string.Empty : configuration.ScopeId,
            SourceActorId = string.IsNullOrWhiteSpace(configuration.SourceActorId) ? string.Empty : configuration.SourceActorId,
        };
        foreach (var (key, value) in configuration.Headers)
            payload.Headers[key] = value;

        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(
                WorkflowScheduledDispatchAdapterConventions.TargetActorId,
                WorkflowScheduledDispatchAdapterConventions.TargetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
        };

        return Task.FromResult(new ScheduledDispatchPreparation(
            null,
            envelope,
            envelope.Payload?.TypeUrl ?? string.Empty,
            new WorkflowScheduleTargetDescriptor(
                configuration.WorkflowName,
                configuration.Prompt,
                configuration.ScopeId ?? string.Empty,
                configuration.SourceActorId ?? string.Empty)));
    }
}
