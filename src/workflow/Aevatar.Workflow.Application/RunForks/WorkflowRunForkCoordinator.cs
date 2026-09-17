using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowRunForkCoordinator : ICommittedStatePublicationHook
{
    private readonly Lazy<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>> _forkDispatchService;
    private readonly ILogger<WorkflowRunForkCoordinator> _logger;

    public WorkflowRunForkCoordinator(
        Lazy<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>> forkDispatchService,
        ILogger<WorkflowRunForkCoordinator>? logger = null)
    {
        _forkDispatchService = forkDispatchService ?? throw new ArgumentNullException(nameof(forkDispatchService));
        _logger = logger ?? NullLogger<WorkflowRunForkCoordinator>.Instance;
    }

    internal WorkflowRunForkCoordinator(
        ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError> forkDispatchService,
        ILogger<WorkflowRunForkCoordinator>? logger = null)
        : this(new Lazy<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>>(
            () => forkDispatchService ?? throw new ArgumentNullException(nameof(forkDispatchService))),
            logger)
    {
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
            var result = await _forkDispatchService.Value.DispatchAsync(
                new WorkflowForkRunCommand(
                    SourceRunId: requested.SourceRunId,
                    StartAtStepId: requested.StartAtStepId,
                    InlineYaml: null,
                    Attempt: Math.Max(0, requested.Attempt),
                    ScopeId: requested.ScopeId),
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
                return;
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
