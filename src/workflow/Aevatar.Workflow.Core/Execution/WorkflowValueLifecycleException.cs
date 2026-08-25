using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

internal sealed class WorkflowValueLifecycleException : InvalidOperationException
{
    internal WorkflowValueLifecycleException(
        WorkflowValueLifecycleFailureKind kind,
        string message)
        : base(message)
    {
        if (kind == WorkflowValueLifecycleFailureKind.Unspecified || !Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        Kind = kind;
        Code = ToCode(kind);
    }

    internal WorkflowValueLifecycleFailureKind Kind { get; }

    internal string Code { get; }

    internal static WorkflowValueLifecycleException ReleasedValueAccessed(string name) =>
        new(
            WorkflowValueLifecycleFailureKind.ReleasedValueAccessed,
            $"released_value_accessed: workflow value '{name}' was explicitly released.");

    internal static WorkflowValueLifecycleException ReleaseTargetMissing(string name) =>
        new(
            WorkflowValueLifecycleFailureKind.ReleaseTargetMissing,
            $"release_target_missing: workflow release target '{name}' is unavailable.");

    internal static WorkflowValueLifecycleException ReleaseTargetLive(string name) =>
        new(
            WorkflowValueLifecycleFailureKind.ReleaseTargetLive,
            $"release_target_live: workflow release target '{name}' is still referenced by active execution state.");

    internal static WorkflowValueLifecycleException ReleaseTargetPinnedForCompensation(string name) =>
        new(
            WorkflowValueLifecycleFailureKind.ReleaseTargetPinnedForCompensation,
            $"release_target_pinned_for_compensation: workflow release target '{name}' is required by compensation state.");

    internal static WorkflowValueLifecycleException SchemaUnavailable() =>
        new(
            WorkflowValueLifecycleFailureKind.SchemaUnavailable,
            "value_lifecycle_schema_unavailable: workflow value lifecycle requires live schema-v2 admission.");

    private static string ToCode(WorkflowValueLifecycleFailureKind kind) =>
        kind switch
        {
            WorkflowValueLifecycleFailureKind.ReleasedValueAccessed => "released_value_accessed",
            WorkflowValueLifecycleFailureKind.ReleaseTargetMissing => "release_target_missing",
            WorkflowValueLifecycleFailureKind.ReleaseTargetLive => "release_target_live",
            WorkflowValueLifecycleFailureKind.ReleaseTargetPinnedForCompensation =>
                "release_target_pinned_for_compensation",
            WorkflowValueLifecycleFailureKind.SchemaUnavailable => "value_lifecycle_schema_unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
}

internal static class WorkflowValueLifecyclePolicy
{
    internal static bool HasDeclarations(WorkflowDefinition? workflow) =>
        workflow != null && HasDeclarations(workflow.Steps);

    private static bool HasDeclarations(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            if (step.ValueLifecycle?.ReleaseVariablesAfterSuccess.Count > 0 ||
                step.Children is { Count: > 0 } && HasDeclarations(step.Children))
            {
                return true;
            }
        }

        return false;
    }
}
