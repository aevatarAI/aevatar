using Aevatar.Workflow.Abstractions;
using System.Globalization;

namespace Aevatar.Workflow.Core.Execution;

/// <summary>
/// Maps the actor-owned normalized kernel state to the cross-layer fork and
/// projection contract. It never infers aliases from equal string content.
/// </summary>
public static class WorkflowNormalizedExecutionSeedCodec
{
    public static WorkflowNormalizedExecutionSeed? Capture(WorkflowExecutionKernelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state.NormalizedValues;
        if (normalized == null)
            return null;

        var seed = new WorkflowNormalizedExecutionSeed
        {
            NextValueSequence = normalized.NextValueSequence,
            CurrentStepInputValueId = normalized.CurrentStepInputValueId,
        };
        var reachableValueIds = CollectSeedValueIds(normalized);
        foreach (var valueId in reachableValueIds)
        {
            if (!normalized.CanonicalValues.TryGetValue(valueId, out var value))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow state references missing canonical value '{valueId}'.");
            }

            seed.CanonicalValues[valueId] = ToSeed(value);
        }
        foreach (var (name, binding) in normalized.Bindings)
            seed.Bindings[name] = ToSeed(binding);
        foreach (var (stepId, completed) in normalized.CompletedSteps)
            seed.CompletedSteps[stepId] = ToSeed(completed);
        CopyCapturableCompletionEvidence(
            normalized,
            normalized.InheritedCompletionValueIds,
            normalized.InheritedCompletions,
            seed);
        CopyCapturableCompletionEvidence(
            normalized,
            normalized.AcceptedCompletionValueIds,
            normalized.AcceptedCompletions,
            seed);
        foreach (var (name, released) in normalized.ReleasedBindings)
            seed.ReleasedBindings[name] = released.Clone();
        seed.Variables.Add(state.Variables);
        return seed;
    }

    public static void Restore(
        WorkflowExecutionKernelState state,
        WorkflowNormalizedExecutionSeed seed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(seed);
        Validate(seed);

        var normalized = new WorkflowNormalizedExecutionValuesState
        {
            NextValueSequence = seed.NextValueSequence,
            CurrentStepInputValueId = seed.CurrentStepInputValueId,
        };
        foreach (var (valueId, value) in seed.CanonicalValues)
            normalized.CanonicalValues[valueId] = ToState(value);
        foreach (var (name, binding) in seed.Bindings)
            normalized.Bindings[name] = ToState(binding);
        foreach (var (stepId, completed) in seed.CompletedSteps)
            normalized.CompletedSteps[stepId] = ToState(completed);
        foreach (var (sourceKey, valueId) in seed.SourceCompletionValueIds)
            normalized.InheritedCompletionValueIds[sourceKey] = valueId;
        foreach (var (sourceKey, source) in seed.SourceCompletions)
            normalized.InheritedCompletions[sourceKey] = ToState(source);
        foreach (var (name, released) in seed.ReleasedBindings)
            normalized.ReleasedBindings[name] = released.Clone();
        state.Variables.Clear();
        state.Variables.Add(seed.Variables);
        state.NormalizedValues = normalized;
    }

    public static void ApplyOverrides(
        WorkflowExecutionKernelState state,
        IDictionary<string, string> overrides)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(overrides);
        if (state.NormalizedValues == null)
        {
            throw new InvalidOperationException(
                "Normalized workflow overrides require normalized value state.");
        }
        foreach (var (name, value) in overrides)
        {
            var key = name?.Trim() ?? string.Empty;
            if (key.Length == 0)
                continue;

            WorkflowExecutionValueStore.SetRequestOverride(
                state,
                key,
                value ?? string.Empty);
        }
    }

    public static Dictionary<string, string> Expand(WorkflowNormalizedExecutionSeed seed)
    {
        var state = new WorkflowExecutionKernelState();
        Restore(state, seed);
        return WorkflowExecutionValueStore.ExpandVariables(state);
    }

    public static string ResolveCurrentInput(WorkflowExecutionKernelState state) =>
        WorkflowExecutionValueStore.ResolveCurrentStepInput(state);

    private static void CopyCapturableCompletionEvidence(
        WorkflowNormalizedExecutionValuesState normalized,
        IDictionary<string, string> valueIds,
        IDictionary<string, WorkflowCompletedStepState> completions,
        WorkflowNormalizedExecutionSeed seed)
    {
        foreach (var (key, completion) in completions)
        {
            if (!valueIds.TryGetValue(key, out var valueId) ||
                !string.Equals(valueId, completion.OutputValueId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow completion evidence '{key}' has inconsistent value identity.");
            }

            if (!HasCapturableCompletionEvidence(normalized, completion))
                continue;

            seed.SourceCompletionValueIds[key] = valueId;
            seed.SourceCompletions[key] = ToSeed(completion);
        }
    }

    private static bool HasCapturableCompletionEvidence(
        WorkflowNormalizedExecutionValuesState normalized,
        WorkflowCompletedStepState completion) =>
        HasCapturableValueEvidence(normalized, completion.OutputValueId, completion.OutputDigest) &&
        HasCapturableOptionalValueEvidence(
            normalized,
            completion.AssignedValueId,
            completion.AssignedValueDigest) &&
        HasCapturableOptionalValueEvidence(
            normalized,
            completion.AssignedMirrorValueId,
            completion.AssignedMirrorDigest);

    private static bool HasCapturableOptionalValueEvidence(
        WorkflowNormalizedExecutionValuesState normalized,
        string? valueId,
        WorkflowValueDigest? digest) =>
        string.IsNullOrWhiteSpace(valueId) || HasCapturableValueEvidence(normalized, valueId, digest);

    private static bool HasCapturableValueEvidence(
        WorkflowNormalizedExecutionValuesState normalized,
        string? valueId,
        WorkflowValueDigest? digest) =>
        !string.IsNullOrWhiteSpace(valueId) &&
        (normalized.CanonicalValues.ContainsKey(valueId) ||
         WorkflowExecutionValueStore.IsAuthoritativeDigest(digest));

    private static HashSet<string> CollectSeedValueIds(
        WorkflowNormalizedExecutionValuesState normalized)
    {
        var valueIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(normalized.CurrentStepInputValueId))
            valueIds.Add(normalized.CurrentStepInputValueId);
        foreach (var binding in normalized.Bindings.Values)
            valueIds.Add(binding.ValueId);
        foreach (var completed in normalized.CompletedSteps.Values)
        {
            valueIds.Add(completed.OutputValueId);
            if (!string.IsNullOrWhiteSpace(completed.AssignedValueId))
                valueIds.Add(completed.AssignedValueId);
            if (!string.IsNullOrWhiteSpace(completed.AssignedMirrorValueId))
                valueIds.Add(completed.AssignedMirrorValueId);
            foreach (var jsonBinding in completed.JsonValueBindings.Values)
                valueIds.Add(jsonBinding.OutputValueId);
        }
        foreach (var valueId in normalized.AcceptedCompletionValueIds.Values)
        {
            if (normalized.CanonicalValues.ContainsKey(valueId))
                valueIds.Add(valueId);
        }
        foreach (var accepted in normalized.AcceptedCompletions.Values)
        {
            if (normalized.CanonicalValues.ContainsKey(accepted.OutputValueId))
                valueIds.Add(accepted.OutputValueId);
            if (!string.IsNullOrWhiteSpace(accepted.AssignedValueId) &&
                normalized.CanonicalValues.ContainsKey(accepted.AssignedValueId))
                valueIds.Add(accepted.AssignedValueId);
            if (!string.IsNullOrWhiteSpace(accepted.AssignedMirrorValueId) &&
                normalized.CanonicalValues.ContainsKey(accepted.AssignedMirrorValueId))
                valueIds.Add(accepted.AssignedMirrorValueId);
            foreach (var jsonBinding in accepted.JsonValueBindings.Values)
            {
                if (normalized.CanonicalValues.ContainsKey(jsonBinding.OutputValueId))
                    valueIds.Add(jsonBinding.OutputValueId);
            }
        }
        foreach (var valueId in normalized.InheritedCompletionValueIds.Values)
        {
            if (normalized.CanonicalValues.ContainsKey(valueId))
                valueIds.Add(valueId);
        }
        foreach (var source in normalized.InheritedCompletions.Values)
        {
            if (normalized.CanonicalValues.ContainsKey(source.OutputValueId))
                valueIds.Add(source.OutputValueId);
            if (!string.IsNullOrWhiteSpace(source.AssignedValueId) &&
                normalized.CanonicalValues.ContainsKey(source.AssignedValueId))
                valueIds.Add(source.AssignedValueId);
            if (!string.IsNullOrWhiteSpace(source.AssignedMirrorValueId) &&
                normalized.CanonicalValues.ContainsKey(source.AssignedMirrorValueId))
                valueIds.Add(source.AssignedMirrorValueId);
            foreach (var jsonBinding in source.JsonValueBindings.Values)
            {
                if (normalized.CanonicalValues.ContainsKey(jsonBinding.OutputValueId))
                    valueIds.Add(jsonBinding.OutputValueId);
            }
        }
        foreach (var (valueId, value) in normalized.CanonicalValues)
        {
            if (value.Released != null)
                valueIds.Add(valueId);
        }

        valueIds.RemoveWhere(string.IsNullOrWhiteSpace);
        return valueIds;
    }

    private static void Validate(WorkflowNormalizedExecutionSeed seed)
    {
        ulong maximumValueSequence = 0;
        foreach (var (valueId, value) in seed.CanonicalValues)
        {
            if (string.IsNullOrWhiteSpace(valueId) ||
                !string.Equals(valueId, value.ValueId, StringComparison.Ordinal) ||
                !TryParseValueSequence(valueId, out var valueSequence) ||
                value.SourceKind == WorkflowCanonicalValueSourceKind.Unspecified ||
                !Enum.IsDefined(value.SourceKind))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed has an invalid canonical value key '{valueId}'.");
            }

            var requiresProducer = value.SourceKind is
                WorkflowCanonicalValueSourceKind.StepOutput or
                WorkflowCanonicalValueSourceKind.AssignedValue or
                WorkflowCanonicalValueSourceKind.InternalOutput or
                WorkflowCanonicalValueSourceKind.InternalInput;
            if (requiresProducer &&
                (string.IsNullOrWhiteSpace(value.ProducerStepId) ||
                 string.IsNullOrWhiteSpace(value.ProducerExecutionId)))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed canonical value '{valueId}' has source kind '{value.SourceKind}' without exact producer identity.");
            }

            if (value.Released != null)
            {
                if (!string.IsNullOrEmpty(value.Value))
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed released value '{valueId}' still contains raw payload.");
                }
                ValidateReleaseTombstone(value.Released, $"canonical value '{valueId}'");
            }

            maximumValueSequence = Math.Max(maximumValueSequence, valueSequence);
        }

        if (seed.NextValueSequence < maximumValueSequence)
        {
            throw new InvalidOperationException(
                $"Normalized workflow seed sequence {seed.NextValueSequence} is below canonical value sequence {maximumValueSequence}.");
        }

        if (seed.PendingOutputReferences.Count != 0)
        {
            throw new InvalidOperationException(
                "Normalized workflow fork seed cannot contain transient pending output references.");
        }

        ValidateSourceCompletionProvenance(seed);

        foreach (var (name, binding) in seed.Bindings)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
                binding.BindingKind == WorkflowValueBindingSeedKind.Unspecified ||
                !Enum.IsDefined(binding.BindingKind) ||
                !seed.CanonicalValues.TryGetValue(binding.ValueId, out var boundValue) ||
                boundValue.Released != null)
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed binding '{name}' references missing canonical value '{binding.ValueId}'.");
            }

            if (binding.BindingKind == WorkflowValueBindingSeedKind.CurrentInput &&
                !string.Equals(name, "input", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed current-input binding has invalid name '{name}'.");
            }

            if (binding.BindingKind == WorkflowValueBindingSeedKind.InternalOutput)
            {
                throw new InvalidOperationException(
                    $"Normalized workflow fork seed exposes transient internal binding '{name}'.");
            }
        }

        var reservedAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (stepId, completed) in seed.CompletedSteps)
        {
            if (string.IsNullOrWhiteSpace(stepId) ||
                !string.Equals(stepId, stepId.Trim(), StringComparison.Ordinal) ||
                !string.Equals(stepId, completed.StepId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(completed.ExecutionId) ||
                !seed.CanonicalValues.TryGetValue(completed.OutputValueId, out var outputValue) ||
                completed.OutputProvenance == WorkflowStepOutputProvenance.Unspecified ||
                !Enum.IsDefined(completed.OutputProvenance) ||
                !Enum.IsDefined(completed.AssignedValueProvenance) ||
                !Enum.IsDefined(completed.FailureOutcome) ||
                !Enum.IsDefined(completed.RetryDisposition) ||
                !Enum.IsDefined(completed.Outcome))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed has an invalid completion '{stepId}'.");
            }

            var outputAlias = $"steps.{stepId}.output";
            var hasOutputBinding = seed.Bindings.TryGetValue(outputAlias, out var outputBinding);
            if (completed.EmitLegacyMirrors && outputValue.Released == null &&
                (!hasOutputBinding ||
                 outputBinding!.BindingKind != WorkflowValueBindingSeedKind.StepOutput ||
                 !string.Equals(outputBinding.ValueId, completed.OutputValueId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed completion '{stepId}' is not aligned with its output binding.");
            }
            if (outputValue.Released != null && hasOutputBinding)
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed released completion '{stepId}' still has an output binding.");
            }
            if (!completed.EmitLegacyMirrors && hasOutputBinding)
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed internal completion '{stepId}' exposes a legacy output binding.");
            }

            switch (completed.OutputProvenance)
            {
                case WorkflowStepOutputProvenance.Produced:
                    var expectedSourceKind = completed.EmitLegacyMirrors
                        ? WorkflowCanonicalValueSourceKind.StepOutput
                        : WorkflowCanonicalValueSourceKind.InternalOutput;
                    if (outputValue.SourceKind != expectedSourceKind ||
                        !string.Equals(outputValue.ProducerStepId, stepId, StringComparison.Ordinal) ||
                        !string.Equals(outputValue.ProducerExecutionId, completed.ExecutionId, StringComparison.Ordinal) ||
                        HasOutputSource(completed))
                    {
                        throw new InvalidOperationException(
                            $"Normalized workflow seed completion '{stepId}' has inconsistent produced output identity.");
                    }
                    break;
                case WorkflowStepOutputProvenance.ForwardedInput:
                    if (HasOutputSource(completed))
                    {
                        throw new InvalidOperationException(
                            $"Normalized workflow seed completion '{stepId}' declares a source for forwarded input.");
                    }
                    break;
                case WorkflowStepOutputProvenance.ReferencedStepOutput:
                    if (string.IsNullOrWhiteSpace(completed.OutputSourceStepId) ||
                        string.IsNullOrWhiteSpace(completed.OutputSourceExecutionId) ||
                        !string.Equals(completed.OutputSourceValueId, completed.OutputValueId, StringComparison.Ordinal) ||
                        !string.Equals(outputValue.ProducerStepId, completed.OutputSourceStepId, StringComparison.Ordinal) ||
                        !string.Equals(outputValue.ProducerExecutionId, completed.OutputSourceExecutionId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Normalized workflow seed completion '{stepId}' has inconsistent referenced output identity.");
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Normalized workflow seed completion '{stepId}' has unsupported output provenance.");
            }

            var assignedVariable = completed.AssignedVariable?.Trim() ?? string.Empty;
            if (assignedVariable.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(completed.AssignedValueId) ||
                    !string.IsNullOrWhiteSpace(completed.AssignedMirrorValueId) ||
                    completed.AssignedValueProvenance != WorkflowStepAssignedValueProvenance.Unspecified)
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed completion '{stepId}' has an orphan assigned value.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(completed.AssignedValueId) ||
                    completed.AssignedValueProvenance == WorkflowStepAssignedValueProvenance.Unspecified ||
                    !seed.CanonicalValues.TryGetValue(completed.AssignedValueId, out var assignedValue) ||
                    string.IsNullOrWhiteSpace(completed.AssignedMirrorValueId) ||
                    !seed.CanonicalValues.ContainsKey(completed.AssignedMirrorValueId))
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed completion '{stepId}' has an invalid assigned-value binding.");
                }

                if (completed.AssignedValueProvenance == WorkflowStepAssignedValueProvenance.ReferencesOutput)
                {
                    if (!string.Equals(completed.AssignedValueId, completed.OutputValueId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Normalized workflow seed completion '{stepId}' assigned alias does not reference its output.");
                    }
                }
                else if (completed.AssignedValueProvenance != WorkflowStepAssignedValueProvenance.Produced ||
                         assignedValue.SourceKind != WorkflowCanonicalValueSourceKind.AssignedValue ||
                         !string.Equals(assignedValue.ProducerStepId, stepId, StringComparison.Ordinal) ||
                         !string.Equals(assignedValue.ProducerExecutionId, completed.ExecutionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed completion '{stepId}' has inconsistent assigned-value provenance.");
                }
            }

            foreach (var (alias, binding) in completed.JsonValueBindings)
            {
                if (string.IsNullOrWhiteSpace(alias) ||
                    !string.Equals(alias, alias.Trim(), StringComparison.Ordinal) ||
                    !seed.CanonicalValues.ContainsKey(binding.OutputValueId) ||
                    !HasAcceptedJsonSource(seed, stepId, alias, binding.OutputValueId))
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed completion '{stepId}' has an invalid JSON source '{binding.OutputValueId}'.");
                }
            }

            AddReservedCompletionAliases(reservedAliases, stepId, completed);
        }

        foreach (var (name, binding) in seed.Bindings)
        {
            if (binding.BindingKind == WorkflowValueBindingSeedKind.RequestOverride &&
                (!WorkflowExecutionValueStore.IsAuthorVariableKey(name) ||
                 reservedAliases.Contains(name)))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed request override '{name}' targets an engine-owned variable key.");
            }

            if (binding.BindingKind == WorkflowValueBindingSeedKind.StepOutput &&
                !seed.CompletedSteps.Values.Any(completed =>
                    string.Equals(name, $"steps.{completed.StepId}.output", StringComparison.Ordinal) &&
                    string.Equals(binding.ValueId, completed.OutputValueId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed has orphan step-output binding '{name}'.");
            }

            if (binding.BindingKind == WorkflowValueBindingSeedKind.AssignedValue &&
                !seed.CompletedSteps.Values.Any(completed =>
                    string.Equals(
                        completed.AssignedVariable?.Trim(),
                        name,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        completed.AssignedValueId,
                        binding.ValueId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed has orphan assigned-value binding '{name}'.");
            }

            if (reservedAliases.Contains(name) &&
                binding.BindingKind is not WorkflowValueBindingSeedKind.StepOutput and
                    not WorkflowValueBindingSeedKind.AssignedValue)
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed binding '{name}' collides with a completion alias.");
            }
        }

        foreach (var name in seed.Variables.Keys)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
                seed.Bindings.ContainsKey(name) ||
                reservedAliases.Contains(name))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed variable '{name}' collides with an authoritative binding or completion alias.");
            }
        }

        foreach (var (name, released) in seed.ReleasedBindings)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
                seed.Bindings.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed released binding '{name}' collides with a live binding.");
            }

            ValidateReleaseTombstone(released, $"binding '{name}'");
            if (!seed.CanonicalValues.Values.Any(value =>
                    value.Released != null && value.Released.Equals(released)))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed released binding '{name}' has no canonical tombstone.");
            }
        }

        var hasCurrentInputValue = !string.IsNullOrWhiteSpace(seed.CurrentStepInputValueId);
        var hasInputBinding = seed.Bindings.TryGetValue("input", out var inputBinding);
        if (hasCurrentInputValue != hasInputBinding ||
            (hasCurrentInputValue &&
             (!seed.CanonicalValues.TryGetValue(seed.CurrentStepInputValueId, out var currentInput) ||
              currentInput.Released != null ||
              inputBinding!.BindingKind != WorkflowValueBindingSeedKind.CurrentInput ||
              !string.Equals(
                  inputBinding.ValueId,
                  seed.CurrentStepInputValueId,
                  StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                $"Normalized workflow seed current input '{seed.CurrentStepInputValueId}' is not aligned with its input binding.");
        }
    }

    private static void ValidateSourceCompletionProvenance(
        WorkflowNormalizedExecutionSeed seed)
    {
        if (seed.SourceCompletionValueIds.Count != seed.SourceCompletions.Count)
        {
            throw new InvalidOperationException(
                "Normalized workflow seed has incomplete source completion provenance.");
        }

        foreach (var (acceptanceKey, accepted) in seed.SourceCompletions)
        {
            var stepId = accepted.StepId?.Trim() ?? string.Empty;
            var executionId = accepted.ExecutionId?.Trim() ?? string.Empty;
            var expectedKey = $"{stepId}\0{executionId}";
            if (stepId.Length == 0 ||
                executionId.Length == 0 ||
                !string.Equals(acceptanceKey, expectedKey, StringComparison.Ordinal) ||
                !seed.SourceCompletionValueIds.TryGetValue(acceptanceKey, out var acceptedValueId) ||
                !string.Equals(acceptedValueId, accepted.OutputValueId, StringComparison.Ordinal) ||
                accepted.OutputProvenance == WorkflowStepOutputProvenance.Unspecified ||
                !Enum.IsDefined(accepted.OutputProvenance) ||
                !Enum.IsDefined(accepted.AssignedValueProvenance) ||
                !Enum.IsDefined(accepted.FailureOutcome) ||
                !Enum.IsDefined(accepted.RetryDisposition) ||
                !Enum.IsDefined(accepted.Outcome))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed has invalid source completion provenance '{acceptanceKey}'.");
            }

            seed.CanonicalValues.TryGetValue(accepted.OutputValueId, out var outputValue);
            if (outputValue == null &&
                !WorkflowExecutionValueStore.IsAuthoritativeDigest(accepted.OutputDigest))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed source completion '{acceptanceKey}' has no digest-backed output evidence.");
            }

            ValidateAcceptedOutputIdentity(accepted, outputValue, stepId);
            ValidateAcceptedAssignmentIdentity(seed, accepted, stepId);
            foreach (var (alias, binding) in accepted.JsonValueBindings)
            {
                if (string.IsNullOrWhiteSpace(alias) ||
                    !string.Equals(alias, alias.Trim(), StringComparison.Ordinal) ||
                    !string.Equals(
                        binding.OutputValueId,
                        accepted.OutputValueId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed accepted completion '{acceptanceKey}' has an invalid JSON source.");
                }
            }
        }
    }

    private static void ValidateAcceptedOutputIdentity(
        WorkflowCompletedStepSeed accepted,
        WorkflowCanonicalValueSeed? outputValue,
        string stepId)
    {
        switch (accepted.OutputProvenance)
        {
            case WorkflowStepOutputProvenance.Produced:
                var expectedSourceKind = accepted.EmitLegacyMirrors
                    ? WorkflowCanonicalValueSourceKind.StepOutput
                    : WorkflowCanonicalValueSourceKind.InternalOutput;
                if (outputValue != null &&
                    (outputValue.SourceKind != expectedSourceKind ||
                     !string.Equals(outputValue.ProducerStepId, stepId, StringComparison.Ordinal) ||
                     !string.Equals(
                         outputValue.ProducerExecutionId,
                         accepted.ExecutionId,
                         StringComparison.Ordinal)) ||
                    HasOutputSource(accepted))
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed accepted completion '{stepId}' has inconsistent produced output identity.");
                }
                return;
            case WorkflowStepOutputProvenance.ForwardedInput:
                if (HasOutputSource(accepted))
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed accepted completion '{stepId}' declares a source for forwarded input.");
                }
                return;
            case WorkflowStepOutputProvenance.ReferencedStepOutput:
                if (string.IsNullOrWhiteSpace(accepted.OutputSourceStepId) ||
                    string.IsNullOrWhiteSpace(accepted.OutputSourceExecutionId) ||
                    !string.Equals(
                        accepted.OutputSourceValueId,
                        accepted.OutputValueId,
                        StringComparison.Ordinal) ||
                    outputValue != null &&
                    (!string.Equals(
                         outputValue.ProducerStepId,
                         accepted.OutputSourceStepId,
                         StringComparison.Ordinal) ||
                     !string.Equals(
                         outputValue.ProducerExecutionId,
                         accepted.OutputSourceExecutionId,
                         StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Normalized workflow seed accepted completion '{stepId}' has inconsistent referenced output identity.");
                }
                return;
            default:
                throw new InvalidOperationException(
                    $"Normalized workflow seed accepted completion '{stepId}' has unsupported output provenance.");
        }
    }

    private static void ValidateAcceptedAssignmentIdentity(
        WorkflowNormalizedExecutionSeed seed,
        WorkflowCompletedStepSeed accepted,
        string stepId)
    {
        var assignedVariable = accepted.AssignedVariable?.Trim() ?? string.Empty;
        if (assignedVariable.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(accepted.AssignedValueId) ||
                !string.IsNullOrWhiteSpace(accepted.AssignedMirrorValueId) ||
                accepted.AssignedValueProvenance != WorkflowStepAssignedValueProvenance.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed accepted completion '{stepId}' has an orphan assigned value.");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(accepted.AssignedValueId) ||
            string.IsNullOrWhiteSpace(accepted.AssignedMirrorValueId))
        {
            throw new InvalidOperationException(
                $"Normalized workflow seed accepted completion '{stepId}' has an invalid assigned value.");
        }
        seed.CanonicalValues.TryGetValue(accepted.AssignedValueId, out var assignedValue);
        seed.CanonicalValues.TryGetValue(accepted.AssignedMirrorValueId, out var assignedMirrorValue);
        if (assignedValue == null &&
            !WorkflowExecutionValueStore.IsAuthoritativeDigest(accepted.AssignedValueDigest) ||
            assignedMirrorValue == null &&
            !WorkflowExecutionValueStore.IsAuthoritativeDigest(accepted.AssignedMirrorDigest))
        {
            throw new InvalidOperationException(
                $"Normalized workflow seed accepted completion '{stepId}' has no digest-backed assigned value.");
        }

        if (accepted.AssignedValueProvenance == WorkflowStepAssignedValueProvenance.ReferencesOutput)
        {
            if (!string.Equals(
                    accepted.AssignedValueId,
                    accepted.OutputValueId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Normalized workflow seed accepted completion '{stepId}' has an invalid assigned alias.");
            }
            return;
        }

        if (accepted.AssignedValueProvenance != WorkflowStepAssignedValueProvenance.Produced ||
            assignedValue != null &&
            (assignedValue.SourceKind != WorkflowCanonicalValueSourceKind.AssignedValue ||
             !string.Equals(assignedValue.ProducerStepId, stepId, StringComparison.Ordinal) ||
             !string.Equals(
                 assignedValue.ProducerExecutionId,
                 accepted.ExecutionId,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Normalized workflow seed accepted completion '{stepId}' has inconsistent assigned-value provenance.");
        }
    }

    private static bool HasAcceptedJsonSource(
        WorkflowNormalizedExecutionSeed seed,
        string stepId,
        string alias,
        string outputValueId) =>
        seed.SourceCompletions.Values.Any(accepted =>
            string.Equals(accepted.StepId, stepId, StringComparison.Ordinal) &&
            string.Equals(accepted.OutputValueId, outputValueId, StringComparison.Ordinal) &&
            accepted.JsonValueBindings.TryGetValue(alias, out var acceptedBinding) &&
            string.Equals(
                acceptedBinding.OutputValueId,
                outputValueId,
                StringComparison.Ordinal));

    private static bool TryParseValueSequence(string valueId, out ulong sequence)
    {
        const string prefix = "value-";
        sequence = 0;
        if (!valueId.StartsWith(prefix, StringComparison.Ordinal) ||
            valueId.Length != prefix.Length + 20 ||
            !ulong.TryParse(
                valueId.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sequence) ||
            sequence == 0)
        {
            return false;
        }

        return string.Equals(
            valueId,
            $"{prefix}{sequence.ToString("D20", CultureInfo.InvariantCulture)}",
            StringComparison.Ordinal);
    }

    private static void ValidateReleaseTombstone(
        WorkflowReleasedValueTombstone? released,
        string owner)
    {
        if (released == null ||
            (!WorkflowExecutionValueStore.IsAuthoritativeDigest(released.Digest) &&
             !WorkflowExecutionValueStore.IsRedactedDigest(released.Digest)) ||
            string.IsNullOrWhiteSpace(released.ReleasedAfterStepId) ||
            string.IsNullOrWhiteSpace(released.ReleasedAfterExecutionId))
        {
            throw new InvalidOperationException(
                $"Normalized workflow seed {owner} has an invalid release tombstone.");
        }
    }

    private static bool HasOutputSource(WorkflowCompletedStepSeed completed) =>
        !string.IsNullOrWhiteSpace(completed.OutputSourceStepId) ||
        !string.IsNullOrWhiteSpace(completed.OutputSourceExecutionId) ||
        !string.IsNullOrWhiteSpace(completed.OutputSourceValueId);

    private static void AddReservedCompletionAliases(
        ISet<string> aliases,
        string stepId,
        WorkflowCompletedStepSeed completed)
    {
        aliases.Add(stepId);
        aliases.Add($"steps.{stepId}.output");
        if (!completed.EmitLegacyMirrors)
            return;

        var prefix = $"steps.{stepId}";
        aliases.Add($"{prefix}.success");
        aliases.Add($"{prefix}.error");
        aliases.Add($"{prefix}.branch_key");
        aliases.Add($"{prefix}.next_step_id");
        aliases.Add($"{prefix}.assigned_variable");
        aliases.Add($"{prefix}.assigned_value");
        foreach (var key in completed.Annotations.Keys)
            aliases.Add($"{prefix}.annotations.{key}");
        if (completed.HasUsage)
        {
            foreach (var key in new[]
                     {
                         "prompt_tokens",
                         "completion_tokens",
                         "total_tokens",
                         "model",
                         "cost",
                         "latency_ms",
                     })
            {
                aliases.Add($"{prefix}.usage.{key}");
            }
        }

        foreach (var alias in completed.JsonValueBindings.Keys)
            aliases.Add($"{prefix}.json.{alias}");
    }

    private static WorkflowCanonicalValueSeed ToSeed(WorkflowCanonicalValueState value) => new()
    {
        ValueId = value.ValueId,
        Value = value.Value,
        ProducerStepId = value.ProducerStepId,
        ProducerExecutionId = value.ProducerExecutionId,
        SourceKind = value.SourceKind,
        Released = value.Released?.Clone(),
    };

    private static WorkflowCanonicalValueState ToState(WorkflowCanonicalValueSeed value) => new()
    {
        ValueId = value.ValueId,
        Value = value.Value,
        ProducerStepId = value.ProducerStepId,
        ProducerExecutionId = value.ProducerExecutionId,
        SourceKind = value.SourceKind,
        Released = value.Released?.Clone(),
    };

    private static WorkflowValueBindingSeed ToSeed(WorkflowValueBindingState binding) => new()
    {
        ValueId = binding.ValueId,
        BindingKind = binding.BindingKind switch
        {
            WorkflowValueBindingKind.StepOutput => WorkflowValueBindingSeedKind.StepOutput,
            WorkflowValueBindingKind.CurrentInput => WorkflowValueBindingSeedKind.CurrentInput,
            WorkflowValueBindingKind.AssignedValue => WorkflowValueBindingSeedKind.AssignedValue,
            WorkflowValueBindingKind.InternalOutput => WorkflowValueBindingSeedKind.InternalOutput,
            WorkflowValueBindingKind.RequestOverride => WorkflowValueBindingSeedKind.RequestOverride,
            _ => throw new InvalidOperationException(
                $"Normalized workflow state has unsupported binding kind '{binding.BindingKind}'."),
        },
    };

    private static WorkflowValueBindingState ToState(WorkflowValueBindingSeed binding) => new()
    {
        ValueId = binding.ValueId,
        BindingKind = binding.BindingKind switch
        {
            WorkflowValueBindingSeedKind.StepOutput => WorkflowValueBindingKind.StepOutput,
            WorkflowValueBindingSeedKind.CurrentInput => WorkflowValueBindingKind.CurrentInput,
            WorkflowValueBindingSeedKind.AssignedValue => WorkflowValueBindingKind.AssignedValue,
            WorkflowValueBindingSeedKind.InternalOutput => WorkflowValueBindingKind.InternalOutput,
            WorkflowValueBindingSeedKind.RequestOverride => WorkflowValueBindingKind.RequestOverride,
            _ => throw new InvalidOperationException(
                $"Normalized workflow seed has unsupported binding kind '{binding.BindingKind}'."),
        },
    };

    private static WorkflowCompletedStepSeed ToSeed(WorkflowCompletedStepState completed)
    {
        var seed = new WorkflowCompletedStepSeed
        {
            StepId = completed.StepId,
            ExecutionId = completed.ExecutionId,
            OutputValueId = completed.OutputValueId,
            Success = completed.Success,
            Error = completed.Error,
            BranchKey = completed.BranchKey,
            NextStepId = completed.NextStepId,
            AssignedVariable = completed.AssignedVariable,
            AssignedValueId = completed.AssignedValueId,
            AssignedMirrorValueId = completed.AssignedMirrorValueId,
            HasUsage = completed.HasUsage,
            EmitLegacyMirrors = completed.EmitLegacyMirrors,
            OutputProvenance = completed.OutputProvenance,
            AssignedValueProvenance = completed.AssignedValueProvenance,
            OutputSourceStepId = completed.OutputSourceStepId,
            OutputSourceExecutionId = completed.OutputSourceExecutionId,
            OutputSourceValueId = completed.OutputSourceValueId,
            OutputReferenceId = completed.OutputReferenceId,
            FailureOutcome = completed.FailureOutcome,
            RetryDisposition = completed.RetryDisposition,
            Outcome = completed.Outcome,
            OutputDigest = completed.OutputDigest?.Clone(),
            AssignedValueDigest = completed.AssignedValueDigest?.Clone(),
            AssignedMirrorDigest = completed.AssignedMirrorDigest?.Clone(),
        };
        seed.Annotations.Add(completed.Annotations);
        foreach (var (alias, binding) in completed.JsonValueBindings)
        {
            seed.JsonValueBindings[alias] = new WorkflowJsonValueBindingSeed
            {
                OutputValueId = binding.OutputValueId,
            };
        }
        if (completed.Usage != null)
            seed.Usage = ToSeed(completed.Usage);
        return seed;
    }

    private static WorkflowCompletedStepState ToState(WorkflowCompletedStepSeed completed)
    {
        var state = new WorkflowCompletedStepState
        {
            StepId = completed.StepId,
            ExecutionId = completed.ExecutionId,
            OutputValueId = completed.OutputValueId,
            Success = completed.Success,
            Error = completed.Error,
            BranchKey = completed.BranchKey,
            NextStepId = completed.NextStepId,
            AssignedVariable = completed.AssignedVariable,
            AssignedValueId = completed.AssignedValueId,
            AssignedMirrorValueId = completed.AssignedMirrorValueId,
            HasUsage = completed.HasUsage,
            EmitLegacyMirrors = completed.EmitLegacyMirrors,
            OutputProvenance = completed.OutputProvenance,
            AssignedValueProvenance = completed.AssignedValueProvenance,
            OutputSourceStepId = completed.OutputSourceStepId,
            OutputSourceExecutionId = completed.OutputSourceExecutionId,
            OutputSourceValueId = completed.OutputSourceValueId,
            OutputReferenceId = completed.OutputReferenceId,
            FailureOutcome = completed.FailureOutcome,
            RetryDisposition = completed.RetryDisposition,
            Outcome = completed.Outcome,
            OutputDigest = completed.OutputDigest?.Clone(),
            AssignedValueDigest = completed.AssignedValueDigest?.Clone(),
            AssignedMirrorDigest = completed.AssignedMirrorDigest?.Clone(),
        };
        state.Annotations.Add(completed.Annotations);
        foreach (var (alias, binding) in completed.JsonValueBindings)
        {
            state.JsonValueBindings[alias] = new WorkflowJsonValueBindingState
            {
                OutputValueId = binding.OutputValueId,
            };
        }
        if (completed.Usage != null)
            state.Usage = ToState(completed.Usage);
        return state;
    }

    private static WorkflowNormalizedUsageMetricsSeed ToSeed(WorkflowUsageMetricsState usage) => new()
    {
        PromptTokens = usage.PromptTokens,
        CompletionTokens = usage.CompletionTokens,
        TotalTokens = usage.TotalTokens,
        Model = usage.Model,
        Cost = usage.Cost,
        LatencyMs = usage.LatencyMs,
    };

    private static WorkflowUsageMetricsState ToState(WorkflowNormalizedUsageMetricsSeed usage) => new()
    {
        PromptTokens = usage.PromptTokens,
        CompletionTokens = usage.CompletionTokens,
        TotalTokens = usage.TotalTokens,
        Model = usage.Model,
        Cost = usage.Cost,
        LatencyMs = usage.LatencyMs,
    };

    private static WorkflowPendingOutputReferenceSeed ToSeed(
        WorkflowPendingOutputReferenceState reference) => new()
        {
            ReferenceId = reference.ReferenceId,
            SourceStepId = reference.SourceStepId,
            SourceExecutionId = reference.SourceExecutionId,
            SourceValueId = reference.SourceValueId,
            TargetStepId = reference.TargetStepId,
            TargetExecutionId = reference.TargetExecutionId,
        };

    private static WorkflowPendingOutputReferenceState ToState(
        WorkflowPendingOutputReferenceSeed reference) => new()
        {
            ReferenceId = reference.ReferenceId,
            SourceStepId = reference.SourceStepId,
            SourceExecutionId = reference.SourceExecutionId,
            SourceValueId = reference.SourceValueId,
            TargetStepId = reference.TargetStepId,
            TargetExecutionId = reference.TargetExecutionId,
        };

}
