using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using System.Globalization;

namespace Aevatar.Workflow.Core.Execution;

internal sealed class WorkflowSideEffectIdempotencyKeyResolver
{
    private readonly WorkflowExpressionEvaluator _expressionEvaluator;

    public WorkflowSideEffectIdempotencyKeyResolver(WorkflowExpressionEvaluator expressionEvaluator)
    {
        _expressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
    }

    public WorkflowStepIdempotencyState Resolve(
        StepDefinition step,
        WorkflowStepIdempotencyState identity,
        IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(variables);

        var normalizedIdentity = NormalizeIdentity(identity);
        var key = string.IsNullOrWhiteSpace(step.IdempotencyKey)
            ? normalizedIdentity.IdempotencyKey
            : _expressionEvaluator.Evaluate(step.IdempotencyKey.Trim(), BuildExpressionVariables(normalizedIdentity, variables)).Trim();
        if (string.IsNullOrWhiteSpace(key))
            key = BuildDefaultKey(normalizedIdentity);

        normalizedIdentity.IdempotencyKey = key;
        return normalizedIdentity;
    }

    public static WorkflowStepIdempotencyState NormalizeIdentity(WorkflowStepIdempotencyState identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return new WorkflowStepIdempotencyState
        {
            LogicalRunId = Normalize(identity.LogicalRunId),
            StepId = Normalize(identity.StepId),
            LogicalAttempt = Math.Max(1, identity.LogicalAttempt),
            IdempotencyKey = Normalize(identity.IdempotencyKey),
        };
    }

    public static string BuildDefaultKey(WorkflowStepIdempotencyState identity)
    {
        var normalized = NormalizeIdentity(identity);
        return $"{normalized.LogicalRunId}:{normalized.StepId}:{normalized.LogicalAttempt}";
    }

    private static Dictionary<string, string> BuildExpressionVariables(
        WorkflowStepIdempotencyState identity,
        IReadOnlyDictionary<string, string> variables)
    {
        var expressionVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal)
        {
            ["run_id"] = identity.LogicalRunId,
            ["logical_run_id"] = identity.LogicalRunId,
            ["step_id"] = identity.StepId,
            ["logical_attempt"] = identity.LogicalAttempt.ToString(CultureInfo.InvariantCulture),
            ["attempt"] = identity.LogicalAttempt.ToString(CultureInfo.InvariantCulture),
        };

        return expressionVariables;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
