using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

/// <summary>
/// Projects workflow execution envelopes directly to workflow run event envelopes.
/// </summary>
public sealed class WorkflowExecutionRunEventProjector
    : ProjectionSessionEventProjectorBase<WorkflowExecutionProjectionContext, WorkflowRunEventEnvelope>
{
    private readonly IEventEnvelopeToWorkflowRunEventMapper _mapper;

    public WorkflowExecutionRunEventProjector(
        IEventEnvelopeToWorkflowRunEventMapper mapper,
        IProjectionSessionEventHub<WorkflowRunEventEnvelope> runEventStreamHub)
        : base(runEventStreamHub)
    {
        _mapper = mapper;
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<WorkflowRunEventEnvelope>> ResolveSessionEventEntries(
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope)
    {
        // Keep streaming pinned to the projection session command id.
        // Missing session identity is fail-closed: trace correlation is not a stream key.
        var streamCommandId = context.SessionId;
        if (string.IsNullOrWhiteSpace(streamCommandId))
            return EmptyEntries;

        IReadOnlyList<WorkflowRunEventEnvelope> runEvents = _mapper.Map(envelope);
        if (runEvents.Count == 0)
            return EmptyEntries;

        using var activity = AevatarActivitySource.StartWorkflowRun(
            context.RootActorId,
            ResolveWorkflowName(envelope),
            ResolveStepName(runEvents));
        AevatarActivitySource.SafeSetStatus(activity, System.Diagnostics.ActivityStatusCode.Ok);

        var entries = new List<ProjectionSessionEventEntry<WorkflowRunEventEnvelope>>(runEvents.Count);
        foreach (var runEvent in runEvents)
        {
            entries.Add(new ProjectionSessionEventEntry<WorkflowRunEventEnvelope>(
                context.RootActorId,
                streamCommandId,
                runEvent));
        }

        return entries;
    }

    private static string ResolveWorkflowName(EventEnvelope envelope)
    {
        var mappedEnvelope = envelope;
        if (CommittedStateEventEnvelope.TryCreateObservedEnvelope(envelope, out var observed) && observed?.Payload != null)
            mappedEnvelope = observed;

        var payload = mappedEnvelope.Payload;
        if (payload == null)
            return "unknown";

        if (payload.Is(StartWorkflowEvent.Descriptor))
            return NormalizeWorkflowName(payload.Unpack<StartWorkflowEvent>().WorkflowName);
        if (payload.Is(WorkflowRunExecutionStartedEvent.Descriptor))
            return NormalizeWorkflowName(payload.Unpack<WorkflowRunExecutionStartedEvent>().WorkflowName);
        if (payload.Is(WorkflowCompletedEvent.Descriptor))
            return NormalizeWorkflowName(payload.Unpack<WorkflowCompletedEvent>().WorkflowName);
        if (payload.Is(WorkflowStoppedEvent.Descriptor))
            return NormalizeWorkflowName(payload.Unpack<WorkflowStoppedEvent>().WorkflowName);

        return "unknown";
    }

    private static string? ResolveStepName(IReadOnlyList<WorkflowRunEventEnvelope> runEvents)
    {
        foreach (var runEvent in runEvents)
        {
            var stepName = runEvent.EventCase switch
            {
                WorkflowRunEventEnvelope.EventOneofCase.StepStarted => runEvent.StepStarted.StepName,
                WorkflowRunEventEnvelope.EventOneofCase.StepFinished => runEvent.StepFinished.StepName,
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(stepName))
                return stepName;
        }

        return null;
    }

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? "unknown" : workflowName.Trim();
}
