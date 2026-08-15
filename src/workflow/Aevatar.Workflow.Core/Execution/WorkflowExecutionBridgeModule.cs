using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Execution;

internal interface IWorkflowExecutionBackgroundWorkOwner
{
    void CancelBackgroundWork();
}

internal sealed class WorkflowExecutionBridgeModule : IEventModule<IEventHandlerContext>
{
    private readonly IReadOnlyList<IEventModule<IWorkflowExecutionContext>> _executors;
    private readonly IWorkflowExecutionStateHost _stateHost;

    public WorkflowExecutionBridgeModule(
        IEnumerable<IEventModule<IWorkflowExecutionContext>> executors,
        IWorkflowExecutionStateHost stateHost)
    {
        _stateHost = stateHost ?? throw new ArgumentNullException(nameof(stateHost));
        _executors = executors
            .OrderBy(x => x.Priority)
            .ToArray();
    }

    public string Name => "workflow_execution_bridge";

    // The kernel must inspect child completions before foreach mutates its attempt ledger.
    public int Priority => 1;

    internal void CancelBackgroundWork()
    {
        foreach (var owner in _executors.OfType<IWorkflowExecutionBackgroundWorkOwner>())
            owner.CancelBackgroundWork();
    }

    public bool CanHandle(EventEnvelope envelope) =>
        _executors.Any(x => x.CanHandle(envelope));

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload?.Is(StepRequestEvent.Descriptor) == true)
        {
            var request = envelope.Payload.Unpack<StepRequestEvent>();
            if (!WorkflowExecutionStateAccess.MatchesAuthoritativeRun(_stateHost.RunId, request.RunId))
            {
                ctx.Logger.LogWarning(
                    "workflow_execution_bridge: ignore fenced step request currentRun={CurrentRunId} requestedRun={RequestedRunId} step={StepId}",
                    _stateHost.RunId,
                    request.RunId,
                    request.StepId);
                return;
            }
        }

        var workflowContext = WorkflowExecutionContextAdapter.Create(ctx, _stateHost);
        foreach (var executor in _executors)
        {
            if (!executor.CanHandle(envelope))
                continue;

            try
            {
                await executor.HandleAsync(envelope, workflowContext, ct);
            }
            catch (Exception ex) when (
                ex is IRuntimeEnvelopeRetryableException ||
                WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(ex))
            {
                ctx.Logger.LogWarning(
                    ex,
                    "workflow_execution_bridge: executor requires runtime redelivery run={RunId}",
                    _stateHost.RunId);
                throw;
            }
            catch (WorkflowDurablePublicationPendingException ex)
            {
                ctx.Logger.LogWarning(
                    ex,
                    "workflow_execution_bridge: durable executor publication remains pending run={RunId}",
                    _stateHost.RunId);
                throw;
            }
            catch (Exception ex) when (envelope.Payload?.Is(StepRequestEvent.Descriptor) == true)
            {
                var request = envelope.Payload.Unpack<StepRequestEvent>();
                ctx.Logger.LogError(
                    ex,
                    "workflow_execution_bridge: executor failed run={RunId} step={StepId} type={StepType}",
                    request.RunId,
                    request.StepId,
                    request.StepType);
                await ctx.PublishAsync(
                    new StepCompletedEvent
                    {
                        RunId = request.RunId,
                        StepId = request.StepId,
                        ExecutionId = request.ExecutionId,
                        Success = false,
                        FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                        Error = WorkflowRuntimeFailureMessages.StepExecutorFailed(request.StepId, request.StepType, ex),
                    },
                    TopologyAudience.Self,
                    ct);
                return;
            }
        }
    }
}
