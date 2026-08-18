using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowExecutionValueStoreTests
{
    [Fact]
    public void AssignedMirror_ShouldAliasOutputOnlyWithTypedProvenance()
    {
        var independent = NormalizedState();
        var independentOutputId = Record(
            independent,
            new StepCompletedEvent
            {
                StepId = "independent",
                Success = true,
                Output = "same-bytes",
                AssignedVariable = "target",
                AssignedValue = "same-bytes",
            });

        var independentCompletion = independent.NormalizedValues!.CompletedSteps["independent"];
        independent.NormalizedValues.CanonicalValues.Should().HaveCount(2);
        independentCompletion.AssignedValueId.Should().NotBe(independentOutputId);

        var aliased = NormalizedState();
        var aliasedOutputId = Record(
            aliased,
            new StepCompletedEvent
            {
                StepId = "aliased",
                Success = true,
                Output = "same-bytes",
                AssignedVariable = "target",
                AssignedValue = "same-bytes",
                AssignedValueProvenance = WorkflowStepAssignedValueProvenance.ReferencesOutput,
            });

        var aliasedCompletion = aliased.NormalizedValues!.CompletedSteps["aliased"];
        aliased.NormalizedValues.CanonicalValues.Should().ContainSingle();
        aliasedCompletion.AssignedValueId.Should().Be(aliasedOutputId);
    }

    [Fact]
    public void AssignedTargetFallback_ShouldNotChangeRawAssignedMirror()
    {
        var state = NormalizedState();
        Record(
            state,
            new StepCompletedEvent
            {
                StepId = "assign",
                Success = true,
                Output = "fallback-output",
                AssignedVariable = "target",
                AssignedValue = "   ",
            });

        var variables = WorkflowExecutionValueStore.CreateVariableView(state);
        variables["target"].Should().Be("fallback-output");
        variables["steps.assign.assigned_value"].Should().Be("   ");
    }

    [Fact]
    public void RepeatedCompletion_ShouldPreservePerKeyLegacyMirrors()
    {
        var state = NormalizedState();
        var firstOutputId = Record(
            state,
            new StepCompletedEvent
            {
                StepId = "fetch.v2",
                Success = true,
                Output = "{\" foo \":1,\"stale\":2,\"   \":9}",
                AssignedVariable = "first-target",
                AssignedValue = "first-assigned",
                Usage = new WorkflowUsageMetrics { TotalTokens = 3 },
                Annotations = { [" retained "] = "annotation" },
            });

        Record(
            state,
            new StepCompletedEvent
            {
                StepId = "fetch.v2",
                ExecutionId = "fetch.v2-execution-2",
                Success = true,
                Output = "{\" foo \":4}",
                Error = "latest-error",
            });

        var variables = WorkflowExecutionValueStore.CreateVariableView(state);
        variables["steps.fetch.v2.success"].Should().Be("true");
        variables["steps.fetch.v2.error"].Should().Be("latest-error");
        variables["steps.fetch.v2.assigned_variable"].Should().BeEmpty();
        variables["steps.fetch.v2.assigned_value"].Should().BeEmpty();
        variables["steps.fetch.v2.annotations.retained"].Should().Be("annotation");
        variables["steps.fetch.v2.usage.total_tokens"].Should().Be("3");
        variables["steps.fetch.v2.json.foo"].Should().Be("4");
        variables["steps.fetch.v2.json.stale"].Should().Be("2");
        variables.Should().NotContainKey("steps.fetch.v2.json.");
        state.NormalizedValues!.CanonicalValues.Should().ContainKey(firstOutputId);
    }

    [Fact]
    public void CompletionWithoutUsage_ShouldNotCreateUsageAliases()
    {
        var state = NormalizedState();
        Record(
            state,
            new StepCompletedEvent
            {
                StepId = "no-usage",
                Success = true,
                Output = "done",
            });

        var variables = WorkflowExecutionValueStore.CreateVariableView(state);
        variables.Should().NotContainKey("steps.no-usage.usage.total_tokens");
    }

    [Fact]
    public void CurrentCompletionEngineKeys_ShouldOverwriteItsAssignment()
    {
        var cases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["input"] = "{\"foo\":3}",
            ["current"] = "{\"foo\":3}",
            ["steps.current.output"] = "{\"foo\":3}",
            ["steps.current.success"] = "true",
            ["steps.current.error"] = "engine-error",
            ["steps.current.branch_key"] = "engine-branch",
            ["steps.current.next_step_id"] = "engine-next",
            ["steps.current.assigned_variable"] = "steps.current.assigned_variable",
            ["steps.current.assigned_value"] = "author-value",
            ["steps.current.annotations.note"] = "engine-annotation",
            ["steps.current.json.foo"] = "3",
            ["steps.current.usage.total_tokens"] = "9",
            ["steps.current.custom"] = "author-value",
        };

        foreach (var (target, expected) in cases)
        {
            var state = NormalizedState();
            Record(
                state,
                new StepCompletedEvent
                {
                    StepId = "current",
                    Success = true,
                    Output = "{\"foo\":3}",
                    Error = "engine-error",
                    BranchKey = "engine-branch",
                    NextStepId = "engine-next",
                    AssignedVariable = target,
                    AssignedValue = "author-value",
                    Usage = new WorkflowUsageMetrics { TotalTokens = 9 },
                    Annotations = { ["note"] = "engine-annotation" },
                });

            WorkflowExecutionValueStore.CreateVariableView(state)[target]
                .Should().Be(expected, because: target);
        }
    }

    [Fact]
    public void LaterAssignment_ShouldOverwritePriorCompletionKeys()
    {
        var targets = new[]
        {
            "prior",
            "steps.prior.output",
            "steps.prior.success",
            "steps.prior.error",
            "steps.prior.branch_key",
            "steps.prior.json.foo",
            "steps.prior.usage.total_tokens",
            "steps.prior.annotations.note",
            "steps.prior.custom",
        };

        foreach (var target in targets)
        {
            var state = NormalizedState();
            Record(
                state,
                new StepCompletedEvent
                {
                    StepId = "prior",
                    Success = false,
                    Output = "{\"foo\":1}",
                    Error = "prior-error",
                    BranchKey = "prior-branch",
                    Usage = new WorkflowUsageMetrics { TotalTokens = 7 },
                    Annotations = { ["note"] = "prior-annotation" },
                });
            Record(
                state,
                new StepCompletedEvent
                {
                    StepId = "current",
                    Success = true,
                    Output = "current-output",
                    AssignedVariable = target,
                    AssignedValue = "author-value",
                });

            WorkflowExecutionValueStore.CreateVariableView(state)[target]
                .Should().Be("author-value", because: target);
        }
    }

    [Fact]
    public void RunUsageMirror_ShouldOverwriteCollidingStepBinding()
    {
        const string collisionKey = "workflow.usage.total_tokens";
        var state = NormalizedState();
        Record(
            state,
            new StepCompletedEvent
            {
                StepId = collisionKey,
                Success = true,
                Output = "step-output",
                AssignedVariable = collisionKey,
                AssignedValue = "author-value",
            });

        state.Variables[collisionKey] = "25";
        WorkflowExecutionValueStore.ReleaseRunUsageBindings(state);

        WorkflowExecutionValueStore.CreateVariableView(state)[collisionKey].Should().Be("25");
    }

    [Fact]
    public void FiftyForwardingSteps_ShouldRetainOnePayloadInstance()
    {
        var payload = new string('x', 128 * 1024);
        var legacyFixture = LegacyFiftyForwardingStepsFixture(payload);
        var state = NormalizedState();
        var currentValueId = WorkflowExecutionValueStore.CaptureInputValue(
            state,
            payload,
            WorkflowCanonicalValueSourceKind.InitialInput);
        WorkflowExecutionValueStore.SetCurrentStepInput(state, payload, currentValueId);

        for (var index = 0; index < 50; index++)
        {
            currentValueId = Record(
                state,
                new StepCompletedEvent
                {
                    StepId = $"switch-{index}",
                    Success = true,
                    Output = payload,
                    OutputProvenance = WorkflowStepOutputProvenance.ForwardedInput,
                });
            WorkflowExecutionValueStore.SetCurrentStepInput(state, payload, currentValueId);
        }

        state.NormalizedValues!.CanonicalValues.Should().ContainSingle();
        state.NormalizedValues.CompletedSteps.Should().HaveCount(50);
        var normalizedSize = state.CalculateSize();
        normalizedSize.Should().BeLessThan(payload.Length * 2);
        normalizedSize.Should().BeLessThan(legacyFixture.CalculateSize() / 25,
            "the legacy fixture repeats the same payload in step, input, and output mirrors");
    }

    [Fact]
    public void NormalizedSeedCodec_ShouldPreserveInternalCompletionWithoutTransientReference()
    {
        var state = NormalizedState();
        state.RunId = "run-1";
        var source = new StepCompletedEvent
        {
            StepId = "producer",
            ExecutionId = "producer-execution",
            Success = true,
            Output = "payload",
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };
        var sourceValueId = WorkflowExecutionValueStore.RecordInternalOutput(state, source);
        var target = new StepCompletedEvent
        {
            StepId = "wrapper",
            ExecutionId = "wrapper-execution",
            Success = true,
            Output = "payload",
        };
        WorkflowExecutionValueStore.StageCompletedStepOutputReference(state, target, source);

        var seed = WorkflowNormalizedExecutionSeedCodec.Capture(state)!;
        seed.PendingOutputReferences.Should().BeEmpty();
        seed.CanonicalValues.Should().ContainKey(sourceValueId);
        seed.CompletedSteps["producer"].EmitLegacyMirrors.Should().BeFalse();

        var restored = new WorkflowExecutionKernelState();
        WorkflowNormalizedExecutionSeedCodec.Restore(restored, seed);
        var roundTripped = WorkflowNormalizedExecutionSeedCodec.Capture(restored)!;

        roundTripped.Should().Be(seed);
        restored.NormalizedValues!.PendingOutputReferences.Should().BeEmpty();
        var variables = WorkflowExecutionValueStore.CreateVariableView(restored);
        variables["producer"].Should().Be("payload");
        variables.Should().NotContainKey("steps.producer.output");
        variables.Should().NotContainKey("steps.producer.success");
    }

    [Fact]
    public void NestedStepReference_ShouldReuseExactCanonicalSourceValue()
    {
        const string payload = "same-bytes";
        var state = NormalizedState();
        var source = new StepCompletedEvent
        {
            StepId = "race_race_0",
            ExecutionId = "child-execution",
            Success = true,
            Output = payload,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };
        var sourceValueId = WorkflowExecutionValueStore.RecordInternalOutput(state, source);

        var nested = new StepCompletedEvent
        {
            StepId = "race",
            ExecutionId = "race-execution",
            Success = true,
            Output = payload,
        };
        WorkflowExecutionValueStore.StageCompletedStepOutputReference(state, nested, source);
        var nestedValueId = WorkflowExecutionValueStore.RecordInternalOutput(state, nested);
        var outer = new StepCompletedEvent
        {
            StepId = "outer",
            ExecutionId = "outer-execution",
            Success = true,
            Output = payload,
        };
        WorkflowExecutionValueStore.StageCompletedStepOutputReference(state, outer, nested);
        var outerValueId = Record(state, outer);

        nestedValueId.Should().Be(sourceValueId);
        outerValueId.Should().Be(sourceValueId);
        state.NormalizedValues!.CanonicalValues.Should().ContainSingle();
        state.NormalizedValues.CompletedSteps.Values
            .Should().OnlyContain(completed => completed.OutputValueId == sourceValueId);
    }

    [Fact]
    public void NestedStepReference_ShouldPreserveInitialInputOriginAndImmediateCompletionIdentity()
    {
        var state = NormalizedState();
        state.RunId = "run-initial-input";
        var inputValueId = WorkflowExecutionValueStore.CaptureInputValue(
            state,
            "initial-payload",
            WorkflowCanonicalValueSourceKind.InitialInput);
        WorkflowExecutionValueStore.SetCurrentStepInput(state, "initial-payload", inputValueId);
        var passThrough = new StepCompletedEvent
        {
            StepId = "switch",
            ExecutionId = "switch-execution",
            Success = true,
            Output = "initial-payload",
            OutputProvenance = WorkflowStepOutputProvenance.ForwardedInput,
        };
        WorkflowExecutionValueStore.RecordInternalOutput(state, passThrough);
        var wrapper = new StepCompletedEvent
        {
            StepId = "race",
            ExecutionId = "race-execution",
            Success = true,
            Output = "initial-payload",
        };

        WorkflowExecutionValueStore.StageCompletedStepOutputReference(state, wrapper, passThrough);
        var wrapperValueId = WorkflowExecutionValueStore.RecordStepCompletion(state, wrapper);

        wrapperValueId.Should().Be(inputValueId);
        state.NormalizedValues!.CanonicalValues.Should().ContainSingle();
        state.NormalizedValues.CanonicalValues[inputValueId].SourceKind
            .Should().Be(WorkflowCanonicalValueSourceKind.InitialInput);
        state.NormalizedValues.CanonicalValues[inputValueId].ProducerStepId.Should().BeEmpty();
        state.NormalizedValues.CompletedSteps["switch"].ExecutionId.Should().Be("switch-execution");
        state.NormalizedValues.CompletedSteps["race"].ExecutionId.Should().Be("race-execution");
    }

    [Fact]
    public void NestedStepReference_ShouldRejectMissingOrChangedSource()
    {
        var missingState = NormalizedState();
        var missing = () => Record(
            missingState,
            new StepCompletedEvent
            {
                StepId = "parent",
                Success = true,
                Output = "payload",
                OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput,
                OutputSourceStepId = "missing-child",
                OutputSourceExecutionId = "missing-execution",
                OutputSourceValueId = ValueId(1),
            });
        missing.Should().Throw<InvalidOperationException>().WithMessage("*missing canonical value*");

        var changedState = NormalizedState();
        var changedSource = new StepCompletedEvent
        {
            StepId = "child",
            ExecutionId = "child-execution",
            Success = true,
            Output = "original",
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };
        WorkflowExecutionValueStore.RecordInternalOutput(changedState, changedSource);
        var changedTarget = new StepCompletedEvent
        {
            StepId = "parent",
            ExecutionId = "parent-execution",
            Success = true,
            Output = "original",
        };
        WorkflowExecutionValueStore.StageCompletedStepOutputReference(
            changedState,
            changedTarget,
            changedSource);
        changedTarget.Output = "changed";
        var changed = () => Record(changedState, changedTarget);
        changed.Should().Throw<InvalidOperationException>().WithMessage("*changed source step 'child' output*");
    }

    [Fact]
    public void DelayedNestedReference_ShouldResolveExactValueAcrossSourceRetry()
    {
        var state = NormalizedState();
        var firstValueId = WorkflowExecutionValueStore.RecordInternalOutput(
            state,
            new StepCompletedEvent
            {
                StepId = "child",
                ExecutionId = "execution-1",
                Success = true,
                Output = "first",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            });
        var delayedParent = new StepCompletedEvent
        {
            StepId = "parent",
            ExecutionId = "parent-execution",
            Success = true,
            Output = "first",
        };
        WorkflowExecutionValueStore.StageCompletedStepOutputReference(
            state,
            delayedParent,
            new StepCompletedEvent
            {
                StepId = "child",
                ExecutionId = "execution-1",
                Success = true,
                Output = "first",
            });

        var secondValueId = WorkflowExecutionValueStore.RecordInternalOutput(
            state,
            new StepCompletedEvent
            {
                StepId = "child",
                ExecutionId = "execution-2",
                Success = true,
                Output = "second",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            });
        var parentValueId = Record(state, delayedParent);

        parentValueId.Should().Be(firstValueId);
        parentValueId.Should().NotBe(secondValueId);
        state.NormalizedValues!.CompletedSteps["child"].OutputValueId.Should().Be(secondValueId);
        state.NormalizedValues.CompletedSteps["parent"].OutputValueId.Should().Be(firstValueId);
        state.NormalizedValues.CanonicalValues.Should().HaveCount(2);
        state.NormalizedValues.PendingOutputReferences.Should().BeEmpty();
    }

    [Fact]
    public void RepeatedLargeRetryOutputs_ShouldRetainAcceptedCanonicalValuesForReplay()
    {
        var state = NormalizedState();
        for (var attempt = 1; attempt <= 32; attempt++)
        {
            WorkflowExecutionValueStore.RecordInternalOutput(
                state,
                new StepCompletedEvent
                {
                    StepId = "retrying-child",
                    ExecutionId = $"execution-{attempt}",
                    Success = true,
                    Output = $"{attempt}:{new string('x', 64 * 1024)}",
                    OutputProvenance = WorkflowStepOutputProvenance.Produced,
                });
        }

        state.NormalizedValues!.CanonicalValues.Should().HaveCount(32);
        state.NormalizedValues.AcceptedCompletions.Should().HaveCount(32);
        state.NormalizedValues.CanonicalValues[
                state.NormalizedValues.CompletedSteps["retrying-child"].OutputValueId]
            .Value.Should().StartWith("32:");
    }

    [Fact]
    public void OutputSourceStepId_ShouldRequireReferencedStepProvenance()
    {
        var state = NormalizedState();

        var act = () => Record(
            state,
            new StepCompletedEvent
            {
                StepId = "parent",
                Success = true,
                Output = "payload",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
                OutputSourceStepId = "child",
            });

        act.Should().Throw<InvalidOperationException>().WithMessage("*without referenced-step provenance*");
    }

    [Fact]
    public void VariableExpansion_ShouldParseSharedJsonCanonicalValueOncePerView()
    {
        const string json = "{\"alpha\":1,\"beta\":2,\"gamma\":3}";
        var state = NormalizedState();
        var outputValueId = Record(
            state,
            new StepCompletedEvent
            {
                StepId = "producer",
                Success = true,
                Output = json,
            });
        WorkflowExecutionValueStore.SetCurrentStepInput(state, json, outputValueId);
        Record(
            state,
            new StepCompletedEvent
            {
                StepId = "forwarder",
                Success = true,
                Output = json,
                OutputProvenance = WorkflowStepOutputProvenance.ForwardedInput,
            });

        var expansion = WorkflowExecutionValueStore.ExpandVariablesWithDiagnostics(state);

        expansion.JsonDocumentParseCount.Should().Be(1);
        expansion.Variables["steps.producer.json.alpha"].Should().Be("1");
        expansion.Variables["steps.producer.json.gamma"].Should().Be("3");
        expansion.Variables["steps.forwarder.json.beta"].Should().Be("2");
    }

    [Fact]
    public void Restore_ShouldRejectCanonicalSequenceAboveDeclaredWatermark()
    {
        var seed = SeedWithCanonicalValue(sequence: 2, nextValueSequence: 1);

        var act = () => WorkflowNormalizedExecutionSeedCodec.Restore(new WorkflowExecutionKernelState(), seed);

        act.Should().Throw<InvalidOperationException>().WithMessage("*sequence 1*below*2*");
    }

    [Fact]
    public void Restore_ShouldRejectMalformedCanonicalId()
    {
        var seed = new WorkflowNormalizedExecutionSeed { NextValueSequence = 1 };
        seed.CanonicalValues["value-1"] = new WorkflowCanonicalValueSeed
        {
            ValueId = "value-1",
            Value = "payload",
        };

        var act = () => WorkflowNormalizedExecutionSeedCodec.Restore(new WorkflowExecutionKernelState(), seed);

        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid canonical value key*");
    }

    [Fact]
    public void Restore_ShouldRejectUnspecifiedBindingKind()
    {
        var seed = SeedWithCanonicalValue(sequence: 1, nextValueSequence: 1);
        seed.Bindings["input"] = new WorkflowValueBindingSeed
        {
            ValueId = ValueId(1),
            BindingKind = WorkflowValueBindingSeedKind.Unspecified,
        };

        var act = () => WorkflowNormalizedExecutionSeedCodec.Restore(new WorkflowExecutionKernelState(), seed);

        act.Should().Throw<InvalidOperationException>().WithMessage("*binding 'input'*");
    }

    [Fact]
    public void Restore_ShouldRejectMismatchedCompletionKey()
    {
        var seed = SeedWithCanonicalValue(sequence: 1, nextValueSequence: 1);
        seed.CompletedSteps["step-a"] = new WorkflowCompletedStepSeed
        {
            StepId = "step-b",
            OutputValueId = ValueId(1),
        };

        var act = () => WorkflowNormalizedExecutionSeedCodec.Restore(new WorkflowExecutionKernelState(), seed);

        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid completion 'step-a'*");
    }

    [Fact]
    public void Restore_ShouldRejectTransientPendingReference()
    {
        var seed = SeedWithCanonicalValue(sequence: 1, nextValueSequence: 1);
        seed.PendingOutputReferences["reference-1"] = new WorkflowPendingOutputReferenceSeed
        {
            ReferenceId = "reference-1",
            SourceStepId = "source",
            SourceExecutionId = "source-execution",
            SourceValueId = ValueId(1),
            TargetStepId = "target",
            TargetExecutionId = "target-execution",
        };

        var act = () => WorkflowNormalizedExecutionSeedCodec.Restore(
            new WorkflowExecutionKernelState(),
            seed);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot contain transient pending output references*");
    }

    [Fact]
    public void AddCanonicalValue_ShouldFailClosedOnCollision()
    {
        var state = NormalizedState();
        state.NormalizedValues!.CanonicalValues[ValueId(1)] = new WorkflowCanonicalValueState
        {
            ValueId = ValueId(1),
            Value = "original",
        };

        var act = () => WorkflowExecutionValueStore.CaptureInputValue(
            state,
            "replacement",
            WorkflowCanonicalValueSourceKind.InitialInput);

        act.Should().Throw<InvalidOperationException>().WithMessage("*collides*");
        state.NormalizedValues.NextValueSequence.Should().Be(0);
        state.NormalizedValues.CanonicalValues[ValueId(1)].Value.Should().Be("original");
    }

    [Fact]
    public void AddCanonicalValue_ShouldFailClosedOnSequenceOverflow()
    {
        var state = NormalizedState();
        state.NormalizedValues!.NextValueSequence = ulong.MaxValue;

        var act = () => WorkflowExecutionValueStore.CaptureInputValue(
            state,
            "payload",
            WorkflowCanonicalValueSourceKind.InitialInput);

        act.Should().Throw<OverflowException>();
        state.NormalizedValues.CanonicalValues.Should().BeEmpty();
        state.NormalizedValues.NextValueSequence.Should().Be(ulong.MaxValue);
    }

    private static WorkflowExecutionKernelState NormalizedState()
    {
        var state = new WorkflowExecutionKernelState();
        WorkflowExecutionValueStore.Initialize(state);
        return state;
    }

    private static WorkflowExecutionKernelState LegacyFiftyForwardingStepsFixture(string payload)
    {
        var state = new WorkflowExecutionKernelState
        {
            CurrentStepInput = payload,
        };
        state.Variables["input"] = payload;
        for (var index = 0; index < 50; index++)
        {
            var stepId = $"switch-{index}";
            state.Variables[stepId] = payload;
            state.Variables[$"steps.{stepId}.output"] = payload;
        }

        return state;
    }

    private static WorkflowNormalizedExecutionSeed SeedWithCanonicalValue(
        ulong sequence,
        ulong nextValueSequence)
    {
        var valueId = ValueId(sequence);
        var seed = new WorkflowNormalizedExecutionSeed
        {
            NextValueSequence = nextValueSequence,
        };
        seed.CanonicalValues[valueId] = new WorkflowCanonicalValueSeed
        {
            ValueId = valueId,
            Value = "payload",
            SourceKind = WorkflowCanonicalValueSourceKind.InitialInput,
        };
        return seed;
    }

    private static string ValueId(ulong sequence) => $"value-{sequence:D20}";

    private static string Record(
        WorkflowExecutionKernelState state,
        StepCompletedEvent completion)
    {
        if (string.IsNullOrWhiteSpace(completion.ExecutionId))
            completion.ExecutionId = $"{completion.StepId}-execution";
        if (completion.OutputProvenance == WorkflowStepOutputProvenance.Unspecified)
            completion.OutputProvenance = WorkflowStepOutputProvenance.Produced;
        if ((!string.IsNullOrWhiteSpace(completion.AssignedVariable) ||
             !string.IsNullOrEmpty(completion.AssignedValue)) &&
            completion.AssignedValueProvenance == WorkflowStepAssignedValueProvenance.Unspecified)
        {
            completion.AssignedValueProvenance = WorkflowStepAssignedValueProvenance.Produced;
        }
        return WorkflowExecutionValueStore.RecordStepCompletion(state, completion);
    }
}
