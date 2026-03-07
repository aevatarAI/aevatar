using System.Globalization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunStepRequestFactory
{
    private readonly WorkflowExpressionEvaluator _expressionEvaluator;

    public WorkflowRunStepRequestFactory(WorkflowExpressionEvaluator expressionEvaluator)
    {
        _expressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
    }

    public StepRequestEvent BuildStepRequest(
        StepDefinition step,
        string input,
        string runId,
        WorkflowRunState state,
        WorkflowDefinition? compiledWorkflow)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(state);

        var request = new StepRequestEvent
        {
            StepId = step.Id,
            StepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type),
            RunId = runId,
            Input = input,
            TargetRole = step.TargetRole ?? string.Empty,
        };

        var variables = ResolveVariables(state, input);
        foreach (var (key, value) in step.Parameters)
        {
            if (ShouldDeferWhileParameterEvaluation(request.StepType, key))
            {
                request.Parameters[key] = value;
                continue;
            }

            var evaluated = _expressionEvaluator.Evaluate(value, variables);
            request.Parameters[key] = WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key)
                ? WorkflowPrimitiveCatalog.ToCanonicalType(evaluated)
                : evaluated;
        }

        if (step.Branches is { Count: > 0 })
        {
            foreach (var (branchKey, branchValue) in step.Branches)
                request.Parameters[$"branch.{branchKey}"] = branchValue;
        }

        if (!string.IsNullOrWhiteSpace(step.TargetRole) && compiledWorkflow != null)
        {
            var role = compiledWorkflow.Roles.FirstOrDefault(x => string.Equals(x.Id, step.TargetRole, StringComparison.OrdinalIgnoreCase));
            if (role is { Connectors.Count: > 0 })
                request.Parameters["allowed_connectors"] = string.Join(",", role.Connectors);
        }

        return request;
    }

    public WorkflowStepExecutionState BuildExecutionState(
        string stepId,
        string stepType,
        string input,
        string targetRole,
        int attempt,
        string parentStepId,
        IReadOnlyDictionary<string, string> parameters)
    {
        var state = new WorkflowStepExecutionState
        {
            StepId = stepId,
            StepType = stepType,
            Input = input ?? string.Empty,
            TargetRole = targetRole ?? string.Empty,
            Attempt = attempt,
            ParentStepId = parentStepId ?? string.Empty,
        };

        foreach (var (key, value) in parameters)
            state.Parameters[key] = value;

        return state;
    }

    public bool EvaluateWhileCondition(WorkflowWhileState state, string output, int nextIteration)
    {
        ArgumentNullException.ThrowIfNull(state);

        var variables = BuildIterationVariables(output, nextIteration, state.MaxIterations);
        var evaluation = state.ConditionExpression.Contains("${", StringComparison.Ordinal)
            ? _expressionEvaluator.Evaluate(state.ConditionExpression, variables)
            : _expressionEvaluator.EvaluateExpression(state.ConditionExpression, variables);
        return IsTruthy(evaluation);
    }

    public Dictionary<string, string> BuildIterationVariables(string input, int iteration, int maxIterations) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["output"] = input,
            ["iteration"] = iteration.ToString(CultureInfo.InvariantCulture),
            ["max_iterations"] = maxIterations.ToString(CultureInfo.InvariantCulture),
        };

    private static Dictionary<string, string> ResolveVariables(WorkflowRunState state, string input)
    {
        var variables = state.Variables.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        variables["input"] = input;
        return variables;
    }

    private static bool ShouldDeferWhileParameterEvaluation(string canonicalStepType, string parameterKey) =>
        string.Equals(canonicalStepType, "while", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(parameterKey, "condition", StringComparison.OrdinalIgnoreCase) ||
         parameterKey.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase));

    private static bool IsTruthy(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (bool.TryParse(value, out var boolValue))
            return boolValue;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Math.Abs(number) >= 1e-9;
        return true;
    }
}
