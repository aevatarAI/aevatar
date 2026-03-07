using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal interface IWorkflowPrimitivePlanner
{
    bool CanHandle(string stepType);

    Task HandleAsync(StepRequestEvent request, CancellationToken ct);
}

internal sealed class WorkflowPrimitiveExecutionPlanner
{
    private readonly Func<StepRequestEvent, CancellationToken, Task<bool>> _tryHandleRegisteredPrimitiveAsync;
    private readonly IReadOnlyList<IWorkflowPrimitivePlanner> _planners;

    public WorkflowPrimitiveExecutionPlanner(
        Func<StepRequestEvent, CancellationToken, Task<bool>> tryHandleRegisteredPrimitiveAsync,
        IReadOnlyList<IWorkflowPrimitivePlanner> planners)
    {
        _tryHandleRegisteredPrimitiveAsync = tryHandleRegisteredPrimitiveAsync
                                             ?? throw new ArgumentNullException(nameof(tryHandleRegisteredPrimitiveAsync));
        _planners = planners ?? throw new ArgumentNullException(nameof(planners));
    }

    public async Task DispatchAsync(StepRequestEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType);
        foreach (var planner in _planners)
        {
            if (!planner.CanHandle(stepType))
                continue;

            await planner.HandleAsync(request, ct);
            return;
        }

        await _tryHandleRegisteredPrimitiveAsync(request, ct);
    }
}

internal sealed class WorkflowControlFlowPlanner : IWorkflowPrimitivePlanner
{
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleDelayAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleWaitSignalAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleRaceAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleWhileAsync;

    public WorkflowControlFlowPlanner(
        Func<StepRequestEvent, CancellationToken, Task> handleDelayAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleWaitSignalAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleRaceAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleWhileAsync)
    {
        _handleDelayAsync = handleDelayAsync;
        _handleWaitSignalAsync = handleWaitSignalAsync;
        _handleRaceAsync = handleRaceAsync;
        _handleWhileAsync = handleWhileAsync;
    }

    public bool CanHandle(string stepType) =>
        stepType is "delay" or "wait_signal" or "race" or "while";

    public Task HandleAsync(StepRequestEvent request, CancellationToken ct) =>
        WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType) switch
        {
            "delay" => _handleDelayAsync(request, ct),
            "wait_signal" => _handleWaitSignalAsync(request, ct),
            "race" => _handleRaceAsync(request, ct),
            "while" => _handleWhileAsync(request, ct),
            _ => Task.CompletedTask,
        };
}

internal sealed class WorkflowHumanInteractionPlanner : IWorkflowPrimitivePlanner
{
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleHumanInputAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleHumanApprovalAsync;

    public WorkflowHumanInteractionPlanner(
        Func<StepRequestEvent, CancellationToken, Task> handleHumanInputAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleHumanApprovalAsync)
    {
        _handleHumanInputAsync = handleHumanInputAsync;
        _handleHumanApprovalAsync = handleHumanApprovalAsync;
    }

    public bool CanHandle(string stepType) =>
        stepType is "human_input" or "human_approval";

    public Task HandleAsync(StepRequestEvent request, CancellationToken ct) =>
        WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType) switch
        {
            "human_input" => _handleHumanInputAsync(request, ct),
            "human_approval" => _handleHumanApprovalAsync(request, ct),
            _ => Task.CompletedTask,
        };
}

internal sealed class WorkflowAIPlanner : IWorkflowPrimitivePlanner
{
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleLlmCallAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleEvaluateAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleReflectAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleCacheAsync;

    public WorkflowAIPlanner(
        Func<StepRequestEvent, CancellationToken, Task> handleLlmCallAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleEvaluateAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleReflectAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleCacheAsync)
    {
        _handleLlmCallAsync = handleLlmCallAsync;
        _handleEvaluateAsync = handleEvaluateAsync;
        _handleReflectAsync = handleReflectAsync;
        _handleCacheAsync = handleCacheAsync;
    }

    public bool CanHandle(string stepType) =>
        stepType is "llm_call" or "evaluate" or "reflect" or "cache";

    public Task HandleAsync(StepRequestEvent request, CancellationToken ct) =>
        WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType) switch
        {
            "llm_call" => _handleLlmCallAsync(request, ct),
            "evaluate" => _handleEvaluateAsync(request, ct),
            "reflect" => _handleReflectAsync(request, ct),
            "cache" => _handleCacheAsync(request, ct),
            _ => Task.CompletedTask,
        };
}

internal sealed class WorkflowCompositionPlanner : IWorkflowPrimitivePlanner
{
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleParallelAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleForEachAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleMapReduceAsync;
    private readonly Func<StepRequestEvent, CancellationToken, Task> _handleWorkflowCallAsync;

    public WorkflowCompositionPlanner(
        Func<StepRequestEvent, CancellationToken, Task> handleParallelAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleForEachAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleMapReduceAsync,
        Func<StepRequestEvent, CancellationToken, Task> handleWorkflowCallAsync)
    {
        _handleParallelAsync = handleParallelAsync;
        _handleForEachAsync = handleForEachAsync;
        _handleMapReduceAsync = handleMapReduceAsync;
        _handleWorkflowCallAsync = handleWorkflowCallAsync;
    }

    public bool CanHandle(string stepType) =>
        stepType is "parallel" or "foreach" or "map_reduce" or "workflow_call";

    public Task HandleAsync(StepRequestEvent request, CancellationToken ct) =>
        WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType) switch
        {
            "parallel" => _handleParallelAsync(request, ct),
            "foreach" => _handleForEachAsync(request, ct),
            "map_reduce" => _handleMapReduceAsync(request, ct),
            "workflow_call" => _handleWorkflowCallAsync(request, ct),
            _ => Task.CompletedTask,
        };
}
