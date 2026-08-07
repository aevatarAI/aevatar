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
    private readonly IActorDispatchPort? _dispatchPort;
    private readonly ILogger<WorkflowRunForkCoordinator> _logger;

    public WorkflowRunForkCoordinator(
        Lazy<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>> forkDispatchService,
        IActorDispatchPort? dispatchPort = null,
        ILogger<WorkflowRunForkCoordinator>? logger = null)
    {
        _forkDispatchService = forkDispatchService ?? throw new ArgumentNullException(nameof(forkDispatchService));
        _dispatchPort = dispatchPort;
        _logger = logger ?? NullLogger<WorkflowRunForkCoordinator>.Instance;
    }

    internal WorkflowRunForkCoordinator(
        ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError> forkDispatchService,
        ILogger<WorkflowRunForkCoordinator>? logger = null)
        : this(new Lazy<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>>(
            () => forkDispatchService ?? throw new ArgumentNullException(nameof(forkDispatchService))),
            dispatchPort: null,
            logger: logger)
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

            await RecordAcceptedForkLineageAsync(context, requested, result.Receipt, ct)
                .ConfigureAwait(false);
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

    private async Task RecordAcceptedForkLineageAsync(
        CommittedStatePublicationContext context,
        WorkflowRunForkRequestedEvent requested,
        WorkflowForkRunAcceptedReceipt? receipt,
        CancellationToken ct)
    {
        if (_dispatchPort == null || receipt == null || !receipt.Accepted)
            return;

        var sourceActorId = context.ActorId?.Trim() ?? string.Empty;
        var childRunId = receipt.NewRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceActorId) || string.IsNullOrWhiteSpace(childRunId))
            return;

        // Implement (issue #3252):
        //   Behavior: source runs record accepted fork children with routable runId and separate child actor address.
        //   Why this shape: the coordinator only relays the accepted child identity back to the source actor; the source actor commits the lineage fact.
        await _dispatchPort.DispatchAsync(
            sourceActorId,
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(new WorkflowRunLineageRecordedEvent
                {
                    SourceRunId = requested.SourceRunId ?? string.Empty,
                    ChildRunId = childRunId,
                    ChildActorId = receipt.NewRunActorId ?? string.Empty,
                    StartAtStepId = requested.StartAtStepId ?? string.Empty,
                    Attempt = Math.Max(0, requested.Attempt),
                    RelationKind = WorkflowRunLineageRelationKind.RetryFork,
                    OriginalRunId = string.IsNullOrWhiteSpace(receipt.OriginalRunId)
                        ? requested.SourceRunId ?? string.Empty
                        : receipt.OriginalRunId,
                }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(sourceActorId, TopologyAudience.Self),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = Guid.NewGuid().ToString("N"),
                },
            },
            ct).ConfigureAwait(false);
    }
}
