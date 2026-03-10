// ─────────────────────────────────────────────────────────────
// WhileModule — 循环模块
// 重复执行子步骤直到条件不满足或达到最大迭代次数
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>循环模块。处理 type=while 的步骤。</summary>
public sealed class WhileModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "while";
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();

    public string Name => "while";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "while") return;

            var maxIterations = int.TryParse(request.Parameters.GetValueOrDefault("max_iterations", "10"), out var max)
                ? Math.Clamp(max, 1, 1_000_000)
                : 10;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var whileKey = BuildRunStepKey(runId, request.StepId);
            var subStepType = request.Parameters.GetValueOrDefault("step", "llm_call");
            var condition = request.Parameters.GetValueOrDefault("condition", "true");
            if (string.IsNullOrWhiteSpace(condition))
                condition = "true";

            var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.Parameters)
            {
                if (key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase))
                    subParameters[key["sub_param_".Length..]] = value;
            }

            var state = new WhileRuntimeState
            {
                StepId = request.StepId,
                RunId = runId,
                SubStepType = subStepType,
                SubTargetRole = request.TargetRole ?? string.Empty,
                Iteration = 0,
                MaxIterations = maxIterations,
                ConditionExpression = condition,
            };
            foreach (var (key, value) in subParameters)
                state.SubParameters[key] = value;
            var runtimeState = WorkflowExecutionStateAccess.Load<WhileModuleState>(ctx, ModuleStateKey);
            runtimeState.Loops[whileKey] = state;
            await SaveStateAsync(runtimeState, ctx, ct);

            ctx.Logger.LogInformation(
                "While 循环 {StepId}: 开始，max_iterations={Max}, condition={Condition}",
                request.StepId,
                maxIterations,
                condition);

            await DispatchIterationAsync(state, request.Input ?? string.Empty, ctx, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var completed = payload.Unpack<StepCompletedEvent>();

            // 找到对应的 while 步骤
            var whileStepId = GetWhileStepId(completed.StepId);
            var runId = WorkflowRunIdNormalizer.Normalize(completed.RunId);
            var whileKey = whileStepId == null ? null : BuildRunStepKey(runId, whileStepId);
            var runtimeState = WorkflowExecutionStateAccess.Load<WhileModuleState>(ctx, ModuleStateKey);
            if (whileStepId == null || whileKey == null || !runtimeState.Loops.TryGetValue(whileKey, out var state)) return;

            if (!completed.Success)
            {
                runtimeState.Loops.Remove(whileKey);
                await SaveStateAsync(runtimeState, ctx, ct);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = state.StepId,
                    RunId = state.RunId,
                    Success = false,
                    Error = completed.Error,
                    Output = completed.Output,
                }, EventDirection.Self, ct);
                return;
            }

            var nextIteration = state.Iteration + 1;
            var shouldContinue = nextIteration < state.MaxIterations &&
                                 EvaluateCondition(state, completed.Output ?? string.Empty, nextIteration);

            if (shouldContinue)
            {
                var nextState = state.Clone();
                nextState.Iteration = nextIteration;
                runtimeState.Loops[whileKey] = nextState;
                await SaveStateAsync(runtimeState, ctx, ct);

                ctx.Logger.LogInformation("While 循环 {StepId}: 迭代 {Iter}/{Max}",
                    whileStepId, nextIteration, state.MaxIterations);

                await DispatchIterationAsync(nextState, completed.Output ?? string.Empty, ctx, ct);
            }
            else
            {
                runtimeState.Loops.Remove(whileKey);
                await SaveStateAsync(runtimeState, ctx, ct);
                ctx.Logger.LogInformation(
                    "While 循环 {StepId}: 完成，iteration={Iter}/{Max}, condition={Condition}",
                    whileStepId,
                    nextIteration,
                    state.MaxIterations,
                    state.ConditionExpression);

                var parentCompleted = new StepCompletedEvent
                {
                    StepId = whileStepId,
                    RunId = state.RunId,
                    Success = true,
                    Output = completed.Output,
                };
                parentCompleted.Annotations["while.iterations"] = nextIteration.ToString();
                parentCompleted.Annotations["while.max_iterations"] = state.MaxIterations.ToString();
                parentCompleted.Annotations["while.condition"] = state.ConditionExpression;
                await ctx.PublishAsync(parentCompleted, EventDirection.Self, ct);
            }
        }
    }

    private static string? GetWhileStepId(string subStepId)
    {
        var idx = subStepId.LastIndexOf("_iter_", StringComparison.Ordinal);
        if (idx <= 0) return null;
        var suffix = subStepId[(idx + "_iter_".Length)..];
        return suffix.All(char.IsDigit) ? subStepId[..idx] : null;
    }

    private static string BuildRunStepKey(string runId, string stepId) => $"{runId}:{stepId}";

    private bool EvaluateCondition(WhileRuntimeState state, string output, int nextIteration)
    {
        var vars = BuildIterationVariables(output, nextIteration, state.MaxIterations);
        var eval = state.ConditionExpression.Contains("${", StringComparison.Ordinal)
            ? _expressionEvaluator.Evaluate(state.ConditionExpression, vars)
            : _expressionEvaluator.EvaluateExpression(state.ConditionExpression, vars);
        return IsTruthy(eval);
    }

    private async Task DispatchIterationAsync(
        WhileRuntimeState state,
        string input,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var request = new StepRequestEvent
        {
            StepId = $"{state.StepId}_iter_{state.Iteration}",
            StepType = state.SubStepType,
            RunId = state.RunId,
            Input = input,
            TargetRole = state.SubTargetRole,
        };
        var vars = BuildIterationVariables(input, state.Iteration, state.MaxIterations);
        foreach (var (key, value) in state.SubParameters)
            request.Parameters[key] = _expressionEvaluator.Evaluate(value, vars);

        await ctx.PublishAsync(request, EventDirection.Down, ct);
    }

    private static Dictionary<string, string> BuildIterationVariables(string input, int iteration, int maxIterations) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["output"] = input,
            ["input"] = input,
            ["iteration"] = iteration.ToString(),
            ["max_iterations"] = maxIterations.ToString(),
        };

    private static bool IsTruthy(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (bool.TryParse(value, out var boolValue))
            return boolValue;
        if (double.TryParse(value, out var number))
            return Math.Abs(number) >= 1e-9;
        return true;
    }

    private static Task SaveStateAsync(
        WhileModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Loops.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }
}
