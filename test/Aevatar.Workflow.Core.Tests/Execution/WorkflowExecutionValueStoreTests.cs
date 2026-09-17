using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowExecutionValueStoreTests
{
    [Fact]
    public void FailedCompletion_ShouldPreserveCanonicalRetryInput()
    {
        var state = NormalizedState();
        var inputValueId = Record(state, new StepCompletedEvent
        {
            StepId = "seed",
            Success = true,
            Output = "original-input",
        });
        WorkflowExecutionValueStore.SetCurrentStepInput(state, "original-input", inputValueId);

        var failureOutputValueId = Record(
            state,
            new StepCompletedEvent
            {
                StepId = "retryable",
                Success = false,
                Output = "transient-failure-output",
                Error = "try again",
            });

        state.NormalizedValues!.CurrentStepInputValueId.Should().Be(inputValueId);
        state.NormalizedValues.Bindings["input"].ValueId.Should().Be(inputValueId);
        state.NormalizedValues.CompletedSteps["retryable"].OutputValueId
            .Should().Be(failureOutputValueId);
        WorkflowExecutionValueStore.CreateVariableView(state)["input"]
            .Should().Be("original-input");
        WorkflowExecutionValueStore.CreateVariableView(state)["steps.retryable.output"]
            .Should().Be("transient-failure-output");
    }

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
    public void RepeatedLargeRetryOutputs_ShouldRetainDigestEvidenceForExactReplay()
    {
        var state = NormalizedState();
        StepCompletedEvent? first = null;
        for (var attempt = 1; attempt <= 32; attempt++)
        {
            var completion = new StepCompletedEvent
            {
                StepId = "retrying-child",
                ExecutionId = $"execution-{attempt}",
                Success = true,
                Output = $"{attempt}:{new string('x', 64 * 1024)}",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            };
            first ??= completion.Clone();
            WorkflowExecutionValueStore.RecordInternalOutput(
                state,
                completion);
        }

        state.NormalizedValues!.CanonicalValues.Should().ContainSingle();
        state.NormalizedValues.AcceptedCompletions.Should().HaveCount(32);
        state.NormalizedValues.AcceptedCompletions.Values.Should().OnlyContain(snapshot =>
            WorkflowExecutionValueStore.IsAuthoritativeDigest(snapshot.OutputDigest));
        state.NormalizedValues.CanonicalValues[
                state.NormalizedValues.CompletedSteps["retrying-child"].OutputValueId]
            .Value.Should().StartWith("32:");

        var replayed = WorkflowExecutionValueStore.RecordInternalOutput(state, first!);
        replayed.Should().Be(ValueId(1));
        first!.Output = "conflicting";
        var conflict = () => WorkflowExecutionValueStore.RecordInternalOutput(state, first);
        conflict.Should().Throw<InvalidOperationException>().WithMessage("*conflicts*");
    }

    [Fact]
    public void RepeatedRetryOutputs_WithoutSchemaV2Adoption_ShouldKeepRawReplayEvidence()
    {
        // A v1-identity actor (no value-lifecycle receipt) must not record digest evidence,
        // because the digest is what allows the raw replay payload to be pruned; an older
        // reader validates exact replay against the raw canonical text.
        var state = NormalizedState();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            WorkflowExecutionValueStore.RecordInternalOutput(
                state,
                new StepCompletedEvent
                {
                    StepId = "retrying-child",
                    ExecutionId = $"execution-{attempt}",
                    Success = true,
                    Output = $"payload-{attempt}",
                    OutputProvenance = WorkflowStepOutputProvenance.Produced,
                },
                inputValueId: null,
                WorkflowValueReplayEvidence.RawValue);
        }

        state.NormalizedValues!.AcceptedCompletions.Should().HaveCount(3);
        state.NormalizedValues.AcceptedCompletions.Values.Should().OnlyContain(snapshot =>
            !WorkflowExecutionValueStore.IsAuthoritativeDigest(snapshot.OutputDigest));
        state.NormalizedValues.CompletedSteps["retrying-child"].OutputDigest.Should().BeNull();
        state.NormalizedValues.CanonicalValues.Should().HaveCount(3);
        state.NormalizedValues.CanonicalValues.Values.Select(static value => value.Value)
            .Should().BeEquivalentTo(["payload-1", "payload-2", "payload-3"]);
    }

    [Fact]
    public void Release_ShouldSupportPerIterationReleaseOfReboundVariable()
    {
        var state = NormalizedState();
        var firstValueId = Record(state, new StepCompletedEvent
        {
            StepId = "producer",
            ExecutionId = "producer-1",
            Success = true,
            Output = "payload-1",
            AssignedVariable = "raw_pages",
            AssignedValue = "payload-1",
            AssignedValueProvenance = WorkflowStepAssignedValueProvenance.ReferencesOutput,
        });
        Record(state, new StepCompletedEvent
        {
            StepId = "reduce",
            ExecutionId = "reduce-1",
            Success = true,
            Output = "reduced-1",
        });
        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state, Release("raw_pages"), "reduce", "reduce-1");
        var firstRead = () => WorkflowExecutionValueStore.CreateVariableView(state)["raw_pages"];
        firstRead.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);

        // Iteration 2: the releasing step ran again and re-bound the name to a new value.
        var secondValueId = Record(state, new StepCompletedEvent
        {
            StepId = "producer",
            ExecutionId = "producer-2",
            Success = true,
            Output = "payload-2",
            AssignedVariable = "raw_pages",
            AssignedValue = "payload-2",
            AssignedValueProvenance = WorkflowStepAssignedValueProvenance.ReferencesOutput,
        });
        secondValueId.Should().NotBe(firstValueId);

        // The re-bound value is readable: the tombstone covers only the released value id.
        WorkflowExecutionValueStore.CreateVariableView(state)["raw_pages"].Should().Be("payload-2");

        // A redelivery of the first iteration's release while the new value is live is a no-op.
        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state, Release("raw_pages"), "reduce", "reduce-1");
        WorkflowExecutionValueStore.CreateVariableView(state)["raw_pages"].Should().Be("payload-2");

        // The second iteration's release targets the new value and succeeds.
        Record(state, new StepCompletedEvent
        {
            StepId = "reduce",
            ExecutionId = "reduce-2",
            Success = true,
            Output = "reduced-2",
        });
        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state, Release("raw_pages"), "reduce", "reduce-2");

        var tombstone = state.NormalizedValues!.ReleasedBindings["raw_pages"];
        tombstone.ValueId.Should().Be(secondValueId);
        tombstone.ReleasedAfterExecutionId.Should().Be("reduce-2");
        state.NormalizedValues.CanonicalValues[secondValueId].Value.Should().BeEmpty();
        var secondRead = () => WorkflowExecutionValueStore.CreateVariableView(state)["raw_pages"];
        secondRead.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);

        // Redelivery of the second release stays idempotent.
        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state, Release("raw_pages"), "reduce", "reduce-2");

        // A redelivery from the first iteration stays idempotent even after the latest
        // tombstone has moved to the second value identity.
        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state, Release("raw_pages"), "reduce", "reduce-1");
        state.NormalizedValues.ReleasedBindings["raw_pages"].ValueId.Should().Be(secondValueId);
        secondRead.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);
    }

    [Fact]
    public void Release_ShouldRejectReReleaseWhenNameWasNotRebound()
    {
        var state = StateReadyToRelease("payload", out _);
        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state, Release("raw_pages"), "reduce", "reduce-execution");

        var act = () => WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state, Release("raw_pages"), "reduce", "reduce-execution-2");

        act.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);
    }

    [Fact]
    public void ProjectedSeed_ShouldOmitRedactedReplayOnlyCompletionWithoutCanonicalValue()
    {
        var state = NormalizedState();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            WorkflowExecutionValueStore.RecordInternalOutput(
                state,
                new StepCompletedEvent
                {
                    StepId = "retrying-child",
                    ExecutionId = $"execution-{attempt}",
                    Success = true,
                    Output = $"payload-{attempt}",
                    OutputProvenance = WorkflowStepOutputProvenance.Produced,
                });
            state.NormalizedValues!.AcceptedCompletions.Values
                .Single(completion => completion.ExecutionId == $"execution-{attempt}")
                .OutputDigest = null;
        }
        WorkflowExecutionValueStore.MigrateToValueLifecycleV2(state);
        state.NormalizedValues!.CanonicalValues.Should().ContainSingle();
        foreach (var accepted in state.NormalizedValues.AcceptedCompletions.Values)
            accepted.OutputDigest = null;

        var seed = WorkflowNormalizedExecutionSeedCodec.Capture(state)!;

        seed.SourceCompletions.Values.Should().ContainSingle()
            .Which.ExecutionId.Should().Be("execution-2");
        seed.SourceCompletionValueIds.Should().ContainSingle();
        var restored = new WorkflowExecutionKernelState();
        WorkflowNormalizedExecutionSeedCodec.Restore(restored, seed);
        restored.NormalizedValues!.InheritedCompletions.Values.Should().ContainSingle()
            .Which.ExecutionId.Should().Be("execution-2");
    }

    [Fact]
    public void Release_ShouldTombstoneEveryAliasOfOneIdentityOnly()
    {
        const string payload = "same-large-payload";
        var state = StateReadyToRelease(payload, out var releasedValueId);
        WorkflowExecutionValueStore.SetRequestOverride(state, "equal_copy", payload);
        var equalCopyId = WorkflowExecutionValueStore.GetBindingValueId(state, "equal_copy");

        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state,
            Release("raw_pages"),
            "reduce",
            "reduce-execution");

        equalCopyId.Should().NotBe(releasedValueId);
        WorkflowExecutionValueStore.CreateVariableView(state)["equal_copy"].Should().Be(payload);
        var canonical = state.NormalizedValues!.CanonicalValues[releasedValueId];
        canonical.Value.Should().BeEmpty();
        canonical.Released.Should().NotBeNull();
        canonical.Released.ReleasedAfterStepId.Should().Be("reduce");
        WorkflowExecutionValueStore.IsAuthoritativeDigest(canonical.Released.Digest).Should().BeTrue();

        foreach (var alias in new[] { "raw_pages", "producer", "steps.producer.output" })
        {
            var access = () => WorkflowExecutionValueStore.CreateVariableView(state)[alias];
            access.Should().Throw<WorkflowValueLifecycleException>()
                .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);
        }
        WorkflowExecutionValueStore.CreateVariableView(state).Keys.Should().NotContain("raw_pages");
        WorkflowExecutionValueStore.CreateVariableView(state).Keys.Should().Contain("steps.producer.success");
    }

    [Fact]
    public void Release_ShouldPrevalidateAllTargetsBeforeMutation()
    {
        var state = StateReadyToRelease("payload", out var valueId);

        var act = () => WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state,
            Release("raw_pages", "missing"),
            "reduce",
            "reduce-execution");

        act.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleaseTargetMissing);
        state.NormalizedValues!.CanonicalValues[valueId].Value.Should().Be("payload");
        state.NormalizedValues.ReleasedBindings.Should().BeEmpty();
    }

    [Fact]
    public void Release_ShouldRejectLiveAndCompensationPinnedValues()
    {
        var live = NormalizedState();
        var liveId = WorkflowExecutionValueStore.CaptureInputValue(
            live,
            "live",
            WorkflowCanonicalValueSourceKind.InitialInput);
        WorkflowExecutionValueStore.SetRequestOverride(live, "raw_pages", "live");
        live.NormalizedValues!.Bindings["raw_pages"].ValueId = liveId;
        WorkflowExecutionValueStore.SetCurrentStepInput(live, "live", liveId);
        var liveAct = () => WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            live,
            Release("raw_pages"),
            "reduce",
            "execution");
        liveAct.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleaseTargetLive);

        var currentOutput = NormalizedState();
        var currentOutputId = Record(
            currentOutput,
            new StepCompletedEvent
            {
                StepId = "reduce",
                ExecutionId = "reduce-execution",
                Success = true,
                Output = "still-needed",
                AssignedVariable = "result",
                AssignedValue = "still-needed",
                AssignedValueProvenance = WorkflowStepAssignedValueProvenance.ReferencesOutput,
            });
        var currentOutputAct = () => WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            currentOutput,
            Release("result"),
            "reduce",
            "reduce-execution");
        currentOutputAct.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleaseTargetLive);
        currentOutput.NormalizedValues!.CanonicalValues[currentOutputId].Value
            .Should().Be("still-needed");

        var pinned = StateReadyToRelease("payload", out var pinnedId);
        var pinnedAct = () => WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            pinned,
            Release("raw_pages"),
            "reduce",
            "reduce-execution",
            valueId => valueId == pinnedId);
        pinnedAct.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(
                WorkflowValueLifecycleFailureKind.ReleaseTargetPinnedForCompensation);
        pinned.NormalizedValues!.CanonicalValues[pinnedId].Value.Should().Be("payload");
    }

    [Fact]
    public void Release_ShouldRejectPendingOutputAndInternalDispatchReferences()
    {
        var referenced = StateReadyToRelease("payload", out var referencedId);
        WorkflowExecutionValueStore.StageCompletedStepOutputReference(
            referenced,
            new StepCompletedEvent
            {
                StepId = "parent",
                ExecutionId = "parent-execution",
                Success = true,
                Output = "payload",
            },
            new StepCompletedEvent
            {
                StepId = "producer",
                ExecutionId = "producer-execution",
                Success = true,
                Output = "payload",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            });

        var referencedAct = () => WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            referenced,
            Release("raw_pages"),
            "reduce",
            "reduce-execution");

        referencedAct.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleaseTargetLive);
        referenced.NormalizedValues!.CanonicalValues[referencedId].Value.Should().Be("payload");

        var dispatched = StateReadyToRelease("payload", out var dispatchedId);
        dispatched.RunId = "run-dispatch";
        WorkflowExecutionValueStore.PrepareInternalDispatch(
            dispatched,
            new StepRequestEvent
            {
                RunId = "run-dispatch",
                StepId = "child",
                ExecutionId = "child-execution",
                Input = "payload",
                InputValueId = dispatchedId,
            },
            "origin-envelope").Should().BeTrue();

        var dispatchedAct = () => WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            dispatched,
            Release("raw_pages"),
            "reduce",
            "reduce-execution");

        dispatchedAct.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleaseTargetLive);
        dispatched.NormalizedValues!.CanonicalValues[dispatchedId].Value.Should().Be("payload");
    }

    [Fact]
    public void Release_ShouldBeIdempotentAndRequestOverrideShouldRestoreName()
    {
        var state = StateReadyToRelease("old", out var oldValueId);
        var lifecycle = Release("raw_pages");

        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state,
            lifecycle,
            "reduce",
            "reduce-execution");
        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state,
            lifecycle,
            "reduce",
            "reduce-execution");
        WorkflowExecutionValueStore.SetRequestOverride(state, "raw_pages", "new");

        WorkflowExecutionValueStore.CreateVariableView(state)["raw_pages"].Should().Be("new");
        WorkflowExecutionValueStore.GetBindingValueId(state, "raw_pages").Should().NotBe(oldValueId);
        state.NormalizedValues!.CanonicalValues[oldValueId].Released.Should().NotBeNull();
        state.NormalizedValues.ReleasedBindings.Should().NotContainKey("raw_pages");
    }

    [Fact]
    public void NormalizedSeed_ShouldRoundTripTombstonesWithoutExpandingRawAliases()
    {
        var state = StateReadyToRelease("secret-pages", out var releasedValueId);
        WorkflowExecutionValueStore.ReleaseVariablesAfterSuccess(
            state,
            Release("raw_pages"),
            "reduce",
            "reduce-execution");

        var seed = WorkflowNormalizedExecutionSeedCodec.Capture(state)!;
        var restored = new WorkflowExecutionKernelState();
        WorkflowNormalizedExecutionSeedCodec.Restore(restored, seed);
        var expanded = WorkflowNormalizedExecutionSeedCodec.Expand(seed);

        seed.CanonicalValues[releasedValueId].Value.Should().BeEmpty();
        seed.CanonicalValues[releasedValueId].Released.Should().NotBeNull();
        seed.ReleasedBindings.Should().ContainKey("raw_pages");
        expanded.Should().NotContainKey("raw_pages");
        expanded.Values.Should().NotContain("secret-pages");
        restored.NormalizedValues!.CanonicalValues[releasedValueId].Released.Should().NotBeNull();
        var access = () => WorkflowExecutionValueStore.CreateVariableView(restored)["raw_pages"];
        access.Should().Throw<WorkflowValueLifecycleException>()
            .Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);

        var projectedSeed = seed.Clone();
        projectedSeed.CanonicalValues[releasedValueId].Released.Digest =
            new WorkflowValueDigest { Redacted = true };
        foreach (var name in projectedSeed.ReleasedBindings.Keys.ToArray())
        {
            var projectedRelease = projectedSeed.ReleasedBindings[name].Clone();
            projectedRelease.Digest = new WorkflowValueDigest { Redacted = true };
            projectedSeed.ReleasedBindings[name] = projectedRelease;
        }
        foreach (var completed in projectedSeed.CompletedSteps.Values)
        {
            completed.OutputDigest = null;
            completed.AssignedValueDigest = null;
            completed.AssignedMirrorDigest = null;
        }
        foreach (var completed in projectedSeed.SourceCompletions.Values)
        {
            completed.OutputDigest = null;
            completed.AssignedValueDigest = null;
            completed.AssignedMirrorDigest = null;
        }

        var projected = new WorkflowExecutionKernelState();
        WorkflowNormalizedExecutionSeedCodec.Restore(projected, projectedSeed);
        WorkflowNormalizedExecutionSeedCodec.Expand(projectedSeed).Should().NotContainKey("raw_pages");
        WorkflowNormalizedExecutionSeedCodec.ApplyOverrides(
            projected,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["raw_pages"] = "replacement",
            });
        WorkflowExecutionValueStore.CreateVariableView(projected)["raw_pages"]
            .Should().Be("replacement");
    }

    [Fact]
    public void V1ToV2Migration_ShouldPopulateDigestEvidence()
    {
        var state = NormalizedState();
        Record(state, new StepCompletedEvent
        {
            StepId = "producer",
            Success = true,
            Output = "payload",
        });
        foreach (var completed in state.NormalizedValues!.CompletedSteps.Values)
            completed.OutputDigest = null;
        foreach (var completed in state.NormalizedValues.AcceptedCompletions.Values)
            completed.OutputDigest = null;

        WorkflowExecutionValueStore.MigrateToValueLifecycleV2(state);

        WorkflowExecutionValueStore.IsAuthoritativeDigest(
            state.NormalizedValues.CompletedSteps["producer"].OutputDigest).Should().BeTrue();
        WorkflowExecutionValueStore.IsAuthoritativeDigest(
            state.NormalizedValues.AcceptedCompletions.Values.Single().OutputDigest).Should().BeTrue();
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

    private static WorkflowExecutionKernelState StateReadyToRelease(
        string payload,
        out string releasedValueId)
    {
        var state = NormalizedState();
        releasedValueId = Record(state, new StepCompletedEvent
        {
            StepId = "producer",
            Success = true,
            Output = payload,
            AssignedVariable = "raw_pages",
            AssignedValue = payload,
            AssignedValueProvenance = WorkflowStepAssignedValueProvenance.ReferencesOutput,
        });
        Record(state, new StepCompletedEvent
        {
            StepId = "reduce",
            ExecutionId = "reduce-execution",
            Success = true,
            Output = "reduced",
        });
        return state;
    }

    private static WorkflowStepValueLifecycle Release(params string[] names)
    {
        var lifecycle = new WorkflowStepValueLifecycle();
        lifecycle.ReleaseVariablesAfterSuccess.Add(names);
        return lifecycle;
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
