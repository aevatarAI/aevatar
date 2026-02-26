// ─────────────────────────────────────────────────────────────
// WhileModule — 循环模块
// 重复执行子步骤序列直到条件不满足或达到最大迭代次数
//
// If the step definition has Children, each iteration dispatches
// them sequentially (e.g. verify → build_dag → next_round).
// Otherwise falls back to dispatching a single step type.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>循环模块。处理 type=while 的步骤。</summary>
public sealed class WhileModule : IEventModule
{
    private WorkflowDefinition? _workflow;
    private readonly Dictionary<string, WhileState> _activeLoops = [];
    private readonly Dictionary<string, string> _pendingChildren = []; // run:childStepId -> run:whileStepId
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();

    public string Name => "while";
    public int Priority => 5;

    /// <summary>设置工作流定义，用于查找步骤的 Children。</summary>
    public void SetWorkflow(WorkflowDefinition workflow) => _workflow = workflow;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "while") return;

            var maxIterations = int.TryParse(
                request.Parameters.GetValueOrDefault("max_iterations", "10"), out var max)
                ? Math.Clamp(max, 1, 1_000_000)
                : 10;

            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var whileKey = BuildRunStepKey(runId, request.StepId);
            var condition = request.Parameters.GetValueOrDefault("condition", "true");
            if (string.IsNullOrWhiteSpace(condition))
                condition = "true";

            var children = ResolveChildren(request.StepId);
            if (children == null || children.Count == 0)
            {
                // Fallback: no children defined — dispatch a single step type per iteration.
                var subStepType = request.Parameters.GetValueOrDefault("step", "llm_call");
                var subParameters = ExtractSubParameters(request.Parameters);

                children =
                [
                    new StepDefinition
                    {
                        Id = "",
                        Type = subStepType,
                        TargetRole = request.TargetRole,
                        Parameters = subParameters,
                    },
                ];
            }

            var state = new WhileState
            {
                StepId = request.StepId,
                RunId = runId,
                ConditionExpression = condition,
                MaxIterations = maxIterations,
                CurrentIteration = 0,
                CurrentChildIndex = 0,
                Children = children,
                Input = request.Input ?? string.Empty,
                FallbackRole = request.TargetRole ?? string.Empty,
            };

            _activeLoops[whileKey] = state;

            ctx.Logger.LogInformation(
                "While {StepId}: start, run={RunId}, max={Max}, children={Count}, condition={Condition}",
                request.StepId,
                runId,
                maxIterations,
                children.Count,
                condition);

            await DispatchCurrentChild(whileKey, state, ctx, ct);
            return;
        }

        if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var completed = payload.Unpack<StepCompletedEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(completed.RunId);
            var childKey = BuildRunStepKey(runId, completed.StepId);

            // Match this completion to an active while loop's pending child.
            if (!_pendingChildren.TryGetValue(childKey, out var whileKey)) return;
            _pendingChildren.Remove(childKey);

            if (!_activeLoops.TryGetValue(whileKey, out var state)) return;

            if (!completed.Success)
            {
                _activeLoops.Remove(whileKey);
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

            // Carry forward output to next child.
            state.Input = completed.Output ?? string.Empty;
            state.CurrentChildIndex++;

            if (state.CurrentChildIndex < state.Children.Count)
            {
                await DispatchCurrentChild(whileKey, state, ctx, ct);
                return;
            }

            // All children in this iteration completed.
            state.CurrentIteration++;

            var shouldContinue = state.CurrentIteration < state.MaxIterations &&
                                 EvaluateCondition(state, state.Input, state.CurrentIteration);

            if (shouldContinue)
            {
                state.CurrentChildIndex = 0;
                ctx.Logger.LogInformation(
                    "While {StepId}: iteration {Iter}/{Max} starting",
                    state.StepId,
                    state.CurrentIteration,
                    state.MaxIterations);

                await DispatchCurrentChild(whileKey, state, ctx, ct);
                return;
            }

            _activeLoops.Remove(whileKey);
            ctx.Logger.LogInformation(
                "While {StepId}: completed after {Iter} iterations",
                state.StepId,
                state.CurrentIteration);

            var parentCompleted = new StepCompletedEvent
            {
                StepId = state.StepId,
                RunId = state.RunId,
                Success = true,
                Output = state.Input,
            };
            parentCompleted.Metadata["while.iterations"] = state.CurrentIteration.ToString();
            parentCompleted.Metadata["while.max_iterations"] = state.MaxIterations.ToString();
            parentCompleted.Metadata["while.condition"] = state.ConditionExpression;
            await ctx.PublishAsync(parentCompleted, EventDirection.Self, ct);
        }
    }

    private static Dictionary<string, string> ExtractSubParameters(IDictionary<string, string> parameters)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            if (key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase))
                result[key["sub_param_".Length..]] = value;
        }

        return result;
    }

    private List<StepDefinition>? ResolveChildren(string stepId)
    {
        if (_workflow == null) return null;
        var step = _workflow.GetStep(stepId);
        return step?.Children is { Count: > 0 } ? step.Children : null;
    }

    private async Task DispatchCurrentChild(
        string whileKey,
        WhileState state,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var child = state.Children[state.CurrentChildIndex];
        var childStepId = string.IsNullOrEmpty(child.Id)
            ? $"{state.StepId}_iter_{state.CurrentIteration}"
            : $"{state.StepId}_iter_{state.CurrentIteration}_{child.Id}";

        var childKey = BuildRunStepKey(state.RunId, childStepId);
        _pendingChildren[childKey] = whileKey;

        var req = new StepRequestEvent
        {
            StepId = childStepId,
            StepType = child.Type,
            RunId = state.RunId,
            Input = state.Input,
            TargetRole = child.TargetRole ?? state.FallbackRole,
        };

        var vars = BuildIterationVariables(state.Input, state.CurrentIteration, state.MaxIterations);
        foreach (var (k, v) in child.Parameters)
        {
            req.Parameters[k] = v.Contains("${", StringComparison.Ordinal)
                ? _expressionEvaluator.Evaluate(v, vars)
                : v;
        }

        // Inject allowed_connectors from role definition.
        if (!string.IsNullOrWhiteSpace(req.TargetRole) && _workflow != null)
        {
            var role = _workflow.Roles.FirstOrDefault(
                r => string.Equals(r.Id, req.TargetRole, StringComparison.OrdinalIgnoreCase));
            if (role is { Connectors.Count: > 0 })
                req.Parameters["allowed_connectors"] = string.Join(",", role.Connectors);
        }

        ctx.Logger.LogInformation(
            "While {WhileStep}: dispatch child={ChildId} type={Type} role={Role} iter={Iter}/{Max}",
            state.StepId,
            childStepId,
            child.Type,
            req.TargetRole,
            state.CurrentIteration,
            state.MaxIterations);

        await ctx.PublishAsync(req, EventDirection.Self, ct);
    }

    private static string BuildRunStepKey(string runId, string stepId) => $"{runId}:{stepId}";

    private bool EvaluateCondition(WhileState state, string output, int nextIteration)
    {
        var vars = BuildIterationVariables(output, nextIteration, state.MaxIterations);
        var eval = state.ConditionExpression.Contains("${", StringComparison.Ordinal)
            ? _expressionEvaluator.Evaluate(state.ConditionExpression, vars)
            : _expressionEvaluator.EvaluateExpression(state.ConditionExpression, vars);
        return IsTruthy(eval);
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

    private sealed class WhileState
    {
        public required string StepId { get; init; }
        public required string RunId { get; init; }
        public required string ConditionExpression { get; init; }
        public required int MaxIterations { get; init; }
        public int CurrentIteration { get; set; }
        public int CurrentChildIndex { get; set; }
        public required List<StepDefinition> Children { get; init; }
        public required string Input { get; set; }
        public required string FallbackRole { get; init; }
    }
}
