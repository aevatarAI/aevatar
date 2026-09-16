using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
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

        var normalizedValues = source.NormalizedForkSeed;
        var variables = normalizedValues == null
            ? CopyMap(source.ForkSeedVariables)
            : WorkflowNormalizedExecutionSeedCodec.Expand(normalizedValues);
        var completedStepIds = normalizedValues == null
            ? source.ForkSeedCompletedStepIds.ToList()
            : normalizedValues.CompletedSteps.Keys
                .OrderBy(static stepId => stepId, StringComparer.Ordinal)
                .ToList();
        return new WorkflowRunForkSeedView(
            source.RunId ?? string.Empty,
            source.Status ?? string.Empty,
            source.WorkflowYaml ?? string.Empty,
            CopyMap(source.InlineWorkflowYamls),
            source.ExpectedExecutionMode,
            variables,
            completedStepIds,
            source.ForkSeedLastFailedStepId ?? string.Empty,
            source.FinalError ?? string.Empty,
            source.ScopeId ?? string.Empty,
            source.ForkSeedIdempotencies.ToDictionary(
                x => x.Key,
                x => ToView(x.Value),
                StringComparer.Ordinal),
            source.CapabilityAdmissionPlan?.Clone(),
            source.WorkflowId ?? string.Empty,
            source.RevisionId ?? string.Empty,
            source.DefinitionVersion,
            ResolveOriginalRunId(source.Lineage, source.RunId),
            normalizedValues?.Clone());
    }

    public WorkflowRunForkSeedProjectionSnapshot ToProjectionSnapshot(WorkflowRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var kernelState = TryReadKernelState(state);
        var normalizedValues = kernelState == null
            ? null
            : WorkflowNormalizedExecutionSeedCodec.Capture(kernelState);
        var variables = kernelState == null || normalizedValues != null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : CopyMap(kernelState.Variables);
        if (normalizedValues == null)
            MergeLegacyForEachChildOutputs(variables, TryReadForEachState(state), state.RunId);
        var completedStepIds = normalizedValues == null
            ? ResolveCompletedStepIds(variables)
            : [];
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
            state.RevisionId ?? string.Empty,
            state.DefinitionVersion,
            kernelState?.InputFileRefs.Select(static fileRef => fileRef.Clone()).ToList() ?? [],
            kernelState?.IdempotencyByStepId.ToDictionary(
                x => x.Key,
                x => x.Value.Clone(),
                StringComparer.Ordinal) ?? new Dictionary<string, WorkflowStepIdempotencyState>(StringComparer.Ordinal),
            normalizedValues);
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

    private static ForEachModuleState? TryReadForEachState(WorkflowRunState state)
    {
        foreach (var packedState in state.ExecutionStates.Values)
        {
            if (packedState?.Is(ForEachModuleState.Descriptor) == true)
                return packedState.Unpack<ForEachModuleState>();
        }

        return null;
    }

    private static void MergeLegacyForEachChildOutputs(
        IDictionary<string, string> variables,
        ForEachModuleState? forEachState,
        string? runId)
    {
        var currentRunId = runId ?? string.Empty;
        if (forEachState == null || string.IsNullOrWhiteSpace(currentRunId))
            return;

        var kernelVariableKeys = variables.Keys.ToHashSet(StringComparer.Ordinal);
        if (string.Equals(forEachState.CompletedChildOutputsRunId, currentRunId, StringComparison.Ordinal))
        {
            foreach (var (stepId, output) in forEachState.CompletedChildOutputs
                         .OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(stepId) && !kernelVariableKeys.Contains(stepId))
                    variables[stepId] = output ?? string.Empty;
            }
        }

        foreach (var parent in forEachState.Parents
                     .Where(parent => string.Equals(
                         parent.Value.ParentRunId,
                         currentRunId,
                         StringComparison.Ordinal))
                     .OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            foreach (var result in parent.Value.Collected
                         .OrderBy(static item => item.Index)
                         .ThenBy(static item => item.StepId, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(result.StepId) && !kernelVariableKeys.Contains(result.StepId))
                    variables[result.StepId] = result.Output ?? string.Empty;
            }
        }
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

    private static string ResolveOriginalRunId(
        Aevatar.Workflow.Abstractions.WorkflowRunLineage? lineage,
        string? runId)
    {
        var originalRunId = lineage?.RetryFork?.OriginalRunId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(originalRunId))
            return originalRunId;

        return runId?.Trim() ?? string.Empty;
    }
}

public sealed record WorkflowRunForkSeedProjectionSnapshot(
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls,
    Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode ExpectedExecutionMode,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<string> CompletedStepIds,
    string LastFailedStepId,
    string ScopeId,
    string RevisionId,
    long DefinitionVersion,
    IReadOnlyList<WorkflowFileRef> InputFileRefs,
    IReadOnlyDictionary<string, WorkflowStepIdempotencyState> IdempotencyByStepId,
    Aevatar.Workflow.Abstractions.WorkflowNormalizedExecutionSeed? NormalizedValues);
