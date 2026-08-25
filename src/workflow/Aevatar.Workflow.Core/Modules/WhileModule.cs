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
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            var whileKey = BuildParentAttemptKey(runId, request.StepId, request.ExecutionId, normalizedValues);
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
                SubExternalInvocation = request.ExternalInvocation?.Clone(),
                ParentExecutionId = request.ExecutionId,
                InputValueId = request.InputValueId,
            };
            foreach (var (key, value) in subParameters)
                state.SubParameters[key] = value;
            var runtimeState = WorkflowExecutionStateAccess.Load<WhileModuleState>(ctx, ModuleStateKey);
            RemoveSupersededLoopAttempts(runtimeState, runId, request.StepId, whileKey);
            SetExpectedChildIdentity(state);
            runtimeState.ChildToLoopKey[state.ExpectedChildStepId] = whileKey;
            runtimeState.Loops[whileKey] = state;
            await SaveStateAsync(runtimeState, ctx, ct);

            ctx.Logger.LogInformation(
                "While 循环 {StepId}: 开始，max_iterations={Max}, condition={Condition}",
                request.StepId,
                maxIterations,
                condition);

            await DispatchIterationAsync(
                state,
                request.Input ?? string.Empty,
                request.InputValueId,
                ctx,
                ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var completed = payload.Unpack<StepCompletedEvent>();

            // 找到对应的 while 步骤
            var whileStepId = GetWhileStepId(completed.StepId);
            var runId = WorkflowRunIdNormalizer.Normalize(completed.RunId);
            var runtimeState = WorkflowExecutionStateAccess.Load<WhileModuleState>(ctx, ModuleStateKey);
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            var whileKey = runtimeState.ChildToLoopKey.TryGetValue(completed.StepId, out var mappedLoopKey)
                ? mappedLoopKey
                : whileStepId == null ? null : BuildRunStepKey(runId, whileStepId);
            if (whileStepId == null || whileKey == null || !runtimeState.Loops.TryGetValue(whileKey, out var state)) return;
            if (!MatchesExpectedChild(state, completed, normalizedValues)) return;

            if (state.PendingCompletion != null)
            {
                await PublishPendingCompletionAsync(runtimeState, whileKey, state, completed, ctx, ct);
                return;
            }

            if (!completed.Success)
            {
                if (!normalizedValues)
                {
                    runtimeState.Loops.Remove(whileKey);
                    runtimeState.ChildToLoopKey.Remove(state.ExpectedChildStepId);
                    await SaveStateAsync(runtimeState, ctx, ct);
                    await ctx.PublishAsync(new StepCompletedEvent
                    {
                        StepId = state.StepId,
                        RunId = state.RunId,
                        ExecutionId = state.ParentExecutionId,
                        Success = false,
                        Error = completed.Error,
                        Output = completed.Output,
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
                    }, TopologyAudience.Self, ct);
                    return;
                }

                state.PendingCompletion = new StepCompletedEvent
                {
                    StepId = state.StepId,
                    RunId = state.RunId,
                    ExecutionId = state.ParentExecutionId,
                    Success = false,
                    Error = completed.Error,
                    Output = completed.Output,
                    OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput,
                };
                if (normalizedValues)
                {
                    await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                        state.PendingCompletion,
                        completed,
                        ctx,
                        ct);
                }
                runtimeState.Loops[whileKey] = state;
                await SaveStateAsync(runtimeState, ctx, ct);
                await PublishPendingCompletionAsync(runtimeState, whileKey, state, completed, ctx, ct);
                return;
            }

            var nextIteration = state.Iteration + 1;
            var shouldContinue = nextIteration < state.MaxIterations &&
                                 EvaluateCondition(state, completed.Output ?? string.Empty, nextIteration);

            if (shouldContinue)
            {
                var nextState = state.Clone();
                nextState.Iteration = nextIteration;
                var nextInputValueId = WorkflowExecutionValueStore.GetCompletedStepOutputValueId(
                    kernelState,
                    completed.StepId);
                nextState.InputValueId = nextInputValueId ?? string.Empty;
                runtimeState.ChildToLoopKey.Remove(state.ExpectedChildStepId);
                SetExpectedChildIdentity(nextState);
                runtimeState.ChildToLoopKey[nextState.ExpectedChildStepId] = whileKey;
                runtimeState.Loops[whileKey] = nextState;
                await SaveStateAsync(runtimeState, ctx, ct);

                ctx.Logger.LogInformation("While 循环 {StepId}: 迭代 {Iter}/{Max}",
                    whileStepId, nextIteration, state.MaxIterations);

                await DispatchIterationAsync(
                    nextState,
                    completed.Output ?? string.Empty,
                    nextInputValueId,
                    ctx,
                    ct);
            }
            else
            {
                ctx.Logger.LogInformation(
                    "While 循环 {StepId}: 完成，iteration={Iter}/{Max}, condition={Condition}",
                    whileStepId,
                    nextIteration,
                    state.MaxIterations,
                    state.ConditionExpression);

                var parentCompletion = new StepCompletedEvent
                {
                    StepId = whileStepId,
                    RunId = state.RunId,
                    ExecutionId = state.ParentExecutionId,
                    Success = true,
                    Output = completed.Output,
                    OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput,
                };
                parentCompletion.Annotations["while.iterations"] = nextIteration.ToString();
                parentCompletion.Annotations["while.max_iterations"] = state.MaxIterations.ToString();
                parentCompletion.Annotations["while.condition"] = state.ConditionExpression;
                if (!normalizedValues)
                {
                    parentCompletion.OutputProvenance = WorkflowStepOutputProvenance.Produced;
                    runtimeState.Loops.Remove(whileKey);
                    runtimeState.ChildToLoopKey.Remove(state.ExpectedChildStepId);
                    await SaveStateAsync(runtimeState, ctx, ct);
                    await ctx.PublishAsync(parentCompletion, TopologyAudience.Self, ct);
                    return;
                }

                state.PendingCompletion = parentCompletion;
                if (normalizedValues)
                {
                    await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                        state.PendingCompletion,
                        completed,
                        ctx,
                        ct);
                }
                runtimeState.Loops[whileKey] = state;
                await SaveStateAsync(runtimeState, ctx, ct);
                await PublishPendingCompletionAsync(runtimeState, whileKey, state, completed, ctx, ct);
            }
        }
    }

    private static async Task PublishPendingCompletionAsync(
        WhileModuleState runtimeState,
        string whileKey,
        WhileRuntimeState state,
        StepCompletedEvent source,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var completion = state.PendingCompletion
            ?? throw new InvalidOperationException($"While state '{whileKey}' has no pending completion.");
        if (string.IsNullOrWhiteSpace(completion.OutputReferenceId))
        {
            if (!string.IsNullOrWhiteSpace(state.ExpectedChildStepId) &&
                (!string.Equals(state.ExpectedChildStepId, source.StepId, StringComparison.Ordinal) ||
                 !string.Equals(state.ExpectedChildExecutionId, source.ExecutionId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"While '{whileKey}' recovery received a non-current child completion.");
            }
            await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(completion, source, ctx, ct);
            runtimeState.Loops[whileKey] = state;
            await SaveStateAsync(runtimeState, ctx, ct);
        }

        await ctx.PublishAsync(
            WorkflowExecutionValueStore.HydrateReferencedCompletionForPublication(completion, ctx),
            TopologyAudience.Self,
            ct);
        runtimeState.Loops.Remove(whileKey);
        runtimeState.ChildToLoopKey.Remove(state.ExpectedChildStepId);
        await SaveStateAsync(runtimeState, ctx, CancellationToken.None);
    }

    private static string? GetWhileStepId(string subStepId)
    {
        var idx = subStepId.LastIndexOf("_iter_", StringComparison.Ordinal);
        if (idx <= 0) return null;
        var suffix = subStepId[(idx + "_iter_".Length)..];
        return suffix.All(char.IsDigit) ? subStepId[..idx] : null;
    }

    private static string BuildRunStepKey(string runId, string stepId) => $"{runId}:{stepId}";

    private static string BuildParentAttemptKey(
        string runId,
        string stepId,
        string executionId,
        bool normalized) =>
        normalized
            ? $"{BuildRunStepKey(runId, stepId)}:execution:{executionId.Trim()}"
            : BuildRunStepKey(runId, stepId);

    private static void SetExpectedChildIdentity(WhileRuntimeState state)
    {
        state.ExpectedChildStepId = $"{state.StepId}_iter_{state.Iteration}";
        state.ExpectedChildExecutionId = WorkflowExecutionValueStore.BuildInternalExecutionId(
            state.RunId,
            state.ParentExecutionId,
            state.ExpectedChildStepId);
    }

    private static bool MatchesExpectedChild(
        WhileRuntimeState state,
        StepCompletedEvent completion,
        bool normalized)
    {
        if (!string.Equals(
                WorkflowRunIdNormalizer.Normalize(state.RunId),
                WorkflowRunIdNormalizer.Normalize(completion.RunId),
                StringComparison.Ordinal))
            return false;
        return !normalized ||
               (string.Equals(state.ExpectedChildStepId, completion.StepId, StringComparison.Ordinal) &&
                string.Equals(state.ExpectedChildExecutionId, completion.ExecutionId, StringComparison.Ordinal));
    }

    private static void RemoveSupersededLoopAttempts(
        WhileModuleState state,
        string runId,
        string stepId,
        string currentKey)
    {
        foreach (var (key, loop) in state.Loops.ToArray())
        {
            if (string.Equals(key, currentKey, StringComparison.Ordinal) ||
                !string.Equals(loop.RunId, runId, StringComparison.Ordinal) ||
                !string.Equals(loop.StepId, stepId, StringComparison.Ordinal))
                continue;
            state.Loops.Remove(key);
            state.ChildToLoopKey.Remove(loop.ExpectedChildStepId);
        }
    }

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
        string? inputValueId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var request = new StepRequestEvent
        {
            StepId = state.ExpectedChildStepId,
            StepType = state.SubStepType,
            RunId = state.RunId,
            Input = input,
            TargetRole = state.SubTargetRole,
            ExternalInvocation = state.SubExternalInvocation?.Clone(),
            ExecutionId = state.ExpectedChildExecutionId,
            InputValueId = inputValueId ?? string.Empty,
        };
        var vars = BuildIterationVariables(input, state.Iteration, state.MaxIterations);
        foreach (var (key, value) in state.SubParameters)
            request.Parameters[key] = _expressionEvaluator.Evaluate(value, vars);

        // 迭代子步骤由本 run 的模块管线消费（与 foreach / map_reduce 一致）。
        // 投递到 Children 时没有子 actor 承接，while 步骤会永远挂起。
        await ctx.PublishAsync(request, TopologyAudience.Self, ct);
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
