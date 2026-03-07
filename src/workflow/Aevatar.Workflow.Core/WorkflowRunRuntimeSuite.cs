using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunRuntimeSuite
{
    private readonly WorkflowRunDispatchRuntime _dispatchRuntime;
    private readonly WorkflowRunAsyncPolicyRuntime _asyncPolicyRuntime;
    private readonly WorkflowChildRunCompletionRegistry _childRunCompletionRegistry;

    public WorkflowRunRuntimeSuite(
        WorkflowRunRuntimeContext context,
        WorkflowRunStepRequestFactory stepRequestFactory,
        WorkflowExpressionEvaluator expressionEvaluator,
        Func<bool, string, string, CancellationToken, Task> finalizeRunAsync)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stepRequestFactory);
        ArgumentNullException.ThrowIfNull(expressionEvaluator);
        ArgumentNullException.ThrowIfNull(finalizeRunAsync);

        _dispatchRuntime = new WorkflowRunDispatchRuntime(context, stepRequestFactory, expressionEvaluator);

        var controlFlowRuntime = new WorkflowRunControlFlowRuntime(context, _dispatchRuntime);
        var humanInteractionRuntime = new WorkflowRunHumanInteractionRuntime(context);
        var llmRuntime = new WorkflowRunLlmRuntime(context);
        var evaluationRuntime = new WorkflowRunEvaluationRuntime(context);
        var reflectRuntime = new WorkflowRunReflectRuntime(context);
        var cacheRuntime = new WorkflowRunCacheRuntime(context, _dispatchRuntime);
        var fanOutRuntime = new WorkflowRunFanOutRuntime(context, _dispatchRuntime);
        var subWorkflowRuntime = new WorkflowRunSubWorkflowRuntime(context);
        var timeoutCallbackRuntime = new WorkflowRunTimeoutCallbackRuntime(context, _dispatchRuntime);
        var aiResponseRuntime = new WorkflowRunAIResponseRuntime(llmRuntime, evaluationRuntime, reflectRuntime);
        var aggregationCompletionRuntime = new WorkflowRunAggregationCompletionRuntime(context, _dispatchRuntime);
        var progressionCompletionRuntime = new WorkflowRunProgressionCompletionRuntime(context, _dispatchRuntime, stepRequestFactory);

        _asyncPolicyRuntime = new WorkflowRunAsyncPolicyRuntime(context, _dispatchRuntime, finalizeRunAsync);

        StepFamilyDispatchTable = new WorkflowStepFamilyDispatchTable(
        [
            controlFlowRuntime,
            humanInteractionRuntime,
            llmRuntime,
            evaluationRuntime,
            reflectRuntime,
            cacheRuntime,
            fanOutRuntime,
            subWorkflowRuntime,
        ]);
        StatefulCompletionHandlers = new WorkflowStatefulCompletionHandlerRegistry(
        [
            aggregationCompletionRuntime,
            progressionCompletionRuntime,
        ]);
        InternalSignalHandlers = new WorkflowInternalSignalRegistry(
        [
            timeoutCallbackRuntime,
            llmRuntime,
        ]);
        ResponseHandlers = new WorkflowResponseHandlerRegistry([aiResponseRuntime]);
        _childRunCompletionRegistry = new WorkflowChildRunCompletionRegistry([subWorkflowRuntime]);
    }

    public WorkflowStepFamilyDispatchTable StepFamilyDispatchTable { get; }

    public WorkflowStatefulCompletionHandlerRegistry StatefulCompletionHandlers { get; }

    public WorkflowInternalSignalRegistry InternalSignalHandlers { get; }

    public WorkflowResponseHandlerRegistry ResponseHandlers { get; }

    public Task DispatchWorkflowStepAsync(
        StepDefinition step,
        string input,
        string runId,
        CancellationToken ct) =>
        _dispatchRuntime.DispatchWorkflowStepAsync(step, input, runId, ct);

    public async Task<bool> TryHandleFailureAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowRunState next,
        CancellationToken ct)
    {
        if (await _asyncPolicyRuntime.TryScheduleRetryAsync(step, evt, next, ct))
            return true;

        return await _asyncPolicyRuntime.TryHandleOnErrorAsync(step, evt, next, ct);
    }

    public Task<bool> TryHandleChildRunCompletionAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        CancellationToken ct) =>
        _childRunCompletionRegistry.TryHandleAsync(evt, publisherActorId, ct);
}
