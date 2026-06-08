using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowRunForkCoordinator : ICommittedStatePublicationHook
{
    private readonly IWorkflowForkRunService _forkRunService;
    private readonly ILogger<WorkflowRunForkCoordinator> _logger;

    public WorkflowRunForkCoordinator(
        IWorkflowForkRunService forkRunService,
        ILogger<WorkflowRunForkCoordinator>? logger = null)
    {
        _forkRunService = forkRunService ?? throw new ArgumentNullException(nameof(forkRunService));
        _logger = logger ?? NullLogger<WorkflowRunForkCoordinator>.Instance;
    }

    public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.Published.StateEvent?.EventData?.Is(WorkflowRunForkRequestedEvent.Descriptor) != true)
            return;

        var requested = context.Published.StateEvent.EventData.Unpack<WorkflowRunForkRequestedEvent>();
        if (string.IsNullOrWhiteSpace(requested.SourceRunId) ||
            string.IsNullOrWhiteSpace(requested.StartAtStepId))
        {
            _logger.LogWarning(
                "Ignoring workflow fork request with missing source or step. source={SourceRunId} step={StartAtStepId}",
                requested.SourceRunId,
                requested.StartAtStepId);
            return;
        }

        try
        {
            var result = await _forkRunService.ForkAsync(
                new WorkflowForkRunCommand(
                    SourceRunId: requested.SourceRunId,
                    StartAtStepId: requested.StartAtStepId,
                    InlineYaml: null,
                    Attempt: Math.Max(0, requested.Attempt)),
                ct).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Workflow fork request failed. source={SourceRunId} step={StartAtStepId} attempt={Attempt} code={ErrorCode} reason={Reason}",
                    requested.SourceRunId,
                    requested.StartAtStepId,
                    requested.Attempt,
                    result.Error?.Code,
                    result.Error?.Reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Workflow fork coordinator failed. source={SourceRunId} step={StartAtStepId} attempt={Attempt}",
                requested.SourceRunId,
                requested.StartAtStepId,
                requested.Attempt);
        }
    }
}
