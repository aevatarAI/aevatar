using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;
using WorkflowFileRef = Aevatar.Workflow.Abstractions.WorkflowFileRef;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowRunForkSeedReadModelMapper
{
    private const string InputVariableKey = "input";
    private const string WorkflowCallInvocationIdVariableKey = "workflow_call.invocation_id";
    private const string WorkflowUsageVariablePrefix = "workflow.usage.";
    private const string StepMirrorVariablePrefix = "steps.";
    private const string FailedStatus = "failed";

    public WorkflowRunForkSeedView ToSeedView(WorkflowExecutionCurrentStateDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowRunForkSeedView(
            source.RunId ?? string.Empty,
            source.Status ?? string.Empty,
            source.WorkflowYaml ?? string.Empty,
            CopyMap(source.InlineWorkflowYamls),
            source.ExpectedExecutionMode,
            CopyMap(source.ForkSeedVariables),
            source.ForkSeedCompletedStepIds.ToList(),
            source.ForkSeedLastFailedStepId ?? string.Empty,
            source.FinalError ?? string.Empty,
            source.ScopeId ?? string.Empty,
            source.ForkSeedIdempotencies.ToDictionary(
                x => x.Key,
                x => ToView(x.Value),
                StringComparer.Ordinal),
            source.CapabilityAdmissionPlan?.Clone());
    }

    public WorkflowRunForkSeedProjectionSnapshot ToProjectionSnapshot(WorkflowRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var kernelState = TryReadKernelState(state);
        var variables = kernelState == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : CopyMap(kernelState.Variables);
        var completedStepIds = ResolveCompletedStepIds(variables);
        var lastFailedStepId = string.Equals(state.Status, FailedStatus, StringComparison.OrdinalIgnoreCase)
            ? kernelState?.CurrentStepId?.Trim() ?? string.Empty
            : string.Empty;

        return new WorkflowRunForkSeedProjectionSnapshot(
            state.WorkflowYaml ?? string.Empty,
            CopyMap(state.InlineWorkflowYamls),
            state.ExpectedExecutionMode,
            variables,
            completedStepIds,
            lastFailedStepId,
            state.ScopeId ?? string.Empty,
            kernelState?.InputFileRefs.Select(static fileRef => fileRef.Clone()).ToList() ?? [],
            kernelState?.IdempotencyByStepId.ToDictionary(
                x => x.Key,
                x => x.Value.Clone(),
                StringComparer.Ordinal) ?? new Dictionary<string, WorkflowStepIdempotencyState>(StringComparer.Ordinal));
    }

    private static WorkflowExecutionKernelState? TryReadKernelState(WorkflowRunState state)
    {
        foreach (var packedState in state.ExecutionStates.Values)
        {
            if (packedState?.Is(WorkflowExecutionKernelState.Descriptor) == true)
                return packedState.Unpack<WorkflowExecutionKernelState>();
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveCompletedStepIds(IReadOnlyDictionary<string, string> variables) =>
        variables.Keys
            .Where(IsCompletedStepVariableKey)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    private static bool IsCompletedStepVariableKey(string key)
    {
        var trimmed = key.Trim();
        return !string.IsNullOrWhiteSpace(trimmed) &&
               !string.Equals(trimmed, InputVariableKey, StringComparison.Ordinal) &&
               !string.Equals(trimmed, WorkflowCallInvocationIdVariableKey, StringComparison.Ordinal) &&
               !trimmed.StartsWith(WorkflowUsageVariablePrefix, StringComparison.Ordinal) &&
               !trimmed.StartsWith(StepMirrorVariablePrefix, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> CopyMap(IDictionary<string, string> source) =>
        source.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.Ordinal);

    private static WorkflowStepIdempotencyView ToView(WorkflowStepIdempotencyReadModel source) =>
        new(
            source.LogicalRunId ?? string.Empty,
            source.StepId ?? string.Empty,
            source.LogicalAttempt,
            source.IdempotencyKey ?? string.Empty);
}

public sealed record WorkflowRunForkSeedProjectionSnapshot(
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls,
    Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode ExpectedExecutionMode,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<string> CompletedStepIds,
    string LastFailedStepId,
    string ScopeId,
    IReadOnlyList<WorkflowFileRef> InputFileRefs,
    IReadOnlyDictionary<string, WorkflowStepIdempotencyState> IdempotencyByStepId);
