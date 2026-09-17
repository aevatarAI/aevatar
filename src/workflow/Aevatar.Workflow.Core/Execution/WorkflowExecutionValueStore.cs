using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowExecutionValueStore
{
    private const string StepPrefix = "steps.";

    internal static bool IsNormalized(WorkflowExecutionKernelState state) =>
        state.NormalizedValues != null;

    internal static bool IsEmpty(WorkflowExecutionKernelState state)
    {
        var normalized = state.NormalizedValues;
        return normalized == null ||
               (normalized.CanonicalValues.Count == 0 &&
                normalized.Bindings.Count == 0 &&
                normalized.CompletedSteps.Count == 0 &&
                normalized.AcceptedCompletions.Count == 0 &&
                normalized.InheritedCompletions.Count == 0 &&
                normalized.ReleasedBindings.Count == 0 &&
                normalized.PendingInternalDispatches.Count == 0 &&
                string.IsNullOrWhiteSpace(normalized.CurrentStepInputValueId));
    }

    internal static void Initialize(WorkflowExecutionKernelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.NormalizedValues = new WorkflowNormalizedExecutionValuesState();
    }

    internal static void Reset(WorkflowExecutionKernelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.NormalizedValues == null)
            return;

        state.NormalizedValues = new WorkflowNormalizedExecutionValuesState();
    }

    internal static void CaptureInitialVariables(WorkflowExecutionKernelState state)
    {
        var normalized = state.NormalizedValues;
        if (normalized == null ||
            !state.Variables.Remove("input", out var input))
        {
            return;
        }

        var valueId = AddCanonicalValue(
            normalized,
            input,
            string.Empty,
            string.Empty,
            WorkflowCanonicalValueSourceKind.InitialInput);
        Bind(normalized, "input", valueId, WorkflowValueBindingKind.CurrentInput);
    }

    internal static string CaptureInputValue(
        WorkflowExecutionKernelState state,
        string input,
        WorkflowCanonicalValueSourceKind sourceKind)
    {
        var normalized = state.NormalizedValues
            ?? throw new InvalidOperationException("Normalized workflow value state is unavailable.");
        if (sourceKind is WorkflowCanonicalValueSourceKind.Unspecified or
            WorkflowCanonicalValueSourceKind.StepOutput or
            WorkflowCanonicalValueSourceKind.AssignedValue or
            WorkflowCanonicalValueSourceKind.InternalOutput or
            WorkflowCanonicalValueSourceKind.InternalInput)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceKind),
                sourceKind,
                "Captured input requires an input-origin source kind.");
        }
        var valueId = AddCanonicalValue(
            normalized,
            input,
            string.Empty,
            string.Empty,
            sourceKind);
        state.Variables.Remove("input");
        Bind(normalized, "input", valueId, WorkflowValueBindingKind.CurrentInput);
        RemoveUnreferencedValues(normalized);
        return valueId;
    }

    internal static void SetRequestOverride(
        WorkflowExecutionKernelState state,
        string name,
        string value)
    {
        var normalized = state.NormalizedValues
            ?? throw new InvalidOperationException("Normalized workflow value state is unavailable.");
        var key = name?.Trim() ?? string.Empty;
        if (key.Length == 0)
            return;
        if (!string.Equals(key, "input", StringComparison.Ordinal) &&
            !IsAuthorVariableKey(state, key))
        {
            throw new InvalidOperationException(
                $"Normalized workflow override '{key}' targets an engine-owned variable key.");
        }

        var valueId = AddCanonicalValue(
            normalized,
            value,
            string.Empty,
            string.Empty,
            WorkflowCanonicalValueSourceKind.RequestOverride);
        state.Variables.Remove(key);
        normalized.Bindings.Remove(key);
        normalized.ReleasedBindings.Remove(key);
        Bind(
            normalized,
            key,
            valueId,
            string.Equals(key, "input", StringComparison.Ordinal)
                ? WorkflowValueBindingKind.CurrentInput
                : WorkflowValueBindingKind.RequestOverride);
        if (string.Equals(key, "input", StringComparison.Ordinal))
            normalized.CurrentStepInputValueId = valueId;
        RemoveUnreferencedValues(normalized);
    }

    internal static bool IsAuthorVariableKey(string? name)
    {
        var key = name?.Trim() ?? string.Empty;
        return key.Length > 0 &&
               !string.Equals(key, "input", StringComparison.Ordinal) &&
               !string.Equals(key, "steps", StringComparison.Ordinal) &&
               !key.StartsWith(StepPrefix, StringComparison.Ordinal) &&
               !string.Equals(key, "workflow.usage", StringComparison.Ordinal) &&
               !key.StartsWith("workflow.usage.", StringComparison.Ordinal);
    }

    internal static bool IsLifecycleReleaseVariableKey(string? name)
    {
        var key = name?.Trim() ?? string.Empty;
        return IsAuthorVariableKey(key) &&
               !string.Equals(key, "workflow_call", StringComparison.Ordinal) &&
               !key.StartsWith("workflow_call.", StringComparison.Ordinal);
    }

    internal static bool IsAuthorVariableKey(
        WorkflowExecutionKernelState state,
        string? name)
    {
        ArgumentNullException.ThrowIfNull(state);
        var key = name?.Trim() ?? string.Empty;
        if (!IsAuthorVariableKey(key))
            return false;

        var normalized = state.NormalizedValues;
        if (normalized == null)
            return true;
        if (normalized.CompletedSteps.Keys.Any(stepId =>
                string.Equals(key, stepId, StringComparison.Ordinal)))
        {
            return false;
        }

        return !normalized.Bindings.TryGetValue(key, out var existing) ||
               existing.BindingKind is WorkflowValueBindingKind.RequestOverride or
                   WorkflowValueBindingKind.AssignedValue;
    }

    internal static void ReleaseVariablesAfterSuccess(
        WorkflowExecutionKernelState state,
        WorkflowStepValueLifecycle? lifecycle,
        string? releaseStepId,
        string? releaseExecutionId,
        Func<string, bool>? isPinnedForCompensation = null)
    {
        if (lifecycle == null || lifecycle.ReleaseVariablesAfterSuccess.Count == 0)
            return;

        var normalized = state.NormalizedValues
            ?? throw WorkflowValueLifecycleException.SchemaUnavailable();
        var stepId = releaseStepId?.Trim() ?? string.Empty;
        var executionId = releaseExecutionId?.Trim() ?? string.Empty;
        if (stepId.Length == 0 || executionId.Length == 0)
        {
            throw new InvalidOperationException(
                "Workflow value release requires exact step and execution identity.");
        }

        // Release is atomic for the whole lifecycle declaration. Keep every released
        // canonical tombstone so an older (step, execution) redelivery remains idempotent
        // even after later iterations have re-bound and released the same variable names.
        if (normalized.CanonicalValues.Values.Any(value =>
                value.Released != null && IsSameRelease(value.Released, stepId, executionId)))
        {
            return;
        }

        var plans = new List<ValueReleasePlan>();
        foreach (var rawName in lifecycle.ReleaseVariablesAfterSuccess)
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (name.Length == 0)
                throw WorkflowValueLifecycleException.ReleaseTargetMissing(name);

            if (normalized.ReleasedBindings.TryGetValue(name, out var priorRelease))
            {
                if (IsSameRelease(priorRelease, stepId, executionId))
                    continue;

                // Per-iteration release: a later execution re-bound the name to a new
                // canonical value after the prior release. The tombstone covers only its
                // own value id, so a live binding to a different value is a fresh release
                // target under the same name; anything else is a stale release attempt.
                if (!normalized.Bindings.TryGetValue(name, out var rebound) ||
                    string.IsNullOrWhiteSpace(rebound.ValueId) ||
                    string.Equals(rebound.ValueId, priorRelease.ValueId, StringComparison.Ordinal))
                {
                    throw WorkflowValueLifecycleException.ReleasedValueAccessed(name);
                }
            }

            if (!normalized.Bindings.TryGetValue(name, out var binding) ||
                string.IsNullOrWhiteSpace(binding.ValueId) ||
                !normalized.CanonicalValues.TryGetValue(binding.ValueId, out var canonical))
            {
                throw WorkflowValueLifecycleException.ReleaseTargetMissing(name);
            }

            if (canonical.Released != null)
            {
                if (IsSameRelease(canonical.Released, stepId, executionId))
                    continue;
                throw WorkflowValueLifecycleException.ReleasedValueAccessed(name);
            }

            if (IsLiveValue(normalized, binding.ValueId, stepId, executionId))
                throw WorkflowValueLifecycleException.ReleaseTargetLive(name);
            if (isPinnedForCompensation?.Invoke(binding.ValueId) == true)
                throw WorkflowValueLifecycleException.ReleaseTargetPinnedForCompensation(name);

            plans.Add(new ValueReleasePlan(name, binding.ValueId, canonical));
        }

        foreach (var group in plans.GroupBy(static plan => plan.ValueId, StringComparer.Ordinal))
        {
            var first = group.First();
            var tombstone = new WorkflowReleasedValueTombstone
            {
                Digest = CreateDigest(first.Canonical.Value ?? string.Empty),
                ReleasedAfterStepId = stepId,
                ReleasedAfterExecutionId = executionId,
                ValueId = first.ValueId,
            };
            first.Canonical.Value = string.Empty;
            first.Canonical.Released = tombstone.Clone();
            UpdateCompletionDigestEvidence(normalized, first.ValueId, tombstone.Digest);

            var aliases = normalized.Bindings
                .Where(pair => string.Equals(pair.Value.ValueId, first.ValueId, StringComparison.Ordinal))
                .Select(static pair => pair.Key)
                .Concat(group.Select(static plan => plan.Name))
                .Concat(CollectCurrentCompletionAliases(normalized, first.ValueId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var alias in aliases)
            {
                if (normalized.Bindings.TryGetValue(alias, out var currentBinding) &&
                    !string.Equals(currentBinding.ValueId, first.ValueId, StringComparison.Ordinal))
                {
                    continue;
                }

                normalized.Bindings.Remove(alias);
                state.Variables.Remove(alias);
                normalized.ReleasedBindings[alias] = tombstone.Clone();
            }
        }

        RemoveUnreferencedValues(normalized);
    }

    internal static void MigrateToValueLifecycleV2(WorkflowExecutionKernelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state.NormalizedValues;
        if (normalized == null)
            return;

        foreach (var completed in normalized.CompletedSteps.Values)
            PopulateMissingCompletionDigests(normalized, completed);
        foreach (var completed in normalized.AcceptedCompletions.Values)
            PopulateMissingCompletionDigests(normalized, completed);
        foreach (var completed in normalized.InheritedCompletions.Values)
            PopulateMissingCompletionDigests(normalized, completed);
        RemoveUnreferencedValues(normalized);
    }

    internal static string RecordInternalOutput(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion,
        string? inputValueId = null,
        WorkflowValueReplayEvidence replayEvidence = WorkflowValueReplayEvidence.Digest)
    {
        var normalized = state.NormalizedValues
            ?? throw new InvalidOperationException("Normalized workflow value state is unavailable.");
        var stepId = completion.StepId?.Trim() ?? string.Empty;
        if (stepId.Length == 0)
            return string.Empty;
        var executionId = completion.ExecutionId?.Trim() ?? string.Empty;
        if (executionId.Length == 0)
        {
            throw new InvalidOperationException(
                $"Normalized internal completion for step '{stepId}' has no execution id.");
        }
        var acceptanceKey = BuildCompletionAcceptanceKey(stepId, executionId);
        if (normalized.AcceptedCompletions.TryGetValue(acceptanceKey, out var replayed) ||
            normalized.CompletedSteps.TryGetValue(stepId, out replayed) &&
            string.Equals(replayed.ExecutionId, executionId, StringComparison.Ordinal))
        {
            return ValidateExactStepCompletionReplay(normalized, replayed, completion, stepId);
        }

        var valueId = ResolveOutputValueId(
            state,
            completion,
            WorkflowCanonicalValueSourceKind.InternalOutput,
            inputValueId,
            out var outputReferenceId);
        var assignedValueId = ResolveAssignedMirrorValueId(
            normalized,
            completion,
            valueId,
            stepId);
        var assignedMirrorValueId = ResolveRawAssignedMirrorValueId(
            normalized,
            completion,
            assignedValueId,
            stepId);
        state.Variables.Remove(stepId);
        normalized.Bindings.Remove(stepId);
        var completed = new WorkflowCompletedStepState
        {
            StepId = stepId,
            ExecutionId = completion.ExecutionId ?? string.Empty,
            OutputValueId = valueId,
            Success = completion.Success,
            Error = completion.Error ?? string.Empty,
            BranchKey = completion.BranchKey ?? string.Empty,
            NextStepId = completion.NextStepId ?? string.Empty,
            AssignedVariable = completion.AssignedVariable?.Trim() ?? string.Empty,
            AssignedValueId = assignedValueId,
            AssignedMirrorValueId = assignedMirrorValueId,
            EmitLegacyMirrors = false,
            OutputProvenance = completion.OutputProvenance,
            AssignedValueProvenance = completion.AssignedValueProvenance,
            OutputSourceStepId = completion.OutputSourceStepId ?? string.Empty,
            OutputSourceExecutionId = completion.OutputSourceExecutionId ?? string.Empty,
            OutputSourceValueId = completion.OutputSourceValueId ?? string.Empty,
            OutputReferenceId = completion.OutputReferenceId ?? string.Empty,
            FailureOutcome = completion.FailureOutcome,
            RetryDisposition = completion.RetryDisposition,
            Outcome = completion.Outcome,
        };
        PopulateCompletionDigests(normalized, completed, replayEvidence);
        normalized.CompletedSteps[stepId] = completed;
        MarkCompletionAccepted(
            normalized,
            completion,
            valueId,
            CreateAcceptedCompletionSnapshot(
                normalized,
                completion,
                valueId,
                assignedValueId,
                assignedMirrorValueId,
                jsonAliases: [],
                emitLegacyMirrors: false,
                replayEvidence));
        ConsumeOutputReference(normalized, outputReferenceId);
        RemoveUnreferencedValues(normalized);
        return valueId;
    }

    internal static string RecordStepCompletion(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion,
        WorkflowValueReplayEvidence replayEvidence = WorkflowValueReplayEvidence.Digest)
    {
        var normalized = state.NormalizedValues
            ?? throw new InvalidOperationException("Normalized workflow value state is unavailable.");
        var stepId = completion.StepId?.Trim() ?? string.Empty;
        if (stepId.Length == 0)
            return string.Empty;

        var executionId = completion.ExecutionId?.Trim() ?? string.Empty;
        if (executionId.Length == 0)
        {
            throw new InvalidOperationException(
                $"Normalized workflow completion for step '{stepId}' has no execution id.");
        }

        var acceptanceKey = BuildCompletionAcceptanceKey(stepId, executionId);
        if (normalized.AcceptedCompletions.TryGetValue(acceptanceKey, out var replayed) ||
            (normalized.CompletedSteps.TryGetValue(stepId, out replayed) &&
             string.Equals(replayed.ExecutionId, executionId, StringComparison.Ordinal)))
        {
            return ValidateExactStepCompletionReplay(normalized, replayed, completion, stepId);
        }

        var outputValueId = ResolveOutputValueId(
            state,
            completion,
            WorkflowCanonicalValueSourceKind.StepOutput,
            forwardedInputValueId: null,
            out var outputReferenceId);
        var assignedValueId = ResolveAssignedMirrorValueId(normalized, completion, outputValueId, stepId);
        var assignedMirrorValueId = ResolveRawAssignedMirrorValueId(
            normalized,
            completion,
            assignedValueId,
            stepId);
        var retryInputBinding = !completion.Success &&
                                normalized.Bindings.TryGetValue("input", out var currentInputBinding)
            ? currentInputBinding.Clone()
            : null;
        var legacyRetryInput = string.Empty;
        var hasLegacyRetryInput = !completion.Success &&
                                  state.Variables.TryGetValue("input", out legacyRetryInput);
        if (!string.IsNullOrWhiteSpace(completion.AssignedVariable))
        {
            var assignedVariable = completion.AssignedVariable.Trim();
            state.Variables.Remove(assignedVariable);
            normalized.Bindings.Remove(assignedVariable);
            Bind(
                normalized,
                assignedVariable,
                assignedValueId,
                WorkflowValueBindingKind.AssignedValue);
        }

        // Preserve the legacy collision order: explicit assignment is written
        // first, then engine-owned step/input/mirror keys overwrite conflicts.
        var jsonAliases = ResolveJsonPropertyAliases(completion);
        RemoveOverwrittenEngineEntries(state, completion, stepId, jsonAliases);
        if (!completion.Success)
        {
            if (retryInputBinding != null)
                normalized.Bindings["input"] = retryInputBinding;
            if (hasLegacyRetryInput)
                state.Variables["input"] = legacyRetryInput;
        }

        normalized.Bindings.Remove(stepId);
        Bind(normalized, $"{StepPrefix}{stepId}.output", outputValueId, WorkflowValueBindingKind.StepOutput);
        if (completion.Success)
        {
            Bind(normalized, "input", outputValueId, WorkflowValueBindingKind.CurrentInput);
            normalized.CurrentStepInputValueId = outputValueId;
        }

        var completed = normalized.CompletedSteps.TryGetValue(stepId, out var existing)
            ? existing
            : new WorkflowCompletedStepState();
        completed.StepId = stepId;
        completed.ExecutionId = completion.ExecutionId ?? string.Empty;
        completed.OutputValueId = outputValueId;
        completed.Success = completion.Success;
        completed.Error = completion.Error ?? string.Empty;
        completed.BranchKey = completion.BranchKey ?? string.Empty;
        completed.NextStepId = completion.NextStepId ?? string.Empty;
        completed.AssignedVariable = completion.AssignedVariable ?? string.Empty;
        completed.AssignedValueId = assignedValueId;
        completed.AssignedMirrorValueId = assignedMirrorValueId;
        completed.EmitLegacyMirrors = true;
        completed.OutputProvenance = completion.OutputProvenance;
        completed.AssignedValueProvenance = completion.AssignedValueProvenance;
        completed.OutputSourceStepId = completion.OutputSourceStepId ?? string.Empty;
        completed.OutputSourceExecutionId = completion.OutputSourceExecutionId ?? string.Empty;
        completed.OutputSourceValueId = completion.OutputSourceValueId ?? string.Empty;
        completed.OutputReferenceId = completion.OutputReferenceId ?? string.Empty;
        completed.FailureOutcome = completion.FailureOutcome;
        completed.RetryDisposition = completion.RetryDisposition;
        completed.Outcome = completion.Outcome;
        PopulateCompletionDigests(normalized, completed, replayEvidence);
        if (HasUsage(completion.Usage))
        {
            completed.Usage = ToUsageState(completion.Usage);
            completed.HasUsage = true;
        }

        foreach (var (key, value) in completion.Annotations)
        {
            if (!string.IsNullOrWhiteSpace(key))
                completed.Annotations[key.Trim()] = value ?? string.Empty;
        }

        foreach (var alias in jsonAliases)
        {
            completed.JsonValueBindings[alias] = new WorkflowJsonValueBindingState
            {
                OutputValueId = outputValueId,
            };
        }
        normalized.CompletedSteps[stepId] = completed;
        MarkCompletionAccepted(
            normalized,
            completion,
            outputValueId,
            CreateAcceptedCompletionSnapshot(
                normalized,
                completion,
                outputValueId,
                assignedValueId,
                assignedMirrorValueId,
                jsonAliases,
                emitLegacyMirrors: true,
                replayEvidence));

        ConsumeOutputReference(normalized, outputReferenceId);
        RemoveUnreferencedValues(normalized);
        return outputValueId;
    }

    internal static bool IsAcceptedCompletion(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion)
    {
        var normalized = state.NormalizedValues;
        if (normalized == null)
            return false;
        var stepId = completion.StepId?.Trim() ?? string.Empty;
        var executionId = completion.ExecutionId?.Trim() ?? string.Empty;
        if (stepId.Length == 0 || executionId.Length == 0 ||
            !normalized.AcceptedCompletionValueIds.TryGetValue(
                BuildCompletionAcceptanceKey(stepId, executionId),
                out var valueId))
        {
            return false;
        }

        if (state.ExecutionIdsByStepId.TryGetValue(stepId, out var activeExecutionId) &&
            !string.Equals(activeExecutionId, executionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!normalized.CompletedSteps.TryGetValue(stepId, out var current) ||
            !string.Equals(current.ExecutionId, executionId, StringComparison.Ordinal))
        {
            return false;
        }
        var acceptanceKey = BuildCompletionAcceptanceKey(stepId, executionId);
        var accepted = normalized.AcceptedCompletions.TryGetValue(acceptanceKey, out var snapshot)
            ? snapshot
            : current;
        normalized.CanonicalValues.TryGetValue(valueId, out var canonical);
        if (!string.Equals(accepted.OutputValueId, valueId, StringComparison.Ordinal) ||
            !CompletionOutputMatches(canonical, accepted.OutputDigest, completion, valueId))
        {
            return false;
        }

        try
        {
            return string.Equals(
                ValidateExactStepCompletionReplay(normalized, accepted, completion, stepId),
                valueId,
                StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void MarkCompletionAccepted(
        WorkflowNormalizedExecutionValuesState normalized,
        StepCompletedEvent completion,
        string valueId,
        WorkflowCompletedStepState accepted)
    {
        var stepId = completion.StepId?.Trim() ?? string.Empty;
        var executionId = completion.ExecutionId?.Trim() ?? string.Empty;
        if (stepId.Length == 0 || executionId.Length == 0 || valueId.Length == 0)
            throw new InvalidOperationException(
                "Normalized workflow completion acceptance requires step, execution, and value identity.");
        var acceptanceKey = BuildCompletionAcceptanceKey(stepId, executionId);
        normalized.AcceptedCompletionValueIds[acceptanceKey] = valueId;
        normalized.AcceptedCompletions[acceptanceKey] = accepted.Clone();
    }

    private static string BuildCompletionAcceptanceKey(string stepId, string executionId) =>
        $"{stepId}\0{executionId}";

    private static string ValidateExactStepCompletionReplay(
        WorkflowNormalizedExecutionValuesState normalized,
        WorkflowCompletedStepState replayed,
        StepCompletedEvent completion,
        string stepId)
    {
        if (!Enum.IsDefined(typeof(WorkflowStepOutputProvenance), completion.OutputProvenance) ||
            !Enum.IsDefined(typeof(WorkflowStepAssignedValueProvenance), completion.AssignedValueProvenance))
        {
            throw new InvalidOperationException(
                $"Workflow completion replay for step '{stepId}' declares an unknown provenance value.");
        }

        if (string.IsNullOrWhiteSpace(replayed.OutputValueId))
        {
            throw new InvalidOperationException(
                $"Workflow completion replay for step '{stepId}' has no canonical output value.");
        }
        normalized.CanonicalValues.TryGetValue(replayed.OutputValueId, out var outputValue);
        if (outputValue == null && !IsAuthoritativeDigest(replayed.OutputDigest))
        {
            throw new InvalidOperationException(
                $"Workflow completion replay for step '{stepId}' has no digest-backed output evidence.");
        }

        var sameCompletion =
            replayed.Success == completion.Success &&
            string.Equals(replayed.Error ?? string.Empty, completion.Error ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(replayed.BranchKey ?? string.Empty, completion.BranchKey ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(replayed.NextStepId ?? string.Empty, completion.NextStepId ?? string.Empty, StringComparison.Ordinal) &&
            CompletionOutputMatches(
                outputValue,
                replayed.OutputDigest,
                completion,
                replayed.OutputValueId) &&
            replayed.OutputProvenance == completion.OutputProvenance &&
            replayed.AssignedValueProvenance == completion.AssignedValueProvenance &&
            replayed.FailureOutcome == completion.FailureOutcome &&
            replayed.RetryDisposition == completion.RetryDisposition &&
            replayed.Outcome == completion.Outcome &&
            string.Equals(replayed.OutputSourceStepId ?? string.Empty, completion.OutputSourceStepId ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(replayed.OutputSourceExecutionId ?? string.Empty, completion.OutputSourceExecutionId ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(replayed.OutputSourceValueId ?? string.Empty, completion.OutputSourceValueId ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(replayed.OutputReferenceId ?? string.Empty, completion.OutputReferenceId ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(
                replayed.AssignedVariable?.Trim() ?? string.Empty,
                completion.AssignedVariable?.Trim() ?? string.Empty,
                StringComparison.Ordinal);
        if (!sameCompletion)
        {
            throw new InvalidOperationException(
                $"Workflow completion replay for step '{stepId}' conflicts with its committed execution '{replayed.ExecutionId}'.");
        }

        if (HasUsage(completion.Usage) != replayed.HasUsage ||
            (replayed.HasUsage && !UsageEquals(replayed.Usage, completion.Usage)))
        {
            throw new InvalidOperationException(
                $"Workflow completion replay for step '{stepId}' changed its usage metrics.");
        }

        var incomingAnnotations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in completion.Annotations)
        {
            var normalizedKey = key?.Trim() ?? string.Empty;
            if (normalizedKey.Length > 0)
                incomingAnnotations[normalizedKey] = value ?? string.Empty;
        }
        if (incomingAnnotations.Count != replayed.Annotations.Count ||
            incomingAnnotations.Any(annotation =>
                !replayed.Annotations.TryGetValue(annotation.Key, out var committedAnnotation) ||
                !string.Equals(
                    committedAnnotation ?? string.Empty,
                    annotation.Value,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Workflow completion replay for step '{stepId}' changed its annotations.");
        }
        var assignedValue = string.IsNullOrWhiteSpace(completion.AssignedValue)
            ? completion.Output ?? string.Empty
            : completion.AssignedValue;
        if (string.IsNullOrWhiteSpace(replayed.AssignedValueId))
        {
            if (!string.IsNullOrWhiteSpace(completion.AssignedVariable) ||
                !string.IsNullOrWhiteSpace(completion.AssignedValue))
            {
                throw new InvalidOperationException(
                    $"Workflow completion replay for step '{stepId}' changed its assigned-value mirror.");
            }
        }
        else
        {
            normalized.CanonicalValues.TryGetValue(replayed.AssignedValueId, out var assignedCanonical);
            if (!ValueMatches(assignedCanonical, replayed.AssignedValueDigest, assignedValue))
            {
                throw new InvalidOperationException(
                    $"Workflow completion replay for step '{stepId}' changed its assigned-value mirror.");
            }
        }

        var rawAssignedValue = completion.AssignedValue ?? string.Empty;
        normalized.CanonicalValues.TryGetValue(
            replayed.AssignedMirrorValueId,
            out var assignedMirrorCanonical);
        if (string.IsNullOrWhiteSpace(replayed.AssignedMirrorValueId) ||
            !ValueMatches(
                assignedMirrorCanonical,
                replayed.AssignedMirrorDigest,
                rawAssignedValue))
        {
            if (!string.IsNullOrWhiteSpace(replayed.AssignedValueId))
            {
                throw new InvalidOperationException(
                    $"Workflow completion replay for step '{stepId}' changed its raw assigned-value mirror.");
            }
        }

        switch (completion.OutputProvenance)
        {
            case WorkflowStepOutputProvenance.Produced:
                var expectedSourceKind = replayed.EmitLegacyMirrors
                    ? WorkflowCanonicalValueSourceKind.StepOutput
                    : WorkflowCanonicalValueSourceKind.InternalOutput;
                if (outputValue != null &&
                    (outputValue.SourceKind != expectedSourceKind ||
                     !string.Equals(outputValue.ProducerStepId, stepId, StringComparison.Ordinal) ||
                     !string.Equals(outputValue.ProducerExecutionId, replayed.ExecutionId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Workflow produced completion replay for step '{stepId}' does not own its canonical output.");
                }
                break;
            case WorkflowStepOutputProvenance.ForwardedInput:
                if (replayed.EmitLegacyMirrors &&
                    !string.Equals(
                        normalized.CurrentStepInputValueId,
                        replayed.OutputValueId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Workflow pass-through completion replay for step '{stepId}' no longer references its exact input.");
                }
                break;
            case WorkflowStepOutputProvenance.ReferencedStepOutput:
                if (!string.Equals(replayed.OutputSourceValueId, replayed.OutputValueId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(replayed.OutputSourceStepId) ||
                    string.IsNullOrWhiteSpace(replayed.OutputSourceExecutionId) ||
                    outputValue != null &&
                    (!string.Equals(outputValue.ProducerStepId, replayed.OutputSourceStepId, StringComparison.Ordinal) ||
                     !string.Equals(outputValue.ProducerExecutionId, replayed.OutputSourceExecutionId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Workflow referenced completion replay for step '{stepId}' has inconsistent source identity.");
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"Workflow completion replay for step '{stepId}' has unsupported output provenance '{completion.OutputProvenance}'.");
        }

        return replayed.OutputValueId;
    }

    private static WorkflowCompletedStepState CreateAcceptedCompletionSnapshot(
        WorkflowNormalizedExecutionValuesState normalized,
        StepCompletedEvent completion,
        string outputValueId,
        string assignedValueId,
        string assignedMirrorValueId,
        IReadOnlyList<string> jsonAliases,
        bool emitLegacyMirrors,
        WorkflowValueReplayEvidence replayEvidence)
    {
        var accepted = new WorkflowCompletedStepState
        {
            StepId = completion.StepId?.Trim() ?? string.Empty,
            ExecutionId = completion.ExecutionId?.Trim() ?? string.Empty,
            OutputValueId = outputValueId,
            Success = completion.Success,
            Error = completion.Error ?? string.Empty,
            BranchKey = completion.BranchKey ?? string.Empty,
            NextStepId = completion.NextStepId ?? string.Empty,
            AssignedVariable = completion.AssignedVariable?.Trim() ?? string.Empty,
            AssignedValueId = assignedValueId,
            AssignedMirrorValueId = assignedMirrorValueId,
            EmitLegacyMirrors = emitLegacyMirrors,
            OutputProvenance = completion.OutputProvenance,
            AssignedValueProvenance = completion.AssignedValueProvenance,
            OutputSourceStepId = completion.OutputSourceStepId ?? string.Empty,
            OutputSourceExecutionId = completion.OutputSourceExecutionId ?? string.Empty,
            OutputSourceValueId = completion.OutputSourceValueId ?? string.Empty,
            OutputReferenceId = completion.OutputReferenceId ?? string.Empty,
            FailureOutcome = completion.FailureOutcome,
            RetryDisposition = completion.RetryDisposition,
            Outcome = completion.Outcome,
        };
        if (HasUsage(completion.Usage))
        {
            accepted.Usage = ToUsageState(completion.Usage);
            accepted.HasUsage = true;
        }
        foreach (var (key, value) in completion.Annotations)
        {
            var normalizedKey = key?.Trim() ?? string.Empty;
            if (normalizedKey.Length > 0)
                accepted.Annotations[normalizedKey] = value ?? string.Empty;
        }
        foreach (var alias in jsonAliases)
        {
            accepted.JsonValueBindings[alias] = new WorkflowJsonValueBindingState
            {
                OutputValueId = outputValueId,
            };
        }

        PopulateCompletionDigests(normalized, accepted, replayEvidence);

        return accepted;
    }

    private static bool UsageEquals(
        WorkflowUsageMetricsState committed,
        WorkflowUsageMetrics incoming) =>
        committed.PromptTokens == incoming.PromptTokens &&
        committed.CompletionTokens == incoming.CompletionTokens &&
        committed.TotalTokens == incoming.TotalTokens &&
        string.Equals(committed.Model ?? string.Empty, incoming.Model ?? string.Empty, StringComparison.Ordinal) &&
        committed.Cost.Equals(incoming.Cost) &&
        committed.LatencyMs == incoming.LatencyMs;

    private static bool CompletionOutputMatches(
        WorkflowCanonicalValueState? canonical,
        WorkflowValueDigest? digest,
        StepCompletedEvent completion,
        string acceptedValueId)
    {
        if (ValueMatches(canonical, digest, completion.Output ?? string.Empty))
        {
            return true;
        }

        return completion.OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput &&
               string.IsNullOrEmpty(completion.Output) &&
               string.Equals(completion.OutputSourceValueId, acceptedValueId, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(completion.OutputReferenceId);
    }

    private static bool ValueMatches(
        WorkflowCanonicalValueState? canonical,
        WorkflowValueDigest? digest,
        string? incoming)
    {
        if (canonical != null && canonical.Released == null)
        {
            return string.Equals(
                canonical.Value ?? string.Empty,
                incoming ?? string.Empty,
                StringComparison.Ordinal);
        }

        var evidence = canonical?.Released?.Digest;
        if (!IsAuthoritativeDigest(evidence))
            evidence = digest;
        return DigestMatches(evidence, incoming ?? string.Empty);
    }

    private static WorkflowValueDigest CreateDigest(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        return new WorkflowValueDigest
        {
            Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)),
            Utf8Size = checked((ulong)bytes.LongLength),
        };
    }

    internal static bool IsAuthoritativeDigest(WorkflowValueDigest? digest) =>
        digest is { Redacted: false } && digest.Sha256.Length == SHA256.HashSizeInBytes;

    internal static bool IsRedactedDigest(WorkflowValueDigest? digest) =>
        digest is { Redacted: true, Utf8Size: 0 } && digest.Sha256.Length == 0;

    private static bool DigestMatches(WorkflowValueDigest? digest, string value)
    {
        if (!IsAuthoritativeDigest(digest))
            return false;

        var bytes = Encoding.UTF8.GetBytes(value);
        return digest!.Utf8Size == checked((ulong)bytes.LongLength) &&
               CryptographicOperations.FixedTimeEquals(
                   digest.Sha256.Span,
                   SHA256.HashData(bytes));
    }

    private static void PopulateCompletionDigests(
        WorkflowNormalizedExecutionValuesState normalized,
        WorkflowCompletedStepState completed,
        WorkflowValueReplayEvidence replayEvidence)
    {
        // Digest evidence is a schema-v2 fact: it lets RemoveUnreferencedValues drop the raw
        // payload that older replay ledgers keep alive. An actor whose runtime schema context
        // has not adopted v2 must keep the raw value so a v1 reader can still replay exactly.
        if (replayEvidence != WorkflowValueReplayEvidence.Digest)
            return;

        completed.OutputDigest = ResolveDigest(normalized, completed.OutputValueId);
        completed.AssignedValueDigest = ResolveDigest(normalized, completed.AssignedValueId);
        completed.AssignedMirrorDigest = ResolveDigest(normalized, completed.AssignedMirrorValueId);
    }

    private static void PopulateMissingCompletionDigests(
        WorkflowNormalizedExecutionValuesState normalized,
        WorkflowCompletedStepState completed)
    {
        if (!IsAuthoritativeDigest(completed.OutputDigest))
            completed.OutputDigest = ResolveDigest(normalized, completed.OutputValueId);
        if (!IsAuthoritativeDigest(completed.AssignedValueDigest))
            completed.AssignedValueDigest = ResolveDigest(normalized, completed.AssignedValueId);
        if (!IsAuthoritativeDigest(completed.AssignedMirrorDigest))
            completed.AssignedMirrorDigest = ResolveDigest(normalized, completed.AssignedMirrorValueId);
    }

    private static WorkflowValueDigest? ResolveDigest(
        WorkflowNormalizedExecutionValuesState normalized,
        string? valueId)
    {
        var key = valueId?.Trim() ?? string.Empty;
        if (key.Length == 0 || !normalized.CanonicalValues.TryGetValue(key, out var canonical))
            return null;
        if (canonical.Released?.Digest != null)
            return canonical.Released.Digest.Clone();
        return CreateDigest(canonical.Value ?? string.Empty);
    }

    private static bool IsSameRelease(
        WorkflowReleasedValueTombstone release,
        string stepId,
        string executionId) =>
        string.Equals(release.ReleasedAfterStepId, stepId, StringComparison.Ordinal) &&
        string.Equals(release.ReleasedAfterExecutionId, executionId, StringComparison.Ordinal);

    private static bool IsLiveValue(
        WorkflowNormalizedExecutionValuesState normalized,
        string valueId,
        string releaseStepId,
        string releaseExecutionId) =>
        string.Equals(normalized.CurrentStepInputValueId, valueId, StringComparison.Ordinal) ||
        (normalized.CompletedSteps.TryGetValue(releaseStepId, out var currentCompletion) &&
         string.Equals(currentCompletion.ExecutionId, releaseExecutionId, StringComparison.Ordinal) &&
         string.Equals(currentCompletion.OutputValueId, valueId, StringComparison.Ordinal)) ||
        normalized.PendingOutputReferences.Values.Any(reference =>
            string.Equals(reference.SourceValueId, valueId, StringComparison.Ordinal)) ||
        normalized.PendingInternalDispatches.Values.Any(dispatch =>
            string.Equals(dispatch.InputValueId, valueId, StringComparison.Ordinal));

    private static IEnumerable<string> CollectCurrentCompletionAliases(
        WorkflowNormalizedExecutionValuesState normalized,
        string valueId)
    {
        foreach (var (stepId, completed) in normalized.CompletedSteps)
        {
            var prefix = $"{StepPrefix}{stepId}";
            if (string.Equals(completed.OutputValueId, valueId, StringComparison.Ordinal))
            {
                yield return stepId;
                yield return $"{prefix}.output";
                foreach (var alias in completed.JsonValueBindings.Keys)
                    yield return $"{prefix}.json.{alias}";
            }

            if (string.Equals(completed.AssignedValueId, valueId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(completed.AssignedVariable) &&
                normalized.Bindings.TryGetValue(completed.AssignedVariable.Trim(), out var binding) &&
                string.Equals(binding.ValueId, valueId, StringComparison.Ordinal))
            {
                yield return completed.AssignedVariable.Trim();
            }

            if (string.Equals(completed.AssignedMirrorValueId, valueId, StringComparison.Ordinal))
                yield return $"{prefix}.assigned_value";
        }
    }

    private static void UpdateCompletionDigestEvidence(
        WorkflowNormalizedExecutionValuesState normalized,
        string valueId,
        WorkflowValueDigest digest)
    {
        foreach (var completed in normalized.CompletedSteps.Values)
            UpdateCompletionDigestEvidence(completed, valueId, digest);
        foreach (var completed in normalized.AcceptedCompletions.Values)
            UpdateCompletionDigestEvidence(completed, valueId, digest);
        foreach (var completed in normalized.InheritedCompletions.Values)
            UpdateCompletionDigestEvidence(completed, valueId, digest);
    }

    private static void UpdateCompletionDigestEvidence(
        WorkflowCompletedStepState completed,
        string valueId,
        WorkflowValueDigest digest)
    {
        if (string.Equals(completed.OutputValueId, valueId, StringComparison.Ordinal))
            completed.OutputDigest = digest.Clone();
        if (string.Equals(completed.AssignedValueId, valueId, StringComparison.Ordinal))
            completed.AssignedValueDigest = digest.Clone();
        if (string.Equals(completed.AssignedMirrorValueId, valueId, StringComparison.Ordinal))
            completed.AssignedMirrorDigest = digest.Clone();
    }

    internal static void SetCurrentStepInput(
        WorkflowExecutionKernelState state,
        string input,
        string? canonicalValueId)
    {
        var normalized = state.NormalizedValues;
        if (normalized == null)
        {
            state.CurrentStepInput = input ?? string.Empty;
            return;
        }

        var valueId = canonicalValueId?.Trim() ?? string.Empty;
        if (valueId.Length == 0 ||
            !normalized.CanonicalValues.TryGetValue(valueId, out var canonical))
        {
            throw new InvalidOperationException(
                $"Normalized workflow current-step input references missing canonical value '{valueId}'.");
        }
        if (canonical.Released != null)
            throw WorkflowValueLifecycleException.ReleasedValueAccessed("input");

        if (!string.Equals(canonical.Value ?? string.Empty, input ?? string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Normalized workflow current-step input does not match canonical value '{valueId}'.");
        }

        state.CurrentStepInput = string.Empty;
        normalized.CurrentStepInputValueId = valueId;
    }

    internal static string ResolveCurrentStepInput(WorkflowExecutionKernelState state)
    {
        var normalized = state.NormalizedValues;
        if (normalized == null || string.IsNullOrWhiteSpace(normalized.CurrentStepInputValueId))
            return state.CurrentStepInput ?? string.Empty;

        return normalized.CanonicalValues.TryGetValue(
            normalized.CurrentStepInputValueId,
            out var value)
                ? value.Released == null
                    ? value.Value ?? string.Empty
                    : throw WorkflowValueLifecycleException.ReleasedValueAccessed("input")
                : throw new InvalidOperationException(
                    $"Workflow current-step input references missing canonical value '{normalized.CurrentStepInputValueId}'.");
    }

    internal static IReadOnlyDictionary<string, string> CreateVariableView(
        WorkflowExecutionKernelState state) =>
        state.NormalizedValues == null
            ? state.Variables
            : new WorkflowExecutionVariableView(state);

    internal static string? GetBindingValueId(
        WorkflowExecutionKernelState state,
        string name)
    {
        if (state.NormalizedValues?.Bindings.TryGetValue(name, out var binding) == true &&
            state.NormalizedValues.CanonicalValues.ContainsKey(binding.ValueId))
        {
            return binding.ValueId;
        }

        return null;
    }

    internal static string ResolveCompletedStepOutput(
        WorkflowExecutionKernelState state,
        string stepId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var key = stepId?.Trim() ?? string.Empty;
        if (key.Length == 0)
            throw new InvalidOperationException("Workflow completed-step output has no step id.");

        var normalized = state.NormalizedValues;
        if (normalized == null)
        {
            return state.Variables.TryGetValue(key, out var legacyValue)
                ? legacyValue ?? string.Empty
                : throw new InvalidOperationException(
                    $"Workflow completed step '{key}' has no legacy output value.");
        }

        if (!normalized.CompletedSteps.TryGetValue(key, out var completed) ||
            string.IsNullOrWhiteSpace(completed.OutputValueId) ||
            !normalized.CanonicalValues.TryGetValue(completed.OutputValueId, out var canonical))
        {
            throw new InvalidOperationException(
                $"Workflow completed step '{key}' has no canonical output value.");
        }

        if (canonical.Released != null)
            throw WorkflowValueLifecycleException.ReleasedValueAccessed(key);
        return canonical.Value ?? string.Empty;
    }

    internal static string? GetCompletedStepOutputValueId(
        WorkflowExecutionKernelState state,
        string? stepId)
    {
        var key = stepId?.Trim() ?? string.Empty;
        if (key.Length == 0 ||
            state.NormalizedValues?.CompletedSteps.TryGetValue(key, out var completed) != true ||
            string.IsNullOrWhiteSpace(completed.OutputValueId) ||
            !state.NormalizedValues.CanonicalValues.ContainsKey(completed.OutputValueId))
        {
            return null;
        }

        return completed.OutputValueId;
    }

    internal static WorkflowCanonicalValueState GetCanonicalValue(
        WorkflowExecutionKernelState state,
        string? valueId)
    {
        var key = valueId?.Trim() ?? string.Empty;
        if (key.Length == 0 ||
            state.NormalizedValues?.CanonicalValues.TryGetValue(key, out var value) != true)
        {
            throw new InvalidOperationException(
                $"Workflow canonical value '{key}' is unavailable.");
        }

        if (value.Released != null)
            throw WorkflowValueLifecycleException.ReleasedValueAccessed(key);
        return value;
    }

    internal static void SetExpressionInput(
        WorkflowExecutionKernelState state,
        string input,
        string? canonicalValueId)
    {
        var normalized = state.NormalizedValues;
        if (normalized == null)
        {
            state.Variables["input"] = input ?? string.Empty;
            return;
        }

        var valueId = canonicalValueId?.Trim() ?? string.Empty;
        if (valueId.Length == 0 ||
            !normalized.CanonicalValues.TryGetValue(valueId, out var canonical))
        {
            throw new InvalidOperationException(
                $"Normalized workflow expression input references missing canonical value '{valueId}'.");
        }
        if (canonical.Released != null)
            throw WorkflowValueLifecycleException.ReleasedValueAccessed("input");

        if (!string.Equals(canonical.Value ?? string.Empty, input ?? string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Normalized workflow expression input does not match canonical value '{valueId}'.");
        }

        state.Variables.Remove("input");
        Bind(normalized, "input", valueId, WorkflowValueBindingKind.CurrentInput);
    }

    internal static Dictionary<string, string> ExpandVariables(
        WorkflowExecutionKernelState state) =>
        new(CreateVariableView(state), StringComparer.Ordinal);

    internal static bool PrepareInternalDispatch(
        WorkflowExecutionKernelState state,
        StepRequestEvent request,
        string? originEnvelopeId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        var normalized = state.NormalizedValues;
        if (normalized == null)
            return false;

        var stepId = request.StepId?.Trim() ?? string.Empty;
        var runId = request.RunId?.Trim() ?? string.Empty;
        if (stepId.Length == 0 || runId.Length == 0 ||
            !string.Equals(state.RunId, runId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Normalized internal workflow dispatch requires the active run and a non-empty step id.");
        }

        if (string.Equals(state.CurrentStepId, stepId, StringComparison.Ordinal) &&
            state.ExecutionIdsByStepId.TryGetValue(stepId, out var topLevelExecutionId) &&
            !string.IsNullOrWhiteSpace(topLevelExecutionId) &&
            string.Equals(topLevelExecutionId, request.ExecutionId, StringComparison.Ordinal))
        {
            return false;
        }

        var envelopeId = originEnvelopeId?.Trim() ?? string.Empty;
        var executionId = request.ExecutionId?.Trim() ?? string.Empty;
        if (executionId.Length == 0)
        {
            if (envelopeId.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Normalized internal workflow dispatch '{stepId}' requires an execution id or runtime envelope id.");
            }

            executionId = $"internal-{envelopeId}";
            request.ExecutionId = executionId;
        }

        var dispatchKey = BuildInternalDispatchKey(runId, stepId, executionId);
        if (normalized.PendingInternalDispatches.TryGetValue(dispatchKey, out var existing))
        {
            if (!string.Equals(existing.RunId, runId, StringComparison.Ordinal) ||
                !string.Equals(existing.StepId, stepId, StringComparison.Ordinal) ||
                !string.Equals(existing.ExecutionId, executionId, StringComparison.Ordinal) ||
                !normalized.CanonicalValues.TryGetValue(existing.InputValueId, out var existingInput) ||
                existingInput.Released != null ||
                !string.Equals(existingInput.Value, request.Input ?? string.Empty, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(request.InputValueId) &&
                 !string.Equals(request.InputValueId, existing.InputValueId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Normalized internal workflow dispatch identity '{stepId}/{executionId}' collides with another lease.");
            }

            request.ExecutionId = executionId;
            request.InputValueId = existing.InputValueId;
            if (existing.OriginEnvelopeId.Length == 0 && envelopeId.Length > 0)
                existing.OriginEnvelopeId = envelopeId;
            return true;
        }

        var inputValueId = request.InputValueId?.Trim() ?? string.Empty;
        if (inputValueId.Length == 0)
        {
            inputValueId = AddCanonicalValue(
                normalized,
                request.Input ?? string.Empty,
                stepId,
                executionId,
                WorkflowCanonicalValueSourceKind.InternalInput);
            request.InputValueId = inputValueId;
        }
        else if (!normalized.CanonicalValues.TryGetValue(inputValueId, out var canonicalInput) ||
                 canonicalInput.Released != null ||
                 !string.Equals(canonicalInput.Value, request.Input ?? string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Normalized internal workflow dispatch '{stepId}/{executionId}' references invalid canonical input '{inputValueId}'.");
        }

        var dispatch = new WorkflowPendingInternalDispatchState
        {
            DispatchKey = dispatchKey,
            RunId = runId,
            StepId = stepId,
            ExecutionId = executionId,
            InputValueId = inputValueId,
            OriginEnvelopeId = envelopeId,
        };
        normalized.PendingInternalDispatches[dispatchKey] = dispatch;
        return true;
    }

    internal static bool TryGetInternalDispatch(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion,
        out WorkflowPendingInternalDispatchState dispatch)
    {
        dispatch = null!;
        var normalized = state.NormalizedValues;
        var runId = completion.RunId?.Trim() ?? string.Empty;
        var stepId = completion.StepId?.Trim() ?? string.Empty;
        var executionId = completion.ExecutionId?.Trim() ?? string.Empty;
        if (normalized == null || runId.Length == 0 || stepId.Length == 0 || executionId.Length == 0)
            return false;

        return normalized.PendingInternalDispatches.TryGetValue(
            BuildInternalDispatchKey(runId, stepId, executionId),
            out dispatch!);
    }

    internal static void ConsumeInternalDispatch(
        WorkflowExecutionKernelState state,
        WorkflowPendingInternalDispatchState dispatch)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(dispatch);
        var normalized = state.NormalizedValues;
        if (normalized == null)
            return;

        normalized.PendingInternalDispatches.Remove(dispatch.DispatchKey);
        RemoveUnreferencedValues(normalized);
    }

    internal static string BuildInternalExecutionId(
        string? runId,
        string? parentExecutionId,
        string? stepId)
    {
        var identity = string.Join(
            '\0',
            runId?.Trim() ?? string.Empty,
            parentExecutionId?.Trim() ?? string.Empty,
            stepId?.Trim() ?? string.Empty);
        return $"internal-execution-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}";
    }

    internal static async Task ReferenceCompletedStepOutputAsync(
        StepCompletedEvent target,
        StepCompletedEvent source,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ctx);

        var state = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
            ctx,
            WorkflowExecutionKernel.ModuleStateKey);
        StageCompletedStepOutputReference(state, target, source);
        if (state.NormalizedValues != null)
        {
            await WorkflowExecutionStateAccess.SaveAsync(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey,
                state,
                ct);
        }
    }

    /// <summary>
    /// Rehydrates a referenced completion immediately before publication. The
    /// durable module outbox keeps only source identity; the event sent to the
    /// kernel still carries the canonical value required by the completion
    /// contract.
    /// </summary>
    internal static StepCompletedEvent HydrateReferencedCompletionForPublication(
        StepCompletedEvent persisted,
        WorkflowExecutionKernelState state)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(state);
        var published = persisted.Clone();
        if (state.NormalizedValues == null ||
            published.OutputProvenance != WorkflowStepOutputProvenance.ReferencedStepOutput)
        {
            return published;
        }

        var sourceValueId = published.OutputSourceValueId?.Trim() ?? string.Empty;
        if (sourceValueId.Length == 0 ||
            !state.NormalizedValues.CanonicalValues.TryGetValue(sourceValueId, out var sourceValue))
        {
            throw new InvalidOperationException(
                $"Workflow referenced completion '{published.StepId}' has no canonical publication value '{sourceValueId}'.");
        }
        if (sourceValue.Released != null)
            throw WorkflowValueLifecycleException.ReleasedValueAccessed(published.OutputSourceStepId);

        published.Output = sourceValue.Value ?? string.Empty;
        return published;
    }

    internal static StepCompletedEvent HydrateReferencedCompletionForPublication(
        StepCompletedEvent persisted,
        IWorkflowExecutionContext ctx)
    {
        var state = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
            ctx,
            WorkflowExecutionKernel.ModuleStateKey);
        return HydrateReferencedCompletionForPublication(persisted, state);
    }

    internal static void StageCompletedStepOutputReference(
        WorkflowExecutionKernelState state,
        StepCompletedEvent target,
        StepCompletedEvent source)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        var sourceStepId = source.StepId?.Trim() ?? string.Empty;
        if (sourceStepId.Length == 0)
            throw new InvalidOperationException("Workflow step output reference has no source step id.");

        var sourceExecutionId = source.ExecutionId?.Trim() ?? string.Empty;
        target.OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput;
        target.OutputSourceStepId = sourceStepId;
        target.OutputSourceExecutionId = sourceExecutionId;

        var normalized = state.NormalizedValues;
        if (normalized == null)
            return;

        if (!normalized.CompletedSteps.TryGetValue(sourceStepId, out var sourceCompletion) ||
            string.IsNullOrWhiteSpace(sourceCompletion.OutputValueId))
        {
            throw new InvalidOperationException(
                $"Workflow step output reference cannot resolve source step '{sourceStepId}'.");
        }

        if (!normalized.CanonicalValues.TryGetValue(sourceCompletion.OutputValueId, out var sourceValue))
        {
            throw new InvalidOperationException(
                $"Workflow step output reference source '{sourceStepId}' has missing canonical value '{sourceCompletion.OutputValueId}'.");
        }
        if (sourceValue.Released != null)
            throw WorkflowValueLifecycleException.ReleasedValueAccessed(sourceStepId);

        if (!string.Equals(
                sourceCompletion.ExecutionId ?? string.Empty,
                sourceExecutionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workflow step output reference source '{sourceStepId}/{sourceExecutionId}' is not the exact committed completion.");
        }

        var referenceOnlySource =
            string.IsNullOrEmpty(source.Output) &&
            source.OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput &&
            string.Equals(
                source.OutputSourceValueId,
                sourceCompletion.OutputValueId,
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(source.OutputReferenceId);
        if (!referenceOnlySource &&
            !string.Equals(
                sourceValue.Value ?? string.Empty,
                source.Output ?? string.Empty,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workflow step output reference source '{sourceStepId}/{sourceExecutionId}' changed its committed output.");
        }

        var targetStepId = target.StepId?.Trim() ?? string.Empty;
        if (targetStepId.Length == 0)
            throw new InvalidOperationException("Workflow step output reference has no target step id.");

        var targetExecutionId = target.ExecutionId?.Trim() ?? string.Empty;
        if (targetExecutionId.Length == 0 &&
            state.ExecutionIdsByStepId.TryGetValue(targetStepId, out var expectedTargetExecutionId))
        {
            targetExecutionId = expectedTargetExecutionId?.Trim() ?? string.Empty;
            target.ExecutionId = targetExecutionId;
        }

        var referenceId = BuildOutputReferenceId(
            state.RunId,
            sourceStepId,
            sourceExecutionId,
            sourceCompletion.OutputValueId,
            targetStepId,
            targetExecutionId);
        var reference = new WorkflowPendingOutputReferenceState
        {
            ReferenceId = referenceId,
            SourceStepId = sourceStepId,
            SourceExecutionId = sourceExecutionId,
            SourceValueId = sourceCompletion.OutputValueId,
            TargetStepId = targetStepId,
            TargetExecutionId = targetExecutionId,
        };
        if (normalized.PendingOutputReferences.TryGetValue(referenceId, out var existingReference) &&
            !existingReference.Equals(reference))
        {
            throw new InvalidOperationException(
                $"Workflow output reference id '{referenceId}' collides with another reference.");
        }

        normalized.PendingOutputReferences[referenceId] = reference;
        target.OutputSourceStepId = sourceStepId;
        target.OutputSourceExecutionId = sourceExecutionId;
        target.OutputSourceValueId = sourceCompletion.OutputValueId;
        target.OutputReferenceId = referenceId;
        // Never persist a duplicate raw payload in a normalized pending
        // outbox. Publication rehydrates this field from OutputSourceValueId.
        target.Output = string.Empty;
    }

    internal static WorkflowVariableExpansionDiagnostics ExpandVariablesWithDiagnostics(
        WorkflowExecutionKernelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.NormalizedValues == null)
        {
            return new WorkflowVariableExpansionDiagnostics(
                new Dictionary<string, string>(state.Variables, StringComparer.Ordinal),
                0);
        }

        var view = new WorkflowExecutionVariableView(state);
        var variables = new Dictionary<string, string>(view, StringComparer.Ordinal);
        return new WorkflowVariableExpansionDiagnostics(variables, view.JsonDocumentParseCount);
    }

    private static string ResolveOutputValueId(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion,
        WorkflowCanonicalValueSourceKind producedSourceKind,
        string? forwardedInputValueId,
        out string outputReferenceId)
    {
        outputReferenceId = string.Empty;
        var normalized = state.NormalizedValues!;
        if (!Enum.IsDefined(typeof(WorkflowStepOutputProvenance), completion.OutputProvenance))
        {
            throw new InvalidOperationException(
                $"Normalized workflow completion for step '{completion.StepId}' declares unknown output provenance '{(int)completion.OutputProvenance}'.");
        }

        if (completion.OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput)
        {
            var sourceStepId = completion.OutputSourceStepId?.Trim() ?? string.Empty;
            if (sourceStepId.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Workflow referenced-step completion for step '{completion.StepId}' has no output source step id.");
            }

            var sourceValueId = completion.OutputSourceValueId?.Trim() ?? string.Empty;
            if (sourceValueId.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Workflow referenced-step completion for step '{completion.StepId}' has no canonical source value id.");
            }

            var referenceId = completion.OutputReferenceId?.Trim() ?? string.Empty;
            var sourceExecutionId = completion.OutputSourceExecutionId?.Trim() ?? string.Empty;
            var targetStepId = completion.StepId?.Trim() ?? string.Empty;
            var targetExecutionId = completion.ExecutionId?.Trim() ?? string.Empty;
            if (!normalized.CanonicalValues.TryGetValue(sourceValueId, out var sourceValue))
            {
                throw new InvalidOperationException(
                    $"Workflow referenced-step completion for step '{completion.StepId}' references missing canonical value '{sourceValueId}'.");
            }
            if (sourceValue.Released != null)
                throw WorkflowValueLifecycleException.ReleasedValueAccessed(sourceStepId);

            var referenceOnlyCompletion =
                string.IsNullOrEmpty(completion.Output) &&
                string.Equals(
                    completion.OutputSourceValueId,
                    sourceValueId,
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(completion.OutputReferenceId);
            if (!referenceOnlyCompletion &&
                !string.Equals(
                    sourceValue.Value ?? string.Empty,
                    completion.Output ?? string.Empty,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workflow referenced-step completion for step '{completion.StepId}' changed source step '{sourceStepId}' output.");
            }

            if (!TryMatchOutputReference(
                    normalized,
                    referenceId,
                    sourceStepId,
                    sourceExecutionId,
                    sourceValueId,
                    targetStepId,
                    targetExecutionId))
            {
                throw new InvalidOperationException(
                    $"Workflow referenced-step completion source identity '{sourceStepId}/{sourceExecutionId}' has no exact pending reference '{referenceId}'.");
            }

            outputReferenceId = referenceId;
            return sourceValueId;
        }

        if (!string.IsNullOrWhiteSpace(completion.OutputSourceStepId) ||
            !string.IsNullOrWhiteSpace(completion.OutputSourceExecutionId) ||
            !string.IsNullOrWhiteSpace(completion.OutputSourceValueId) ||
            !string.IsNullOrWhiteSpace(completion.OutputReferenceId))
        {
            throw new InvalidOperationException(
                $"Workflow completion for step '{completion.StepId}' declares output source step '{completion.OutputSourceStepId}' without referenced-step provenance.");
        }

        if (completion.OutputProvenance == WorkflowStepOutputProvenance.ForwardedInput)
        {
            var canonicalInputValueId = producedSourceKind == WorkflowCanonicalValueSourceKind.InternalOutput
                ? forwardedInputValueId?.Trim() ?? normalized.CurrentStepInputValueId
                : normalized.CurrentStepInputValueId;
            if (canonicalInputValueId.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Workflow pass-through completion for step '{completion.StepId}' has no exact canonical input identity.");
            }

            if (!normalized.CanonicalValues.TryGetValue(
                    canonicalInputValueId,
                    out var forwarded))
            {
                throw new InvalidOperationException(
                    $"Workflow pass-through completion references missing canonical input '{canonicalInputValueId}'.");
            }
            if (forwarded.Released != null)
                throw WorkflowValueLifecycleException.ReleasedValueAccessed("input");

            if (!string.Equals(
                    forwarded.Value ?? string.Empty,
                    completion.Output ?? string.Empty,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workflow pass-through completion for step '{completion.StepId}' changed its forwarded input.");
            }

            return canonicalInputValueId;
        }

        if (completion.OutputProvenance != WorkflowStepOutputProvenance.Produced)
        {
            throw new InvalidOperationException(
                $"Normalized workflow completion for step '{completion.StepId}' must declare supported output provenance.");
        }

        return AddCanonicalValue(
            normalized,
            completion.Output ?? string.Empty,
            completion.StepId,
            completion.ExecutionId,
            producedSourceKind);
    }

    private static string ResolveAssignedMirrorValueId(
        WorkflowNormalizedExecutionValuesState normalized,
        StepCompletedEvent completion,
        string outputValueId,
        string stepId)
    {
        var assignedVariable = completion.AssignedVariable?.Trim() ?? string.Empty;
        var declaredAssignedValue = completion.AssignedValue ?? string.Empty;
        if (assignedVariable.Length == 0 && declaredAssignedValue.Length == 0)
            return string.Empty;

        if (assignedVariable.Length == 0)
        {
            throw new InvalidOperationException(
                $"Workflow assigned-value mirror for step '{stepId}' has no target variable.");
        }

        if (!Enum.IsDefined(typeof(WorkflowStepAssignedValueProvenance), completion.AssignedValueProvenance) ||
            completion.AssignedValueProvenance == WorkflowStepAssignedValueProvenance.Unspecified)
        {
            throw new InvalidOperationException(
                $"Workflow assigned-value mirror for step '{stepId}' must declare supported provenance.");
        }

        var assignedMirrorValue = string.IsNullOrWhiteSpace(declaredAssignedValue)
            ? completion.Output ?? string.Empty
            : declaredAssignedValue;
        if (completion.AssignedValueProvenance == WorkflowStepAssignedValueProvenance.ReferencesOutput)
        {
            if (!string.Equals(
                    assignedMirrorValue,
                    completion.Output ?? string.Empty,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workflow assigned-value alias for step '{stepId}' does not match its output.");
            }

            return outputValueId;
        }

        if (completion.AssignedValueProvenance != WorkflowStepAssignedValueProvenance.Produced)
        {
            throw new InvalidOperationException(
                $"Workflow assigned-value mirror for step '{stepId}' has unsupported provenance '{completion.AssignedValueProvenance}'.");
        }

        return AddCanonicalValue(
            normalized,
            assignedMirrorValue,
            stepId,
            completion.ExecutionId,
            WorkflowCanonicalValueSourceKind.AssignedValue);
    }

    private static string ResolveRawAssignedMirrorValueId(
        WorkflowNormalizedExecutionValuesState normalized,
        StepCompletedEvent completion,
        string assignedValueId,
        string stepId)
    {
        if (string.IsNullOrWhiteSpace(assignedValueId))
            return string.Empty;
        if (!normalized.CanonicalValues.TryGetValue(assignedValueId, out var assignedValue))
        {
            throw new InvalidOperationException(
                $"Workflow assigned-value mirror for step '{stepId}' references missing canonical value '{assignedValueId}'.");
        }

        var rawAssignedValue = completion.AssignedValue ?? string.Empty;
        if (string.Equals(
                assignedValue.Value ?? string.Empty,
                rawAssignedValue,
                StringComparison.Ordinal))
        {
            return assignedValueId;
        }

        return AddCanonicalValue(
            normalized,
            rawAssignedValue,
            stepId,
            completion.ExecutionId,
            WorkflowCanonicalValueSourceKind.AssignedValue);
    }

    private static WorkflowUsageMetricsState ToUsageState(
        WorkflowUsageMetrics? usage) =>
        usage == null
            ? new WorkflowUsageMetricsState()
            : new WorkflowUsageMetricsState
            {
                PromptTokens = Math.Max(0, usage.PromptTokens),
                CompletionTokens = Math.Max(0, usage.CompletionTokens),
                TotalTokens = Math.Max(0, usage.TotalTokens),
                Model = usage.Model ?? string.Empty,
                Cost = Math.Max(0, usage.Cost),
                LatencyMs = Math.Max(0, usage.LatencyMs),
            };

    private static bool HasUsage(WorkflowUsageMetrics? usage) =>
        usage != null &&
        (usage.PromptTokens > 0 ||
         usage.CompletionTokens > 0 ||
         usage.TotalTokens > 0 ||
         !string.IsNullOrWhiteSpace(usage.Model) ||
         usage.Cost > 0 ||
         usage.LatencyMs > 0);


    private static string AddCanonicalValue(
        WorkflowNormalizedExecutionValuesState normalized,
        string? value,
        string? producerStepId,
        string? producerExecutionId,
        WorkflowCanonicalValueSourceKind sourceKind)
    {
        if (sourceKind == WorkflowCanonicalValueSourceKind.Unspecified ||
            !Enum.IsDefined(sourceKind))
        {
            throw new InvalidOperationException(
                $"Workflow canonical value has invalid source kind '{sourceKind}'.");
        }

        var requiresProducer = sourceKind is
            WorkflowCanonicalValueSourceKind.StepOutput or
            WorkflowCanonicalValueSourceKind.AssignedValue or
            WorkflowCanonicalValueSourceKind.InternalOutput or
            WorkflowCanonicalValueSourceKind.InternalInput;
        if (requiresProducer &&
            (string.IsNullOrWhiteSpace(producerStepId) ||
             string.IsNullOrWhiteSpace(producerExecutionId)))
        {
            throw new InvalidOperationException(
                $"Workflow canonical value with source kind '{sourceKind}' requires an exact producer step and execution identity.");
        }

        var sequence = checked(normalized.NextValueSequence + 1);
        var valueId = $"value-{sequence.ToString("D20", CultureInfo.InvariantCulture)}";
        var canonical = new WorkflowCanonicalValueState
        {
            ValueId = valueId,
            Value = value ?? string.Empty,
            ProducerStepId = producerStepId ?? string.Empty,
            ProducerExecutionId = producerExecutionId ?? string.Empty,
            SourceKind = sourceKind,
        };
        if (!normalized.CanonicalValues.TryAdd(valueId, canonical))
        {
            throw new InvalidOperationException(
                $"Workflow canonical value sequence {sequence} collides with existing value '{valueId}'.");
        }

        normalized.NextValueSequence = sequence;
        return valueId;
    }

    private static void Bind(
        WorkflowNormalizedExecutionValuesState normalized,
        string? name,
        string valueId,
        WorkflowValueBindingKind kind)
    {
        var key = name?.Trim() ?? string.Empty;
        if (key.Length == 0)
            return;

        normalized.Bindings[key] = new WorkflowValueBindingState
        {
            ValueId = valueId,
            BindingKind = kind,
        };
    }

    private static IReadOnlyList<string> ResolveJsonPropertyAliases(
        StepCompletedEvent completion)
    {
        if (!completion.Success || string.IsNullOrWhiteSpace(completion.Output))
            return [];

        try
        {
            using var document = JsonDocument.Parse(completion.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            return document.RootElement
                .EnumerateObject()
                .Select(static property => property.Name.Trim())
                .Where(static name => name.Length > 0)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void RemoveOverwrittenEngineEntries(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion,
        string stepId,
        IReadOnlyList<string> jsonAliases)
    {
        var prefix = $"{StepPrefix}{stepId}";
        foreach (var key in new[]
                 {
                     stepId,
                     "input",
                     $"{prefix}.output",
                     $"{prefix}.success",
                     $"{prefix}.error",
                     $"{prefix}.branch_key",
                     $"{prefix}.next_step_id",
                     $"{prefix}.assigned_variable",
                     $"{prefix}.assigned_value",
                 })
        {
            RemoveEngineEntry(state, key);
        }

        foreach (var key in completion.Annotations.Keys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                RemoveEngineEntry(state, $"{prefix}.annotations.{key.Trim()}");
        }

        if (HasUsage(completion.Usage))
        {
            foreach (var suffix in new[]
                     {
                         "prompt_tokens",
                         "completion_tokens",
                         "total_tokens",
                         "model",
                         "cost",
                         "latency_ms",
                     })
            {
                RemoveEngineEntry(state, $"{prefix}.usage.{suffix}");
            }
        }

        foreach (var alias in jsonAliases)
            RemoveEngineEntry(state, $"{prefix}.json.{alias}");
    }

    private static void RemoveEngineEntry(
        WorkflowExecutionKernelState state,
        string key)
    {
        state.Variables.Remove(key);
        state.NormalizedValues!.Bindings.Remove(key);
    }

    internal static void ReleaseRunUsageBindings(WorkflowExecutionKernelState state)
    {
        var bindings = state.NormalizedValues?.Bindings;
        if (bindings == null)
            return;

        foreach (var suffix in new[]
                 {
                     "prompt_tokens",
                     "completion_tokens",
                     "total_tokens",
                     "model",
                     "cost",
                     "latency_ms",
                 })
        {
            bindings.Remove($"workflow.usage.{suffix}");
        }
    }

    internal static void PruneUnreferencedValues(WorkflowExecutionKernelState state)
    {
        if (state.NormalizedValues != null)
            RemoveUnreferencedValues(state.NormalizedValues);
    }

    private static void RemoveUnreferencedValues(
        WorkflowNormalizedExecutionValuesState normalized)
    {
        var referenced = normalized.Bindings.Values
            .Select(static binding => binding.ValueId)
            .Concat(normalized.CompletedSteps.Values.Select(static step => step.OutputValueId))
            .Concat(normalized.CompletedSteps.Values.Select(static step => step.AssignedValueId))
            .Concat(normalized.CompletedSteps.Values.Select(static step => step.AssignedMirrorValueId))
            .Concat(normalized.CompletedSteps.Values.SelectMany(
                static step => step.JsonValueBindings.Values.Select(
                    static binding => binding.OutputValueId)))
            .Concat(normalized.AcceptedCompletions.Values.SelectMany(
                CollectCompletionValuesRequiringRawRetention))
            .Concat(normalized.InheritedCompletions.Values.SelectMany(
                CollectCompletionValuesRequiringRawRetention))
            .Concat(normalized.PendingOutputReferences.Values.Select(
                static reference => reference.SourceValueId))
            .Concat(normalized.PendingInternalDispatches.Values.Select(
                static dispatch => dispatch.InputValueId))
            .Concat(normalized.CanonicalValues
                .Where(static pair => pair.Value.Released != null)
                .Select(static pair => pair.Key))
            .Append(normalized.CurrentStepInputValueId)
            .Where(static valueId => !string.IsNullOrWhiteSpace(valueId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var valueId in normalized.CanonicalValues.Keys
                     .Where(valueId => !referenced.Contains(valueId))
                     .ToArray())
        {
            normalized.CanonicalValues.Remove(valueId);
        }
    }

    private static IEnumerable<string> CollectCompletionValuesRequiringRawRetention(
        WorkflowCompletedStepState completed)
    {
        if (!IsAuthoritativeDigest(completed.OutputDigest))
        {
            yield return completed.OutputValueId;
            foreach (var binding in completed.JsonValueBindings.Values)
                yield return binding.OutputValueId;
        }
        if (!IsAuthoritativeDigest(completed.AssignedValueDigest))
            yield return completed.AssignedValueId;
        if (!IsAuthoritativeDigest(completed.AssignedMirrorDigest))
            yield return completed.AssignedMirrorValueId;
    }

    private static bool TryMatchOutputReference(
        WorkflowNormalizedExecutionValuesState normalized,
        string referenceId,
        string sourceStepId,
        string sourceExecutionId,
        string sourceValueId,
        string targetStepId,
        string targetExecutionId)
    {
        if (referenceId.Length > 0 &&
            normalized.PendingOutputReferences.TryGetValue(referenceId, out var pending) &&
            string.Equals(pending.SourceStepId, sourceStepId, StringComparison.Ordinal) &&
            string.Equals(pending.SourceExecutionId, sourceExecutionId, StringComparison.Ordinal) &&
            string.Equals(pending.SourceValueId, sourceValueId, StringComparison.Ordinal) &&
            string.Equals(pending.TargetStepId, targetStepId, StringComparison.Ordinal) &&
            string.Equals(pending.TargetExecutionId, targetExecutionId, StringComparison.Ordinal))
            return true;

        // An exact already-recorded target is an idempotent redelivery after
        // the lease was consumed, not a fresh authority to reference a value.
        return normalized.CompletedSteps.TryGetValue(targetStepId, out var completed) &&
               string.Equals(completed.ExecutionId, targetExecutionId, StringComparison.Ordinal) &&
               string.Equals(completed.OutputValueId, sourceValueId, StringComparison.Ordinal);
    }

    private static void ConsumeOutputReference(
        WorkflowNormalizedExecutionValuesState normalized,
        string referenceId)
    {
        if (referenceId.Length > 0)
            normalized.PendingOutputReferences.Remove(referenceId);
    }

    private static string BuildOutputReferenceId(
        string? runId,
        string sourceStepId,
        string sourceExecutionId,
        string sourceValueId,
        string targetStepId,
        string targetExecutionId)
    {
        var identity = string.Join(
            '\0',
            runId ?? string.Empty,
            sourceStepId,
            sourceExecutionId,
            sourceValueId,
            targetStepId,
            targetExecutionId);
        return $"output-reference-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}";
    }

    private static string BuildInternalDispatchKey(
        string runId,
        string stepId,
        string executionId)
    {
        var identity = string.Join('\0', runId, stepId, executionId);
        return $"internal-dispatch-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}";
    }

    private sealed record ValueReleasePlan(
        string Name,
        string ValueId,
        WorkflowCanonicalValueState Canonical);

    private sealed class WorkflowExecutionVariableView
        : IReadOnlyDictionary<string, string>
    {
        private readonly WorkflowExecutionKernelState _state;
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>>
            _jsonVariablesByValueId = new(StringComparer.Ordinal);

        internal WorkflowExecutionVariableView(WorkflowExecutionKernelState state)
        {
            _state = state;
        }

        internal int JsonDocumentParseCount { get; private set; }

        public int Count => Enumerate().Count();

        public IEnumerable<string> Keys => Enumerate().Select(static item => item.Key);

        public IEnumerable<string> Values => Enumerate().Select(static item => item.Value);

        public string this[string key] =>
            TryGetValue(key, out var value)
                ? value
                : throw new KeyNotFoundException($"Workflow variable '{key}' was not found.");

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, out string value)
        {
            var normalized = _state.NormalizedValues!;
            // A tombstone covers only its own value id: a name re-bound to a new canonical
            // value after the release (per-iteration release) serves the live binding.
            if (normalized.ReleasedBindings.TryGetValue(key, out var tombstone) &&
                (!normalized.Bindings.TryGetValue(key, out var liveBinding) ||
                 string.IsNullOrWhiteSpace(liveBinding.ValueId) ||
                 string.Equals(liveBinding.ValueId, tombstone.ValueId, StringComparison.Ordinal)))
            {
                throw WorkflowValueLifecycleException.ReleasedValueAccessed(key);
            }

            if (normalized.Bindings.TryGetValue(key, out var binding))
            {
                if (!normalized.CanonicalValues.TryGetValue(binding.ValueId, out var canonical))
                {
                    throw new InvalidOperationException(
                        $"Workflow binding '{key}' references missing canonical value '{binding.ValueId}'.");
                }

                if (canonical.Released != null)
                    throw WorkflowValueLifecycleException.ReleasedValueAccessed(key);

                value = canonical.Value ?? string.Empty;
                return true;
            }

            if (_state.Variables.TryGetValue(key, out value!))
                return true;

            if (TryResolveCompletedOutputAlias(normalized, key, out value))
                return true;

            if (TryResolveCompletedStepValue(normalized, key, out value))
                return true;

            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerate().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<KeyValuePair<string, string>> Enumerate()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (stepId, completed) in _state.NormalizedValues!.CompletedSteps)
            {
                if (TryGetCanonicalValue(completed.OutputValueId, out var output, allowReleased: false))
                    result[stepId] = output;
                AddCompletedStepEntries(result, stepId, completed);
            }

            foreach (var (key, value) in _state.Variables)
                result[key] = value;

            foreach (var key in _state.NormalizedValues.Bindings.Keys)
            {
                if (TryGetValue(key, out var value))
                    result[key] = value;
            }

            return result;
        }

        private void AddCompletedStepEntries(
            IDictionary<string, string> target,
            string stepId,
            WorkflowCompletedStepState completed)
        {
            if (!completed.EmitLegacyMirrors)
                return;

            var prefix = $"{StepPrefix}{stepId}";
            target[$"{prefix}.success"] = completed.Success ? "true" : "false";
            target[$"{prefix}.error"] = completed.Error ?? string.Empty;
            target[$"{prefix}.branch_key"] = completed.BranchKey ?? string.Empty;
            target[$"{prefix}.next_step_id"] = completed.NextStepId ?? string.Empty;
            target[$"{prefix}.assigned_variable"] = completed.AssignedVariable ?? string.Empty;
            var assignedMirrorValueId = string.IsNullOrWhiteSpace(completed.AssignedMirrorValueId)
                ? completed.AssignedValueId
                : completed.AssignedMirrorValueId;
            if (string.IsNullOrEmpty(assignedMirrorValueId))
            {
                target[$"{prefix}.assigned_value"] = string.Empty;
            }
            else if (TryGetCanonicalValue(
                         assignedMirrorValueId,
                         out var assignedValue,
                         allowReleased: false))
            {
                target[$"{prefix}.assigned_value"] = assignedValue;
            }
            else
            {
                if (!_state.NormalizedValues!.CanonicalValues.TryGetValue(
                        assignedMirrorValueId,
                        out var assignedCanonical) ||
                    assignedCanonical.Released == null)
                {
                    throw new InvalidOperationException(
                        $"Workflow completion '{stepId}' references missing assigned mirror value '{assignedMirrorValueId}'.");
                }
            }

            foreach (var (key, value) in completed.Annotations)
                target[$"{prefix}.annotations.{key}"] = value ?? string.Empty;

            if (completed.HasUsage)
                AddUsageEntries(target, prefix, completed.Usage);
            AddJsonEntries(target, prefix, completed);
        }

        private bool TryResolveCompletedOutputAlias(
            WorkflowNormalizedExecutionValuesState normalized,
            string key,
            out string value)
        {
            value = string.Empty;
            if (!normalized.CompletedSteps.TryGetValue(key, out var completed))
                return false;
            if (TryGetCanonicalValue(completed.OutputValueId, out value))
                return true;

            throw new InvalidOperationException(
                $"Workflow completion '{key}' references missing output '{completed.OutputValueId}'.");
        }

        private bool TryResolveCompletedStepValue(
            WorkflowNormalizedExecutionValuesState normalized,
            string key,
            out string value)
        {
            value = string.Empty;
            if (!key.StartsWith(StepPrefix, StringComparison.Ordinal))
                return false;

            WorkflowCompletedStepState? completed = null;
            var field = string.Empty;
            foreach (var stepId in normalized.CompletedSteps.Keys
                         .OrderByDescending(static candidate => candidate.Length)
                         .ThenBy(static candidate => candidate, StringComparer.Ordinal))
            {
                var prefix = $"{StepPrefix}{stepId}.";
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                completed = normalized.CompletedSteps[stepId];
                field = key[prefix.Length..];
                break;
            }
            if (completed == null || !completed.EmitLegacyMirrors)
                return false;

            switch (field)
            {
                case "success":
                    value = completed.Success ? "true" : "false";
                    return true;
                case "error":
                    value = completed.Error ?? string.Empty;
                    return true;
                case "branch_key":
                    value = completed.BranchKey ?? string.Empty;
                    return true;
                case "next_step_id":
                    value = completed.NextStepId ?? string.Empty;
                    return true;
                case "assigned_variable":
                    value = completed.AssignedVariable ?? string.Empty;
                    return true;
                case "assigned_value":
                    var assignedMirrorValueId = string.IsNullOrWhiteSpace(completed.AssignedMirrorValueId)
                        ? completed.AssignedValueId
                        : completed.AssignedMirrorValueId;
                    if (string.IsNullOrEmpty(assignedMirrorValueId))
                    {
                        value = string.Empty;
                        return true;
                    }
                    if (TryGetCanonicalValue(assignedMirrorValueId, out value))
                        return true;
                    throw new InvalidOperationException(
                        $"Workflow completion references missing assigned mirror value '{assignedMirrorValueId}'.");
            }

            if (field.StartsWith("annotations.", StringComparison.Ordinal))
                return completed.Annotations.TryGetValue(field["annotations.".Length..], out value!);

            if (completed.HasUsage && field.StartsWith("usage.", StringComparison.Ordinal))
                return TryResolveUsage(completed.Usage, field["usage.".Length..], out value);

            if (field.StartsWith("json.", StringComparison.Ordinal))
            {
                var alias = field["json.".Length..];
                if (!completed.JsonValueBindings.TryGetValue(alias, out var jsonBinding))
                    return false;
                if (!TryGetCanonicalValue(jsonBinding.OutputValueId, out var output))
                {
                    throw new InvalidOperationException(
                        $"Workflow JSON alias '{alias}' references missing output '{jsonBinding.OutputValueId}'.");
                }

                return ReadJsonVariables(jsonBinding.OutputValueId, output)
                    .TryGetValue(alias, out value!);
            }

            return false;
        }

        private bool TryGetCanonicalValue(
            string valueId,
            out string value,
            bool allowReleased = true)
        {
            if (_state.NormalizedValues!.CanonicalValues.TryGetValue(valueId, out var canonical))
            {
                if (canonical.Released != null)
                {
                    if (allowReleased)
                        throw WorkflowValueLifecycleException.ReleasedValueAccessed(valueId);
                    value = string.Empty;
                    return false;
                }
                value = canonical.Value ?? string.Empty;
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static bool TryResolveUsage(
            WorkflowUsageMetricsState? usage,
            string field,
            out string value)
        {
            usage ??= new WorkflowUsageMetricsState();
            value = field switch
            {
                "prompt_tokens" => usage.PromptTokens.ToString(CultureInfo.InvariantCulture),
                "completion_tokens" => usage.CompletionTokens.ToString(CultureInfo.InvariantCulture),
                "total_tokens" => usage.TotalTokens.ToString(CultureInfo.InvariantCulture),
                "model" => usage.Model ?? string.Empty,
                "cost" => usage.Cost.ToString("G17", CultureInfo.InvariantCulture),
                "latency_ms" => usage.LatencyMs.ToString(CultureInfo.InvariantCulture),
                _ => string.Empty,
            };
            return field is "prompt_tokens" or "completion_tokens" or "total_tokens" or
                "model" or "cost" or "latency_ms";
        }

        private static void AddUsageEntries(
            IDictionary<string, string> target,
            string prefix,
            WorkflowUsageMetricsState? usage)
        {
            usage ??= new WorkflowUsageMetricsState();
            target[$"{prefix}.usage.prompt_tokens"] = usage.PromptTokens.ToString(CultureInfo.InvariantCulture);
            target[$"{prefix}.usage.completion_tokens"] = usage.CompletionTokens.ToString(CultureInfo.InvariantCulture);
            target[$"{prefix}.usage.total_tokens"] = usage.TotalTokens.ToString(CultureInfo.InvariantCulture);
            target[$"{prefix}.usage.model"] = usage.Model ?? string.Empty;
            target[$"{prefix}.usage.cost"] = usage.Cost.ToString("G17", CultureInfo.InvariantCulture);
            target[$"{prefix}.usage.latency_ms"] = usage.LatencyMs.ToString(CultureInfo.InvariantCulture);
        }

        private void AddJsonEntries(
            IDictionary<string, string> target,
            string prefix,
            WorkflowCompletedStepState completed)
        {
            foreach (var (alias, binding) in completed.JsonValueBindings)
            {
                if (!TryGetCanonicalValue(
                        binding.OutputValueId,
                        out var output,
                        allowReleased: false))
                {
                    if (!_state.NormalizedValues!.CanonicalValues.TryGetValue(
                            binding.OutputValueId,
                            out var outputCanonical) ||
                        outputCanonical.Released == null)
                    {
                        throw new InvalidOperationException(
                            $"Workflow JSON alias '{alias}' references missing output '{binding.OutputValueId}'.");
                    }
                    continue;
                }

                if (ReadJsonVariables(binding.OutputValueId, output)
                    .TryGetValue(alias, out var value))
                    target[$"{prefix}.json.{alias}"] = value;
            }
        }

        private IReadOnlyDictionary<string, string> ReadJsonVariables(
            string outputValueId,
            string output)
        {
            if (_jsonVariablesByValueId.TryGetValue(outputValueId, out var cached))
                return cached;

            var parsed = ParseJsonVariables(output);
            _jsonVariablesByValueId[outputValueId] = parsed;
            JsonDocumentParseCount++;
            return parsed;
        }

        private static IReadOnlyDictionary<string, string> ParseJsonVariables(string output)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var document = JsonDocument.Parse(output);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return result;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var alias = property.Name.Trim();
                    if (alias.Length > 0)
                        result[alias] = ToVariableValue(property.Value);
                }
            }
            catch (JsonException)
            {
                // The binding was created only from valid JSON; invalid persisted
                // content is surfaced as an absent derived value.
            }

            return result;
        }

        private static string ToVariableValue(JsonElement value) =>
            value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => value.GetRawText(),
            };
    }
}

internal sealed record WorkflowVariableExpansionDiagnostics(
    IReadOnlyDictionary<string, string> Variables,
    int JsonDocumentParseCount);
