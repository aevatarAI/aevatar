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
        var workflowContext = WorkflowExecutionContextAdapter.Create(ctx, _stateHost);
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

            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                workflowContext,
                WorkflowExecutionKernel.ModuleStateKey);
            if (WorkflowExecutionValueStore.IsNormalized(kernelState) &&
                !string.Equals(
                    envelope.Route?.PublisherActorId,
                    ctx.AgentId,
                    StringComparison.Ordinal))
            {
                ctx.Logger.LogWarning(
                    "workflow_execution_bridge: reject non-self normalized step request actor={ActorId} publisher={PublisherActorId} step={StepId}",
                    ctx.AgentId,
                    envelope.Route?.PublisherActorId ?? string.Empty,
                    request.StepId);
                return;
            }
            if (WorkflowExecutionValueStore.PrepareInternalDispatch(
                    kernelState,
                    request,
                    envelope.Id))
            {
                await WorkflowExecutionStateAccess.SaveAsync(
                    workflowContext,
                    WorkflowExecutionKernel.ModuleStateKey,
                    kernelState,
                    ct);
                envelope.Payload = Google.Protobuf.WellKnownTypes.Any.Pack(request);
            }
        }

        // The kernel is the sole authority that admits a normalized
        // completion.  A completion rejected by the kernel must not continue
        // through this bridge into composite modules, otherwise a stale child
        // can still decrement counters or resolve a newer parent attempt.
        if (envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true)
        {
            var completion = envelope.Payload.Unpack<StepCompletedEvent>();
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                workflowContext,
                WorkflowExecutionKernel.ModuleStateKey);
            if (WorkflowExecutionValueStore.IsNormalized(kernelState))
            {
                var selfPublished = string.Equals(
                    envelope.Route?.PublisherActorId,
                    ctx.AgentId,
                    StringComparison.Ordinal);
                if (!selfPublished ||
                    !WorkflowExecutionValueStore.IsAcceptedCompletion(kernelState, completion))
                {
                    ctx.Logger.LogWarning(
                        "workflow_execution_bridge: reject non-authoritative normalized completion actor={ActorId} publisher={PublisherActorId} step={StepId} execution={ExecutionId}",
                        ctx.AgentId,
                        envelope.Route?.PublisherActorId ?? string.Empty,
                        completion.StepId,
                        completion.ExecutionId);
                    return;
                }
            }
        }

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
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
                    },
                    TopologyAudience.Self,
                    ct);
                return;
            }
        }
    }
}
