using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Core.Modules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Execution;

internal sealed class WorkflowToolCallAttemptPersistenceTelemetryHook(
    ILogger<WorkflowToolCallAttemptPersistenceTelemetryHook>? logger = null)
    : ICommittedStatePublicationHook
{
    private readonly ILogger<WorkflowToolCallAttemptPersistenceTelemetryHook> _logger =
        logger ?? NullLogger<WorkflowToolCallAttemptPersistenceTelemetryHook>.Instance;

    public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var stateEvent = context.Published.StateEvent;
        if (context.ActorType != typeof(WorkflowRunGAgent) ||
            stateEvent == null ||
            CommittedStateRepublish.IsRepublishEventId(stateEvent.EventId))
        {
            return Task.CompletedTask;
        }

        foreach (var observation in WorkflowToolCallAttemptPersistence.BuildCommittedObservations(stateEvent))
            WorkflowToolCallTelemetry.Record(_logger, observation);

        return Task.CompletedTask;
    }
}
