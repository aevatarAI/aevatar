using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class IdempotentStepExecutionTests
{
    private static WorkflowDefinition SingleStepWorkflow(string stepId = "step-1") => new()
    {
        Name = "test-workflow",
        Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
        Steps = [new StepDefinition { Id = stepId, Type = "llm_call", TargetRole = "worker" }],
    };

    private static WorkflowDefinition ThreeStepWorkflow() => new()
    {
        Name = "test-resume-workflow",
        Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
        Steps =
        [
            new StepDefinition { Id = "step-a", Type = "transform" },
            new StepDefinition
            {
                Id = "step-b",
                Type = "transform",
                Parameters =
                {
                    ["summary"] = "${concat(step_a_output, ':', topic, ':', input)}",
                    ["topic_value"] = "${topic}",
                },
            },
            new StepDefinition { Id = "step-c", Type = "transform" },
        ],
    };

    private static WorkflowDefinition ValueLifecycleWorkflow(bool consumerReferencesReleasedValue = false) => new()
    {
        Name = "value-lifecycle-workflow",
        Roles = [],
        Steps =
        [
            new StepDefinition { Id = "producer", Type = "transform", Next = "reduce" },
            new StepDefinition
            {
                Id = "reduce",
                Type = "transform",
                Next = "consumer",
                ValueLifecycle = new WorkflowStepValueLifecycle
                {
                    ReleaseVariablesAfterSuccess = { "raw_pages" },
                },
            },
            new StepDefinition
            {
                Id = "consumer",
                Type = "transform",
                Parameters =
                {
                    ["source"] = consumerReferencesReleasedValue ? "${raw_pages}" : "${input}",
                },
            },
        ],
    };

    private static EventEnvelope Wrap(IMessage msg) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(msg),
        Route = new EnvelopeRoute { PublisherActorId = "agent-1" },
    };

    // Serialized by the schema-v0 descriptors. These constants intentionally
    // stay opaque so current descriptors cannot silently regenerate the fixture.
    private const string LegacyWorkflowRunStateAnyBase64 =
        "CjV0eXBlLmdvb2dsZWFwaXMuY29tL2FldmF0YXIud29ya2Zsb3cuV29ya2Zsb3dSdW5TdGF0ZRL/AgoR" +
        "ZGVmaW5pdGlvbi1sZWdhY3kSFm5hbWU6IGxlZ2FjeQpzdGVwczogW10aBmxlZ2FjeSABOgpsZWdhY3kt" +
        "cnVuQgZmYWlsZWRKDGxlZ2FjeS1pbnB1dFoObGVnYWN5IGZhaWx1cmVihgIKGXdvcmtmbG93X2V4ZWN1" +
        "dGlvbl9rZXJuZWwS6AEKQXR5cGUuZ29vZ2xlYXBpcy5jb20vYWV2YXRhci53b3JrZmxvdy5Xb3JrZmxv" +
        "d0V4ZWN1dGlvbktlcm5lbFN0YXRlEqIBGgZzdGVwLWIiFGxlZ2FjeS1jdXJyZW50LWlucHV0KhUKBWlu" +
        "cHV0EgxsZWdhY3ktaW5wdXQqDwoGc3RlcC1hEgVhbHBoYTIKCgZzdGVwLWIQAVoXCgZzdGVwLWISDWxl" +
        "Z2FjeS1leGVjLWJ6NQoGc3RlcC1iEisKCmxlZ2FjeS1ydW4SBnN0ZXAtYhgCIhNsZWdhY3ktcnVuOnN0" +
        "ZXAtYjoykgEMc2NvcGUtbGVnYWN5";

    private const string LegacyWorkflowExecutionKernelStateAnyBase64 =
        "CkF0eXBlLmdvb2dsZWFwaXMuY29tL2FldmF0YXIud29ya2Zsb3cuV29ya2Zsb3dFeGVjdXRpb25LZXJu" +
        "ZWxTdGF0ZRKyAQgBEgpsZWdhY3ktcnVuGgZzdGVwLWIiFGxlZ2FjeS1jdXJyZW50LWlucHV0KhUKBWlu" +
        "cHV0EgxsZWdhY3ktaW5wdXQqDwoGc3RlcC1hEgVhbHBoYTIKCgZzdGVwLWIQAUgBWhcKBnN0ZXAtYhIN" +
        "bGVnYWN5LWV4ZWMtYno1CgZzdGVwLWISKwoKbGVnYWN5LXJ1bhIGc3RlcC1iGAIiE2xlZ2FjeS1ydW46" +
        "c3RlcC1iOjI=";

    [Fact]
    public void SchemaV0WorkflowRunWireFixture_ShouldRehydrateThroughIdentityMigrationWithoutNormalization()
    {
        var packed = Any.Parser.ParseFrom(Convert.FromBase64String(LegacyWorkflowRunStateAnyBase64));
        packed.Is(WorkflowRunState.Descriptor).Should().BeTrue();
        var source = packed.Unpack<WorkflowRunState>();
        var migration = new WorkflowRunStateV0ToV1Migration();

        var migrated = migration.Apply(source);

        migration.FromStateVersion.Should().Be(0);
        migration.ToStateVersion.Should().Be(1);
        migrated.Should().NotBeSameAs(source);
        migrated.Equals(source).Should().BeTrue();
        var kernel = migrated.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        kernel.NormalizedValues.Should().BeNull();
        kernel.PendingWorkflowCompletion.Should().BeNull();
        kernel.Variables.Should().Contain("step-a", "alpha");
        kernel.IdempotencyByStepId["step-b"].IdempotencyKey
            .Should().Be("legacy-run:step-b:2");
    }

    [Fact]
    public void SchemaV1ToV2Migration_ShouldReplaceHistoricalRetryPayloadsWithDigestEvidence()
    {
        var kernel = new WorkflowExecutionKernelState { RunId = "run-migrate-v2" };
        WorkflowExecutionValueStore.Initialize(kernel);
        for (var attempt = 1; attempt <= 32; attempt++)
        {
            WorkflowExecutionValueStore.RecordInternalOutput(
                kernel,
                CreateProducedCompletion(
                    kernel.RunId,
                    "retrying-child",
                    $"execution-{attempt}",
                    $"{attempt}:{new string('x', 64 * 1024)}"));
            kernel.NormalizedValues!.AcceptedCompletions.Values
                .Single(completion => completion.ExecutionId == $"execution-{attempt}")
                .OutputDigest = null;
        }
        kernel.NormalizedValues!.CompletedSteps["retrying-child"].OutputDigest = null;
        kernel.NormalizedValues.CanonicalValues.Should().HaveCount(32);
        var sourceSize = kernel.CalculateSize();
        var source = new WorkflowRunState { RunId = kernel.RunId };
        source.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey] = Any.Pack(kernel);

        var migration = new WorkflowRunStateV1ToV2Migration();
        var migrated = migration.Apply(source);

        migration.FromStateVersion.Should().Be(1);
        migration.ToStateVersion.Should().Be(2);
        var migratedKernel = migrated.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        migratedKernel.NormalizedValues!.CanonicalValues.Should().ContainSingle();
        migratedKernel.NormalizedValues.AcceptedCompletions.Should().HaveCount(32);
        migratedKernel.NormalizedValues.AcceptedCompletions.Values.Should().OnlyContain(completion =>
            WorkflowExecutionValueStore.IsAuthoritativeDigest(completion.OutputDigest));
        migratedKernel.CalculateSize().Should().BeLessThan(sourceSize / 8);
        source.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>()
            .NormalizedValues!.CanonicalValues.Should().HaveCount(32);
    }

    [Fact]
    public async Task SchemaV0KernelWireFixture_ShouldResumePendingDispatchWithLegacyIdentity()
    {
        var packed = Any.Parser.ParseFrom(
            Convert.FromBase64String(LegacyWorkflowExecutionKernelStateAnyBase64));
        packed.Is(WorkflowExecutionKernelState.Descriptor).Should().BeTrue();
        var host = new RecordingStateHost { RunId = "legacy-run" };
        host.States[WorkflowExecutionKernel.ModuleStateKey] = packed;
        var context = new RecordingEventHandlerContext();

        await new WorkflowExecutionKernel(ThreeStepWorkflow(), host).HandleAsync(
            Wrap(new WorkflowExecutionRecoveryRequestedEvent { RunId = "legacy-run" }),
            context,
            CancellationToken.None);

        var request = StepRequests(context).Should().ContainSingle().Subject;
        request.StepId.Should().Be("step-b");
        request.Input.Should().Be("legacy-current-input");
        request.ExecutionId.Should().Be("legacy-exec-b");
        request.IdempotencyKey.Should().Be("legacy-run:step-b:2");
        request.InputValueId.Should().BeEmpty();
        var resumed = LoadKernelState(host);
        resumed.NormalizedValues.Should().BeNull();
        resumed.CurrentStepDispatchPending.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoptedNormalizedState_AfterGateRevocation_ShouldRehydrateButRejectNewRunWithoutMutation(
        bool normalizedFork)
    {
        var now = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var admissionReader = new MutableAdmissionReader(CreateNormalizedAdmission(now));
        var schemaAccessor = new FixedSchemaContextAccessor(CreateNormalizedSchemaContext(now));
        var membershipReader = new FixedMembershipReader(new RuntimeLocalMembershipIdentity(
            7,
            "digest-a",
            "revision-a",
            "member-a",
            "inc-a"));
        var initialHost = CreateAdoptedStateHost(
            schemaAccessor,
            admissionReader,
            membershipReader,
            new FixedTimeProvider(now));
        var initialContext = new RecordingEventHandlerContext();
        var initialKernel = new WorkflowExecutionKernel(SingleStepWorkflow(), initialHost);

        await initialKernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = "run-1",
                Input = "hello",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            initialContext,
            CancellationToken.None);
        var executionId = StepRequests(initialContext).Single().ExecutionId;
        await initialKernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "done",
                ExecutionId = executionId,
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            }),
            initialContext,
            CancellationToken.None);

        var terminalState = LoadKernelState(initialHost);
        terminalState.Active.Should().BeFalse();
        terminalState.NormalizedValues.Should().NotBeNull();
        var normalizedSeed = WorkflowNormalizedExecutionSeedCodec.Capture(terminalState);
        normalizedSeed.Should().NotBeNull();

        admissionReader.Admission.Status = RuntimeFleetCapabilityGateStatus.Revoked;
        var rehydratedHost = CreateAdoptedStateHost(
            schemaAccessor,
            admissionReader,
            membershipReader,
            new FixedTimeProvider(now));
        foreach (var (key, value) in initialHost.States)
            rehydratedHost.States[key] = value.Clone();

        var rehydratedState = LoadKernelState(rehydratedHost);
        rehydratedState.NormalizedValues.Should().NotBeNull(
            "the immutable adoption receipt keeps already-persisted normalized state readable");
        WorkflowNormalizedStateWriteAdmission.IsGranted(schemaAccessor).Should().BeTrue();
        var persistedBeforeRejectedStart =
            rehydratedHost.States[WorkflowExecutionKernel.ModuleStateKey].ToByteArray();

        var rejectedStart = new StartWorkflowEvent
        {
            RunId = "run-2",
            Input = "new-input",
            ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
        };
        if (normalizedFork)
        {
            rejectedStart.ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "run-1",
                StartAtStepId = "step-1",
                NormalizedValues = normalizedSeed!,
            };
        }

        var act = () => WorkflowNormalizedStateWriteAdmission.SelectNewRunRepresentationAsync(
            rehydratedHost,
            rejectedStart.ForkSeed,
            CancellationToken.None);

        if (normalizedFork)
        {
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*live fleet admission*");
        }
        else
        {
            (await act()).Should().Be(WorkflowExecutionValueRepresentation.Legacy);
        }
        rehydratedHost.States[WorkflowExecutionKernel.ModuleStateKey].ToByteArray()
            .Should().Equal(persistedBeforeRejectedStart);
    }

    [Fact]
    public async Task ValueLifecycleAdmission_ShouldRequireV2ReceiptAndLiveGate()
    {
        var v1Host = CreateNormalizedStateHost("run-v1-rejected");
        var v1Act = () => WorkflowNormalizedStateWriteAdmission.SelectNewRunRepresentationAsync(
            v1Host,
            forkSeed: null,
            CancellationToken.None,
            requiresValueLifecycle: true);

        var rejected = await v1Act.Should().ThrowAsync<WorkflowValueLifecycleException>();
        rejected.Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.SchemaUnavailable);

        var v2Host = CreateValueLifecycleStateHost("run-v2-admitted");
        WorkflowNormalizedStateWriteAdmission.IsValueLifecycleGranted(
            v2Host.RuntimeStateSchemaContextReader).Should().BeTrue();
        var selected = await WorkflowNormalizedStateWriteAdmission.SelectNewRunRepresentationAsync(
            v2Host,
            forkSeed: null,
            CancellationToken.None,
            requiresValueLifecycle: true);
        selected.Should().Be(WorkflowExecutionValueRepresentation.Normalized);
    }

    [Fact]
    public async Task ValueLifecycleKernel_WithV1Receipt_ShouldFailBeforeStateMutation()
    {
        var host = CreateNormalizedStateHost("run-lifecycle-v1");
        var kernel = new WorkflowExecutionKernel(ValueLifecycleWorkflow(), host);

        var act = () => kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = host.RunId,
                Input = "request",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            new RecordingEventHandlerContext(),
            CancellationToken.None);

        var rejected = await act.Should().ThrowAsync<WorkflowValueLifecycleException>();
        rejected.Which.Kind.Should().Be(WorkflowValueLifecycleFailureKind.SchemaUnavailable);
        host.States.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueLifecycleKernel_ShouldReleaseIntermediateAcrossReactivationAndComplete()
    {
        const string runId = "run-lifecycle-success";
        var rawPages = new string('x', 64 * 1024);
        var workflow = ValueLifecycleWorkflow();
        var host = CreateValueLifecycleStateHost(runId);
        var context = new RecordingEventHandlerContext();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = runId,
                Input = "request",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            context,
            CancellationToken.None);
        var producer = StepRequests(context).Single(request => request.StepId == "producer");
        var producerCompletion = CreateProducedCompletion(
            runId,
            producer.StepId,
            producer.ExecutionId,
            rawPages);
        producerCompletion.AssignedVariable = "raw_pages";
        producerCompletion.AssignedValue = rawPages;
        producerCompletion.AssignedValueProvenance =
            WorkflowStepAssignedValueProvenance.ReferencesOutput;
        await kernel.HandleAsync(Wrap(producerCompletion), context, CancellationToken.None);
        var reduce = StepRequests(context).Single(request => request.StepId == "reduce");

        kernel = new WorkflowExecutionKernel(workflow, host);
        await kernel.HandleAsync(
            Wrap(CreateProducedCompletion(runId, reduce.StepId, reduce.ExecutionId, "reduced")),
            context,
            CancellationToken.None);

        var state = LoadKernelState(host);
        var releasedValueId = state.NormalizedValues!.CompletedSteps["producer"].OutputValueId;
        state.NormalizedValues.CanonicalValues[releasedValueId].Value.Should().BeEmpty();
        state.NormalizedValues.CanonicalValues[releasedValueId].Released.Should().NotBeNull();
        state.NormalizedValues.ReleasedBindings.Should().ContainKey("raw_pages");
        state.CalculateSize().Should().BeLessThan(rawPages.Length / 2);
        var consumer = StepRequests(context).Single(request => request.StepId == "consumer");
        consumer.Input.Should().Be("reduced");
        consumer.Parameters["source"].Should().Be("reduced");

        await new WorkflowExecutionKernel(workflow, host).HandleAsync(
            Wrap(CreateProducedCompletion(runId, consumer.StepId, consumer.ExecutionId, "done")),
            context,
            CancellationToken.None);

        WorkflowCompletions(context).Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ValueLifecycleKernel_AfterDispatchCrash_ShouldRecoverWithoutRawPayload()
    {
        const string runId = "run-lifecycle-recovery";
        var rawPages = new string('y', 64 * 1024);
        var workflow = ValueLifecycleWorkflow();
        var host = CreateValueLifecycleStateHost(runId);
        var context = new RecordingEventHandlerContext();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = runId,
                Input = "request",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            context,
            CancellationToken.None);
        var producer = StepRequests(context).Single(request => request.StepId == "producer");
        var producerCompletion = CreateProducedCompletion(
            runId,
            producer.StepId,
            producer.ExecutionId,
            rawPages);
        producerCompletion.AssignedVariable = "raw_pages";
        producerCompletion.AssignedValue = rawPages;
        producerCompletion.AssignedValueProvenance =
            WorkflowStepAssignedValueProvenance.ReferencesOutput;
        await kernel.HandleAsync(Wrap(producerCompletion), context, CancellationToken.None);
        var reduce = StepRequests(context).Single(request => request.StepId == "reduce");
        context.FailNextPublishType = typeof(StepRequestEvent);

        await FluentActions.Awaiting(() => kernel.HandleAsync(
                Wrap(CreateProducedCompletion(runId, reduce.StepId, reduce.ExecutionId, "reduced")),
                context,
                CancellationToken.None))
            .Should().ThrowAsync<EventStoreOptimisticConcurrencyException>();

        var crashed = LoadKernelState(host);
        crashed.CurrentStepDispatchPending.Should().BeTrue();
        crashed.NormalizedValues!.ReleasedBindings.Should().ContainKey("raw_pages");
        crashed.NormalizedValues.CanonicalValues.Values.Should().NotContain(value =>
            string.Equals(value.Value, rawPages, StringComparison.Ordinal));

        var recoveredContext = new RecordingEventHandlerContext();
        await new WorkflowExecutionKernel(workflow, host).HandleAsync(
            Wrap(new WorkflowExecutionRecoveryRequestedEvent { RunId = runId }),
            recoveredContext,
            CancellationToken.None);

        StepRequests(recoveredContext).Should().ContainSingle()
            .Which.StepId.Should().Be("consumer");
        LoadKernelState(host).CurrentStepDispatchPending.Should().BeFalse();
    }

    [Fact]
    public async Task ValueLifecycleKernel_WhenNextStepReadsReleasedAlias_ShouldPublishTypedFailure()
    {
        const string runId = "run-lifecycle-failure";
        const string rawPages = "raw-pages";
        var workflow = ValueLifecycleWorkflow(consumerReferencesReleasedValue: true);
        var host = CreateValueLifecycleStateHost(runId);
        var context = new RecordingEventHandlerContext();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = runId,
                Input = "request",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            context,
            CancellationToken.None);
        var producer = StepRequests(context).Single(request => request.StepId == "producer");
        var producerCompletion = CreateProducedCompletion(
            runId,
            producer.StepId,
            producer.ExecutionId,
            rawPages);
        producerCompletion.AssignedVariable = "raw_pages";
        producerCompletion.AssignedValue = rawPages;
        producerCompletion.AssignedValueProvenance =
            WorkflowStepAssignedValueProvenance.ReferencesOutput;
        await kernel.HandleAsync(Wrap(producerCompletion), context, CancellationToken.None);
        var reduce = StepRequests(context).Single(request => request.StepId == "reduce");

        await kernel.HandleAsync(
            Wrap(CreateProducedCompletion(runId, reduce.StepId, reduce.ExecutionId, "reduced")),
            context,
            CancellationToken.None);

        var failure = WorkflowCompletions(context).Should().ContainSingle().Subject;
        failure.Success.Should().BeFalse();
        failure.ValueLifecycleFailureKind.Should().Be(
            WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);
        LoadKernelState(host).PendingWorkflowCompletion!.ValueLifecycleFailureKind.Should().Be(
            WorkflowValueLifecycleFailureKind.ReleasedValueAccessed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizedStart_WithoutEntryStep_ShouldPublishTerminalAndRetainOnlyDurableOutbox(
        bool missingForkEntry)
    {
        var now = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var host = CreateAdoptedStateHost(
            new FixedSchemaContextAccessor(CreateNormalizedSchemaContext(now)),
            new MutableAdmissionReader(CreateNormalizedAdmission(now)),
            new FixedMembershipReader(new RuntimeLocalMembershipIdentity(
                7,
                "digest-a",
                "revision-a",
                "member-a",
                "inc-a")),
            new FixedTimeProvider(now));
        var workflow = missingForkEntry
            ? SingleStepWorkflow()
            : new WorkflowDefinition
            {
                Name = "empty-workflow",
                Roles = [],
                Steps = [],
            };
        var start = new StartWorkflowEvent
        {
            RunId = "run-no-entry",
            Input = "input",
            ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
        };
        if (missingForkEntry)
        {
            start.ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "source-run",
                StartAtStepId = "missing-step",
                NormalizedValues = new WorkflowNormalizedExecutionSeed(),
            };
        }
        var context = new RecordingEventHandlerContext();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(start), context, CancellationToken.None);

        var packedTerminalState = host.GetExecutionState(WorkflowExecutionKernel.ModuleStateKey);
        packedTerminalState.Should().NotBeNull();
        var terminalState = packedTerminalState!.Unpack<WorkflowExecutionKernelState>();
        terminalState.PendingWorkflowCompletion.Should().NotBeNull();
        terminalState.NormalizedValues.Should().BeNull();
        context.Published.Select(static item => item.Event)
            .Count(static payload => payload.Is(WorkflowCompletedEvent.Descriptor))
            .Should().Be(1);
    }

    [Theory]
    [InlineData("start-parameter-input")]
    [InlineData("")]
    public async Task NormalizedFork_InputOverrides_ShouldAlignDispatchAndKeepSourceCompletionProvenanceSeparate(
        string startParameterInput)
    {
        var sourceState = new WorkflowExecutionKernelState { RunId = "source-run" };
        WorkflowExecutionValueStore.Initialize(sourceState);
        var initialValueId = WorkflowExecutionValueStore.CaptureInputValue(
            sourceState,
            "source-input",
            WorkflowCanonicalValueSourceKind.InitialInput);
        WorkflowExecutionValueStore.SetCurrentStepInput(
            sourceState,
            "source-input",
            initialValueId);
        _ = WorkflowExecutionValueStore.RecordStepCompletion(
            sourceState,
            new StepCompletedEvent
            {
                StepId = "step-a",
                RunId = "source-run",
                ExecutionId = "source-execution",
                Success = true,
                Output = "{\"field\":\"source\"}",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            });
        var seed = WorkflowNormalizedExecutionSeedCodec.Capture(sourceState)!;
        seed.SourceCompletions.Should().ContainSingle();

        var now = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var host = CreateAdoptedStateHost(
            new FixedSchemaContextAccessor(CreateNormalizedSchemaContext(now)),
            new MutableAdmissionReader(CreateNormalizedAdmission(now)),
            new FixedMembershipReader(new RuntimeLocalMembershipIdentity(
                7,
                "digest-a",
                "revision-a",
                "member-a",
                "inc-a")),
            new FixedTimeProvider(now));
        var context = new RecordingEventHandlerContext();
        var start = new StartWorkflowEvent
        {
            RunId = "target-run",
            Input = "request-input",
            ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "source-run",
                StartAtStepId = "step-b",
                NormalizedValues = seed,
            },
        };
        start.ForkSeed.VariableOverrides["input"] = "fork-input";
        start.Parameters["input"] = startParameterInput;

        await new WorkflowExecutionKernel(ThreeStepWorkflow(), host).HandleAsync(
            Wrap(start),
            context,
            CancellationToken.None);

        var request = StepRequests(context).Should().ContainSingle().Subject;
        request.StepId.Should().Be("step-b");
        request.Input.Should().Be(startParameterInput);
        request.InputValueId.Should().NotBeNullOrWhiteSpace();
        var targetState = LoadKernelState(host);
        targetState.NormalizedValues!.AcceptedCompletions.Should().BeEmpty();
        targetState.NormalizedValues.AcceptedCompletionValueIds.Should().BeEmpty();
        targetState.NormalizedValues.InheritedCompletions.Should().ContainSingle();
        targetState.NormalizedValues.Bindings["input"].ValueId.Should().Be(request.InputValueId);
        targetState.NormalizedValues.CurrentStepInputValueId.Should().Be(request.InputValueId);
        targetState.NormalizedValues.CanonicalValues[request.InputValueId].Value
            .Should().Be(startParameterInput);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("retry")]
    [InlineData("reactivation")]
    [InlineData("fork")]
    public async Task NormalizedExpressions_ShouldPreserveCanonicalDataflowAcrossExecutionModes(string mode)
    {
        const string sourceOutput = "{\"route\":\"match\",\"detail\":\"canonical-detail\"}";
        var workflow = CreateNormalizedExpressionWorkflow();
        var runId = $"run-expression-{mode}";
        var host = CreateNormalizedStateHost(runId);
        var context = new RecordingEventHandlerContext();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        StepRequestEvent routeRequest;

        if (string.Equals(mode, "fork", StringComparison.Ordinal))
        {
            var sourceState = new WorkflowExecutionKernelState { RunId = "source-expression-run" };
            WorkflowExecutionValueStore.Initialize(sourceState);
            var initialValueId = WorkflowExecutionValueStore.CaptureInputValue(
                sourceState,
                "source-input",
                WorkflowCanonicalValueSourceKind.InitialInput);
            WorkflowExecutionValueStore.SetCurrentStepInput(sourceState, "source-input", initialValueId);
            _ = WorkflowExecutionValueStore.RecordStepCompletion(
                sourceState,
                CreateProducedCompletion(
                    "source-expression-run",
                    "producer",
                    "source-producer-execution",
                    sourceOutput));

            await kernel.HandleAsync(
                Wrap(new StartWorkflowEvent
                {
                    RunId = runId,
                    Input = "ignored-fork-request-input",
                    ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
                    ForkSeed = new WorkflowRunForkSeed
                    {
                        SourceRunId = "source-expression-run",
                        StartAtStepId = "route",
                        NormalizedValues = WorkflowNormalizedExecutionSeedCodec.Capture(sourceState),
                    },
                }),
                context,
                CancellationToken.None);
            routeRequest = StepRequests(context).Should().ContainSingle().Subject;
        }
        else
        {
            await kernel.HandleAsync(
                Wrap(new StartWorkflowEvent
                {
                    RunId = runId,
                    Input = "initial-input",
                    ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
                }),
                context,
                CancellationToken.None);
            var producerRequest = StepRequests(context).Should().ContainSingle().Subject;

            if (string.Equals(mode, "reactivation", StringComparison.Ordinal))
            {
                var reactivatedHost = CreateNormalizedStateHost(runId);
                foreach (var (key, value) in host.States)
                    reactivatedHost.States[key] = value.Clone();
                host = reactivatedHost;
                context = new RecordingEventHandlerContext();
                kernel = new WorkflowExecutionKernel(workflow, host);
            }

            if (string.Equals(mode, "retry", StringComparison.Ordinal))
            {
                var firstExecutionId = producerRequest.ExecutionId;
                await kernel.HandleAsync(
                    Wrap(CreateProducedCompletion(
                        runId,
                        "producer",
                        producerRequest.ExecutionId,
                        "transient-output",
                        success: false,
                        error: "transient failure")),
                    context,
                    CancellationToken.None);
                producerRequest = StepRequests(context)
                    .Last(request => request.StepId == "producer");
                producerRequest.ExecutionId.Should().NotBe(firstExecutionId);
            }

            await kernel.HandleAsync(
                Wrap(CreateProducedCompletion(
                    runId,
                    "producer",
                    producerRequest.ExecutionId,
                    sourceOutput)),
                context,
                CancellationToken.None);
            routeRequest = StepRequests(context)
                .Should().ContainSingle(request => request.StepId == "route")
                .Subject;
        }

        routeRequest.Input.Should().Be(sourceOutput);
        routeRequest.Parameters["on"].Should().Be("match");

        var bridge = new WorkflowExecutionBridgeModule(
            [new SwitchModule(), new AssignModule()],
            host);
        await bridge.HandleAsync(Wrap(routeRequest), context, CancellationToken.None);
        var routeCompletion = StepCompletions(context)
            .Should().ContainSingle(completion => completion.StepId == "route")
            .Subject;
        routeCompletion.BranchKey.Should().Be("match");
        await kernel.HandleAsync(Wrap(routeCompletion), context, CancellationToken.None);

        var assignRequest = StepRequests(context)
            .Should().ContainSingle(request => request.StepId == "assign")
            .Subject;
        assignRequest.Input.Should().Be(sourceOutput);
        assignRequest.Parameters["value"].Should().Be("$input");
        await bridge.HandleAsync(Wrap(assignRequest), context, CancellationToken.None);
        var assignCompletion = StepCompletions(context)
            .Should().ContainSingle(completion => completion.StepId == "assign")
            .Subject;
        assignCompletion.AssignedVariable.Should().Be("selected");
        assignCompletion.AssignedValue.Should().Be(sourceOutput);
        assignCompletion.AssignedValueProvenance
            .Should().Be(WorkflowStepAssignedValueProvenance.ReferencesOutput);
        await kernel.HandleAsync(Wrap(assignCompletion), context, CancellationToken.None);

        var consumerRequest = StepRequests(context)
            .Should().ContainSingle(request => request.StepId == "consumer")
            .Subject;
        consumerRequest.Parameters.Should().Contain("legacy_step", sourceOutput);
        consumerRequest.Parameters.Should().Contain("step_output", sourceOutput);
        consumerRequest.Parameters.Should().Contain("json_field", "canonical-detail");
        consumerRequest.Parameters.Should().Contain("input_value", sourceOutput);
        consumerRequest.Parameters.Should().Contain("assigned_value", sourceOutput);
        consumerRequest.Parameters.Should().Contain("switch_branch", "match");
        consumerRequest.Parameters.Should().Contain("assign_output", sourceOutput);
        consumerRequest.InputValueId.Should().NotBeNullOrWhiteSpace();

        var finalState = LoadKernelState(host);
        finalState.NormalizedValues.Should().NotBeNull();
        finalState.Variables.Keys.Should().NotContain(key =>
            string.Equals(key, "producer", StringComparison.Ordinal) ||
            key.StartsWith("steps.producer", StringComparison.Ordinal) ||
            string.Equals(key, "selected", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeTerminalState_AfterTerminalDeliveryCommit_ShouldDiscardDeliveredOutbox()
    {
        var state = new WorkflowExecutionKernelState
        {
            Active = true,
            RunId = "run-terminal",
            PendingWorkflowCompletion = new WorkflowCompletedEvent
            {
                RunId = "run-terminal",
                WorkflowName = "test-workflow",
                Success = true,
                Output = "done",
            },
        };

        WorkflowExecutionKernel.NormalizeTerminalState(state).Should().BeTrue();

        state.PendingWorkflowCompletion.Should().BeNull(
            "the committed WorkflowCompletedEvent proves the durable terminal outbox was delivered");
    }

    [Fact]
    public async Task AcceptedSuccessReplay_AfterRetryBackoffCleanupCrash_ShouldCaptureOutcomeAndUsageBeforeSuccessor()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "compensable-recovery",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    Compensation = "refund",
                },
                new StepDefinition { Id = "done", Type = "transform" },
                new StepDefinition { Id = "refund", Type = "tool_call" },
            ],
        };
        var now = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var host = CreateAdoptedStateHost(
            new FixedSchemaContextAccessor(CreateNormalizedSchemaContext(now)),
            new MutableAdmissionReader(CreateNormalizedAdmission(now)),
            new FixedMembershipReader(new RuntimeLocalMembershipIdentity(
                7,
                "digest-a",
                "revision-a",
                "member-a",
                "inc-a")),
            new FixedTimeProvider(now));
        var context = new RecordingEventHandlerContext();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = "run-compensable",
                Input = "order-1",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            context,
            CancellationToken.None);
        var chargeRequest = StepRequests(context).Should().ContainSingle().Subject;
        var preCompletionState = LoadKernelState(host);
        preCompletionState.RetryBackoffsByStepId["charge"] = new RetryBackoffState
        {
            CallbackId = "retry-charge",
            DelayMs = 100,
            NextAttempt = 2,
        };
        preCompletionState.RetryAttemptsByStepId["charge"] = 1;
        await host.UpsertExecutionStateAsync(
            WorkflowExecutionKernel.ModuleStateKey,
            Any.Pack(preCompletionState),
            CancellationToken.None);
        host.FailNextCompensableOutcomeCommit = true;
        var completion = new StepCompletedEvent
        {
            StepId = "charge",
            RunId = "run-compensable",
            ExecutionId = chargeRequest.ExecutionId,
            Success = true,
            Output = "charge-ok",
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
            Usage = new WorkflowUsageMetrics
            {
                PromptTokens = 10,
                CompletionTokens = 15,
                TotalTokens = 25,
                LatencyMs = 123,
            },
        };

        await FluentActions.Awaiting(() => kernel.HandleAsync(
                Wrap(completion),
                context,
                CancellationToken.None))
            .Should().ThrowAsync<EventStoreOptimisticConcurrencyException>();

        var crashState = LoadKernelState(host);
        crashState.NormalizedValues!.AcceptedCompletions.Values.Should().ContainSingle()
            .Which.StepId.Should().Be("charge");
        crashState.RetryBackoffsByStepId.Should().NotContainKey("charge");
        crashState.Usage.TotalTokens.Should().Be(25);
        host.CompensableOutcomes.Should().BeEmpty();
        context.Published.Clear();

        await new WorkflowExecutionKernel(workflow, host).HandleAsync(
            Wrap(completion.Clone()),
            context,
            CancellationToken.None);

        var recoveredOutcome = host.CompensableOutcomes.Should().ContainSingle().Subject;
        recoveredOutcome.StepId.Should().Be("charge");
        recoveredOutcome.CapturedOutputValueId.Should().Be(
            crashState.NormalizedValues.CompletedSteps["charge"].OutputValueId);
        StepRequests(context).Should().ContainSingle()
            .Which.StepId.Should().Be("done");
        var recoveredState = LoadKernelState(host);
        recoveredState.RetryAttemptsByStepId.Should().NotContainKey("charge");
        recoveredState.Usage.TotalTokens.Should().Be(25);
    }

    [Fact]
    public async Task NormalizedTerminalPublishCrash_ShouldRecoverPersistedOutboxUntilTerminalCommit()
    {
        const string runId = "run-terminal-recovery";
        var host = CreateNormalizedStateHost(runId);
        var context = new RecordingEventHandlerContext();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = runId,
                Input = "hello",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            context,
            CancellationToken.None);
        var request = StepRequests(context).Should().ContainSingle().Subject;
        context.Published.Clear();
        context.FailNextPublishType = typeof(WorkflowCompletedEvent);

        await FluentActions.Awaiting(() => kernel.HandleAsync(
                Wrap(CreateProducedCompletion(
                    runId,
                    request.StepId,
                    request.ExecutionId,
                    "done")),
                context,
                CancellationToken.None))
            .Should().ThrowAsync<EventStoreOptimisticConcurrencyException>();

        var crashed = LoadKernelState(host);
        crashed.Active.Should().BeFalse();
        crashed.PendingWorkflowCompletion.Should().NotBeNull();
        crashed.PendingWorkflowCompletion!.Output.Should().Be("done");

        var recoveredContext = new RecordingEventHandlerContext();
        var recovery = Wrap(new WorkflowExecutionRecoveryRequestedEvent { RunId = runId });
        await new WorkflowExecutionKernel(SingleStepWorkflow(), host).HandleAsync(
            recovery,
            recoveredContext,
            CancellationToken.None);

        WorkflowCompletions(recoveredContext).Should().ContainSingle()
            .Which.Output.Should().Be("done");
        var accepted = LoadKernelState(host);
        accepted.PendingWorkflowCompletion.Should().NotBeNull(
            "publication admission is not the terminal fact commit");
        WorkflowExecutionKernel.NormalizeTerminalState(accepted).Should().BeFalse();
        host.States[WorkflowExecutionKernel.ModuleStateKey] = Any.Pack(accepted);

        recoveredContext.Published.Clear();
        await new WorkflowExecutionKernel(SingleStepWorkflow(), host).HandleAsync(
            recovery.Clone(),
            recoveredContext,
            CancellationToken.None);
        recoveredContext.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task NormalizedCompensationOutcomeCrash_BeforeContinuationStage_ShouldRecoverWithoutRedispatch()
    {
        const string runId = "run-compensation-outcome-recovery";
        const string compensationExecutionId = "compensation-execution-1";
        var host = CreateNormalizedStateHost(runId);
        host.CompensationCompletionResult = new WorkflowCompensationTransitionResult(
            WorkflowCompensationTransitionStatus.CompletedAll,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
        host.FailNextCompensationCompletionAfterCommit = true;
        var context = new RecordingEventHandlerContext();
        var workflow = SingleStepWorkflow();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = runId,
                Input = "hello",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            context,
            CancellationToken.None);
        var request = StepRequests(context).Should().ContainSingle().Subject;
        var state = LoadKernelState(host);
        state.CompensationExecutionIdsByStepId[request.StepId] = compensationExecutionId;
        await host.UpsertExecutionStateAsync(
            WorkflowExecutionKernel.ModuleStateKey,
            Any.Pack(state),
            CancellationToken.None);
        context.Published.Clear();
        var completion = CreateProducedCompletion(
            runId,
            request.StepId,
            request.ExecutionId,
            "refunded");

        await FluentActions.Awaiting(() => kernel.HandleAsync(
                Wrap(completion),
                context,
                CancellationToken.None))
            .Should().ThrowAsync<EventStoreOptimisticConcurrencyException>();

        var crashed = LoadKernelState(host);
        crashed.PendingCompensationOutcome.Should().NotBeNull();
        crashed.PendingCompensationOutcome!.ContinuationCase.Should().Be(
            WorkflowPendingCompensationOutcomeState.ContinuationOneofCase.None);
        crashed.CompensationExecutionIdsByStepId[request.StepId]
            .Should().Be(compensationExecutionId);
        host.CompensationCompletions.Should().ContainSingle();

        var recoveredContext = new RecordingEventHandlerContext();
        await new WorkflowExecutionKernel(workflow, host).HandleAsync(
            Wrap(new WorkflowExecutionRecoveryRequestedEvent { RunId = runId }),
            recoveredContext,
            CancellationToken.None);

        host.CompensationCompletions.Should().ContainSingle(
            "the actor-owned outcome is idempotent across recovery");
        WorkflowCompletions(recoveredContext).Should().ContainSingle()
            .Which.Success.Should().BeFalse();
        var recovered = LoadKernelState(host);
        recovered.PendingCompensationOutcome.Should().BeNull();
        recovered.CompensationExecutionIdsByStepId.Should().NotContainKey(request.StepId);
        StepRequests(recoveredContext).Should().BeEmpty(
            "an accepted compensation outcome must never re-execute its physical step");
    }

    [Fact]
    public async Task NormalizedCompensationContinuationPublishCrash_ShouldReplayStagedRequestWithoutRecommittingOutcome()
    {
        const string runId = "run-compensation-continuation-recovery";
        const string compensationExecutionId = "compensation-execution-1";
        var host = CreateNormalizedStateHost(runId);
        host.CompensationCompletionResult = new WorkflowCompensationTransitionResult(
            WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext,
            "next-compensation",
            "failed-step",
            "next-idempotency-key",
            "captured-output",
            "next-compensation-execution");
        var context = new RecordingEventHandlerContext();
        var workflow = SingleStepWorkflow();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = runId,
                Input = "hello",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            context,
            CancellationToken.None);
        var request = StepRequests(context).Should().ContainSingle().Subject;
        var state = LoadKernelState(host);
        state.CompensationExecutionIdsByStepId[request.StepId] = compensationExecutionId;
        await host.UpsertExecutionStateAsync(
            WorkflowExecutionKernel.ModuleStateKey,
            Any.Pack(state),
            CancellationToken.None);
        context.Published.Clear();
        context.FailNextPublishType = typeof(CompensationRequestEvent);

        await FluentActions.Awaiting(() => kernel.HandleAsync(
                Wrap(CreateProducedCompletion(
                    runId,
                    request.StepId,
                    request.ExecutionId,
                    "refunded")),
                context,
                CancellationToken.None))
            .Should().ThrowAsync<EventStoreOptimisticConcurrencyException>();

        var crashed = LoadKernelState(host);
        crashed.PendingCompensationOutcome.Should().NotBeNull();
        crashed.PendingCompensationOutcome!.ContinuationCase.Should().Be(
            WorkflowPendingCompensationOutcomeState.ContinuationOneofCase.NextCompensationRequest);
        host.CompensationCompletions.Should().ContainSingle();

        var recoveredContext = new RecordingEventHandlerContext();
        await new WorkflowExecutionKernel(workflow, host).HandleAsync(
            Wrap(new WorkflowExecutionRecoveryRequestedEvent { RunId = runId }),
            recoveredContext,
            CancellationToken.None);

        host.CompensationCompletions.Should().ContainSingle(
            "the staged continuation no longer calls the actor outcome transition");
        recoveredContext.Published.Select(static publication => publication.Event)
            .Where(static payload => payload.Is(CompensationRequestEvent.Descriptor))
            .Select(static payload => payload.Unpack<CompensationRequestEvent>())
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new CompensationRequestEvent
            {
                RunId = runId,
                FailedStepId = "failed-step",
                CompensationStepId = "next-compensation",
                IdempotencyKey = "next-idempotency-key",
                CapturedOutput = "captured-output",
                ExecutionId = "next-compensation-execution",
            });
        LoadKernelState(host).PendingCompensationOutcome.Should().BeNull();
        StepRequests(recoveredContext).Should().BeEmpty();
    }

    [Theory]
    [InlineData("non-self")]
    [InlineData("mismatched-execution")]
    [InlineData("missing-fence")]
    public async Task NormalizedCompletionFence_ShouldRejectWithoutMutatingState(string scenario)
    {
        const string runId = "run-normalized-completion-fence";
        var host = CreateNormalizedStateHost(runId);
        var context = new RecordingEventHandlerContext();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = runId,
                Input = "hello",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            context,
            CancellationToken.None);
        var request = StepRequests(context).Should().ContainSingle().Subject;
        var completion = CreateProducedCompletion(
            runId,
            request.StepId,
            request.ExecutionId,
            "forged-output");
        var envelope = Wrap(completion);
        if (string.Equals(scenario, "non-self", StringComparison.Ordinal))
        {
            envelope.Route.PublisherActorId = "external-actor";
        }
        else if (string.Equals(scenario, "mismatched-execution", StringComparison.Ordinal))
        {
            completion.ExecutionId = "wrong-execution";
            envelope.Payload = Any.Pack(completion);
        }
        else
        {
            var unfenced = LoadKernelState(host);
            unfenced.ExecutionIdsByStepId.Remove(request.StepId);
            await host.UpsertExecutionStateAsync(
                WorkflowExecutionKernel.ModuleStateKey,
                Any.Pack(unfenced),
                CancellationToken.None);
        }
        context.Published.Clear();
        var persistedBefore = host.States[WorkflowExecutionKernel.ModuleStateKey].ToByteArray();

        await kernel.HandleAsync(envelope, context, CancellationToken.None);

        host.States[WorkflowExecutionKernel.ModuleStateKey].ToByteArray()
            .Should().Equal(persistedBefore);
        WorkflowCompletions(context).Should().BeEmpty();
        if (string.Equals(scenario, "non-self", StringComparison.Ordinal))
        {
            context.Published.Should().BeEmpty();
        }
        else
        {
            context.Published.Select(static publication => publication.Event)
                .Count(static payload => payload.Is(StaleStepCompletionRejectedEvent.Descriptor))
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task StepRequest_ShouldContainExecutionId()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var request = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .FirstOrDefault(r => r.StepId == "step-1");

        request.Should().NotBeNull();
        request!.ExecutionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Dispatch_ShouldNotLogRawStepInputContent()
    {
        // Tool-call inputs routinely carry secrets (e.g. {"token":"<NyxID JWT>"}).
        // The dispatch log previously previewed up to 200 chars of raw input, which
        // leaked partial credentials into stdout -> Elasticsearch. Regression guard:
        // the dispatch line is still emitted, but the raw content never is.
        const string secret = "eyJhbGciOiJSENTINEL.secret-credential-payload-must-never-be-logged";
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = secret }),
            ctx, CancellationToken.None);

        ctx.RecordingLogger.Messages.Should()
            .Contain(m => m.Contains("workflow_loop: dispatch") && m.Contains("input=("),
                "the dispatch log line must still be emitted with a length marker");
        ctx.RecordingLogger.Messages.Should()
            .NotContain(m => m.Contains(secret),
                "raw step input content may carry secrets and must never be logged");
    }

    [Fact]
    public async Task StepRequest_ShouldResolveAndPersistDefaultIdempotencyKeyBeforePublish()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx,
            CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.IdempotencyKey.Should().Be("run-1:step-1:1");

        var state = LoadKernelState(host);
        state.IdempotencyByStepId.Should().ContainKey("step-1");
        state.IdempotencyByStepId["step-1"].Should().BeEquivalentTo(new
        {
            LogicalRunId = "run-1",
            StepId = "step-1",
            LogicalAttempt = 1,
            IdempotencyKey = "run-1:step-1:1",
        });
    }

    [Fact]
    public async Task StepRequest_ShouldUseAuthorExpressionOverDefaultAndEvaluateVariables()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "author-key-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    TargetRole = "worker",
                    IdempotencyKey = "${concat(order_id, ':', step_id, ':', logical_attempt, ':', input)}",
                    Parameters = { ["tool"] = "charge" },
                },
            ],
        };
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-1", Input = "payload" };
        start.Parameters["order_id"] = "order-7";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        StepRequests(ctx).Single().IdempotencyKey.Should().Be("order-7:charge:1:payload");
        LoadKernelState(host).IdempotencyByStepId["charge"].IdempotencyKey.Should().Be("order-7:charge:1:payload");
    }

    [Fact]
    public async Task PendingDispatchReplay_ShouldRestorePersistedIdempotencyKey()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "replay-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "side_effect",
                    Type = "tool_call",
                    TargetRole = "worker",
                    IdempotencyKey = "${nonce}",
                    Parameters = { ["tool"] = "external" },
                },
            ],
        };
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-1", Input = "payload" };
        start.Parameters["nonce"] = "first-key";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var state = LoadKernelState(host);
        var originalExecutionId = state.ExecutionIdsByStepId["side_effect"];
        state.CurrentStepDispatchPending = true;
        state.Variables["nonce"] = "changed-key";
        await host.UpsertExecutionStateAsync("workflow_execution_kernel", Any.Pack(state), CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "payload" }), ctx, CancellationToken.None);

        var replay = StepRequests(ctx).Single();
        replay.IdempotencyKey.Should().Be("first-key");
        replay.ExecutionId.Should().Be(originalExecutionId);
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeedIdempotency_ShouldReuseSourceFailedStepKey()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "fork-run",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "source-run",
                StartAtStepId = "step-b",
                StartStepIdempotency = new WorkflowStepIdempotencyState
                {
                    LogicalRunId = "source-run",
                    StepId = "step-b",
                    LogicalAttempt = 2,
                    IdempotencyKey = "source-run:step-b:2",
                },
            },
        };
        start.ForkSeed.Variables["input"] = "seed-input";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.IdempotencyKey.Should().Be("source-run:step-b:2");
        var persisted = LoadKernelState(host).IdempotencyByStepId["step-b"];
        persisted.LogicalRunId.Should().Be("source-run");
        persisted.LogicalAttempt.Should().Be(2);
    }

    [Fact]
    public async Task CompensationRequest_ShouldPersistCarriedIdempotencyKeyForCrashReplay()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "compensation-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "cancel",
                    Type = "tool_call",
                    Parameters = { ["tool"] = "cancel_order" },
                },
            ],
        };
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "start" }), ctx, CancellationToken.None);
        var state = LoadKernelState(host);
        state.CurrentStepDispatchPending = false;
        state.ExecutionIdsByStepId.Clear();
        state.IdempotencyByStepId.Clear();
        await host.UpsertExecutionStateAsync("workflow_execution_kernel", Any.Pack(state), CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(
            Wrap(new CompensationRequestEvent
            {
                RunId = "run-1",
                FailedStepId = "charge",
                CompensationStepId = "cancel",
                IdempotencyKey = "compensate:charge:1",
                CapturedOutput = "captured",
            }),
            ctx,
            CancellationToken.None);

        var first = StepRequests(ctx).Single();
        first.IdempotencyKey.Should().Be("compensate:charge:1");
        first.Input.Should().Be("captured");
        var persistedState = LoadKernelState(host);
        persistedState.IdempotencyByStepId["cancel"].IdempotencyKey.Should().Be("compensate:charge:1");
        persistedState.CurrentStepDispatchPending = true;
        persistedState.Variables["input"] = "changed";
        await host.UpsertExecutionStateAsync("workflow_execution_kernel", Any.Pack(persistedState), CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(
            Wrap(new CompensationRequestEvent
            {
                RunId = "run-1",
                FailedStepId = "charge",
                CompensationStepId = "cancel",
                IdempotencyKey = "different",
                CapturedOutput = "other",
            }),
            ctx,
            CancellationToken.None);

        StepRequests(ctx).Single().IdempotencyKey.Should().Be("compensate:charge:1");
    }

    [Fact]
    public async Task DuplicateWorkflowCallStart_WithSameRunAndInvocation_ShouldNotPublishAlreadyActiveFailure()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-1",
            Input = "hello",
        };
        start.Parameters["workflow_call.invocation_id"] = "invoke-1";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(Wrap(start.Clone()), ctx, CancellationToken.None);

        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should()
            .BeEmpty();
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeed_ShouldPersistHydratedSeedVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-1",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "source-run",
                StartAtStepId = "step-b",
            },
        };
        start.ForkSeed.Variables["input"] = "seed-input";
        start.ForkSeed.Variables["step_a_output"] = "alpha";
        start.ForkSeed.Variables["topic"] = "seed-topic";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .Single();
        request.StepId.Should().Be("step-b");
        request.Input.Should().Be("seed-input");
        request.Parameters["summary"].Should().Be("alpha:seed-topic:seed-input");
        StepRequests(ctx).Should().NotContain(x => x.StepId == "step-a");

        var state = host.States["workflow_execution_kernel"].Unpack<WorkflowExecutionKernelState>();
        state.Variables["step_a_output"].Should().Be("alpha");
        state.Variables["input"].Should().Be("seed-input");
        state.Variables["topic"].Should().Be("seed-topic");
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeedMissingStep_ShouldPublishFailureWithoutDispatch()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = "run-1",
                Input = "hello",
                ForkSeed = new WorkflowRunForkSeed
                {
                    SourceRunId = "source-run",
                    StartAtStepId = "missing-step",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Should()
            .BeEmpty();
        var completed = ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Select(e => e.Unpack<WorkflowCompletedEvent>())
            .First(e => !e.Success && e.Error.Contains("missing-step", StringComparison.Ordinal));
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("missing-step");
    }

    [Fact]
    public async Task StepCompleted_MatchingId_ShouldAccept()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        // Start workflow → dispatches step-1
        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var executionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        // Complete step-1 with matching execution_id → should be accepted
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "done",
                ExecutionId = executionId,
            }),
            ctx, CancellationToken.None);

        // Should NOT have published a StaleStepCompletionRejectedEvent
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Should().BeEmpty();

        // Should have published a WorkflowCompletedEvent (single-step workflow done)
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task StepCompleted_MismatchId_ShouldReject()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        ctx.Published.Clear();

        // Complete step-1 with WRONG execution_id → should be rejected
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "stale",
                ExecutionId = "wrong-execution-id",
            }),
            ctx, CancellationToken.None);

        var rejection = ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Select(e => e.Unpack<StaleStepCompletionRejectedEvent>())
            .FirstOrDefault();

        rejection.Should().NotBeNull();
        rejection!.StepId.Should().Be("step-1");
        rejection.ReceivedExecutionId.Should().Be("wrong-execution-id");

        // Should NOT have completed the workflow
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task StepCompleted_EmptyId_ShouldAcceptForBackwardsCompat()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        ctx.Published.Clear();

        // Complete step-1 with empty execution_id (backwards-compatible old worker)
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "done",
                ExecutionId = "",
            }),
            ctx, CancellationToken.None);

        // Should NOT reject — empty execution_id is backwards compatible
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Should().BeEmpty();

        // Should complete
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task StaleCompletion_ShouldPublishRejectionEvent()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var executionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        // Send stale completion
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "stale",
                ExecutionId = "old-execution-id",
            }),
            ctx, CancellationToken.None);

        var rejection = ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Select(e => e.Unpack<StaleStepCompletionRejectedEvent>())
            .Single();

        rejection.StepId.Should().Be("step-1");
        rejection.ExpectedExecutionId.Should().Be(executionId);
        rejection.ReceivedExecutionId.Should().Be("old-execution-id");
    }

    [Fact]
    public async Task StepRetry_ShouldGenerateNewExecutionId()
    {
        // Workflow with retry policy
        var workflow = new WorkflowDefinition
        {
            Name = "test-retry",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "llm_call",
                    TargetRole = "worker",
                    Retry = new StepRetryPolicy { MaxAttempts = 3, Backoff = "fixed", DelayMs = 0 },
                },
            ],
        };

        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-1", Input = "hello" };
        start.InputFileRefs.Add(BuildWorkflowFileRef("file-retry"));

        // Start workflow → first dispatch
        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var firstRequest = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1");
        var firstExecutionId = firstRequest.ExecutionId;
        firstRequest.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-retry");

        ctx.Published.Clear();

        // Fail step-1 → triggers retry (delayMs=0 → immediate re-dispatch)
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = false,
                Error = "transient error",
                ExecutionId = firstExecutionId,
            }),
            ctx, CancellationToken.None);

        var secondRequest = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .FirstOrDefault(r => r.StepId == "step-1");

        secondRequest.Should().NotBeNull("retry with delayMs=0 should immediately re-dispatch");
        secondRequest!.ExecutionId.Should().NotBeNullOrEmpty();
        secondRequest.ExecutionId.Should().NotBe(firstExecutionId, "retry must generate a new execution_id");
        secondRequest.IdempotencyKey.Should().Be("run-1:step-1:2");
        secondRequest.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-retry");
    }

    [Fact]
    public async Task StepCompleted_WithUsage_ShouldMirrorRunAndStepUsageVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowDefinition
        {
            Name = "usage-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition { Id = "step-1", Type = "llm_call", TargetRole = "worker" },
                new StepDefinition { Id = "step-2", Type = "transform" },
            ],
        };
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-usage", Input = "hello" }),
            ctx,
            CancellationToken.None);

        var executionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-usage",
                Success = true,
                Output = "done",
                ExecutionId = executionId,
                Usage = new WorkflowUsageMetrics
                {
                    PromptTokens = 10,
                    CompletionTokens = 15,
                    TotalTokens = 25,
                    Model = "gpt-5.4",
                    Cost = 0.42,
                    LatencyMs = 123,
                },
            }),
            ctx,
            CancellationToken.None);

        var state = host.GetExecutionState("workflow_execution_kernel")!.Unpack<WorkflowExecutionKernelState>();
        state.Variables["workflow.usage.prompt_tokens"].Should().Be("10");
        state.Variables["workflow.usage.completion_tokens"].Should().Be("15");
        state.Variables["workflow.usage.total_tokens"].Should().Be("25");
        state.Variables["workflow.usage.model"].Should().Be("gpt-5.4");
        state.Variables["workflow.usage.cost"].Should().Be("0.41999999999999998");
        state.Variables["workflow.usage.latency_ms"].Should().Be("123");
        state.Variables["steps.step-1.usage.total_tokens"].Should().Be("25");
        state.Variables["steps.step-1.usage.model"].Should().Be("gpt-5.4");
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeed_ShouldDispatchSeedStartStepAndHydrateVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-resume",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "run-source",
                StartAtStepId = "step-b",
            },
        };
        start.ForkSeed.Variables["input"] = "seed-input";
        start.ForkSeed.Variables["step_a_output"] = "alpha";
        start.ForkSeed.Variables["topic"] = "seed-topic";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var requests = StepRequests(ctx);
        requests.Should().ContainSingle();
        requests[0].StepId.Should().Be("step-b");
        requests[0].Input.Should().Be("seed-input");
        requests[0].Parameters["summary"].Should().Be("alpha:seed-topic:seed-input");
        requests.Should().NotContain(x => x.StepId == "step-a");

        var state = LoadKernelState(host);
        state.CurrentStepId.Should().Be("step-b");
        state.CurrentStepInput.Should().Be("seed-input");
        state.Variables["step_a_output"].Should().Be("alpha");
        state.Variables["topic"].Should().Be("seed-topic");
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeedMissingStartStep_ShouldPublishFailureWithoutDispatch()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-resume",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "run-source",
                StartAtStepId = "missing-step",
            },
        };
        start.ForkSeed.Variables["step_a_output"] = "alpha";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        StepRequests(ctx).Should().BeEmpty();
        var completions = WorkflowCompletions(ctx);
        completions.Should().ContainSingle();
        completions.Should().OnlyContain(x => !x.Success);
        completions.Should().OnlyContain(
            x => x.Error == "fork seed start step 'missing-step' was not found");
        var state = LoadKernelState(host);
        state.Active.Should().BeFalse();
        state.RunId.Should().BeEmpty();
        state.PendingWorkflowCompletion.Should().BeEquivalentTo(completions.Single());
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeed_ShouldLetStartParametersOverrideSeedVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-resume",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "run-source",
                StartAtStepId = "step-b",
            },
        };
        start.ForkSeed.Variables["input"] = "seed-input";
        start.ForkSeed.Variables["step_a_output"] = "alpha";
        start.ForkSeed.Variables["topic"] = "seed-topic";
        start.Parameters["topic"] = "parameter-topic";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepId.Should().Be("step-b");
        request.Input.Should().Be("seed-input");
        request.Parameters["topic_value"].Should().Be("parameter-topic");

        var state = LoadKernelState(host);
        state.Variables["topic"].Should().Be("parameter-topic");
        state.Variables["step_a_output"].Should().Be("alpha");
    }

    [Fact]
    public async Task StartWorkflow_WithoutForkSeed_ShouldDispatchFirstStepWithFreshVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-plain", Input = "fresh-input" }),
            ctx,
            CancellationToken.None);

        var requests = StepRequests(ctx);
        requests.Should().ContainSingle();
        requests[0].StepId.Should().Be("step-a");
        requests[0].Input.Should().Be("fresh-input");

        var state = LoadKernelState(host);
        state.Variables["input"].Should().Be("fresh-input");
        state.Variables.Should().NotContainKey("step_a_output");
        state.Variables.Keys.Should().BeEquivalentTo(DefaultStartVariableKeys);
    }

    [Fact]
    public async Task StartWorkflow_WithInputFileRefs_ShouldDispatchFirstStepAndPersistCurrentStepRefs()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-files",
            Input = "fresh-input",
        };
        start.InputFileRefs.Add(BuildWorkflowFileRef("file-first"));

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepId.Should().Be("step-a");
        request.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-first");

        var state = LoadKernelState(host);
        state.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-first");
        state.CurrentStepInputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-first");
    }

    [Fact]
    public async Task StepCompleted_ShouldCarryStartInputFileRefsToNextStep()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-files",
            Input = "fresh-input",
        };
        start.InputFileRefs.Add(BuildWorkflowFileRef("file-first"));

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);
        var first = StepRequests(ctx).Single();
        ctx.Published.Clear();

        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-a",
                RunId = "run-files",
                Success = true,
                Output = "alpha",
                ExecutionId = first.ExecutionId,
            }),
            ctx,
            CancellationToken.None);

        var second = StepRequests(ctx).Single();
        second.StepId.Should().Be("step-b");
        second.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-first");
        LoadKernelState(host).CurrentStepInputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-first");
    }

    [Fact]
    public async Task StartWorkflow_WithStepPresentation_ShouldDispatchTypedInteractionSpec()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowDefinition
        {
            Name = "interaction-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "approval",
                    Type = "human_approval",
                    TargetRole = "worker",
                    Presentation = new StepPresentation
                    {
                        InteractionSpec = new InteractionSpec
                        {
                            Title = "Approve ${input}",
                            Body = "Release ${release}",
                            Disposition = InteractionDisposition.Ephemeral,
                            Actions =
                            {
                                new InteractionAction
                                {
                                    Kind = InteractionActionKind.Button,
                                    ActionId = "approve",
                                    Label = "Approve ${release}",
                                    Value = "${release}",
                                    Style = InteractionActionStyle.Primary,
                                },
                            },
                        },
                    },
                },
            ],
        };
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-interaction", Input = "deploy" };
        start.Parameters["release"] = "v1";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.Parameters.Should().NotContainKey("interaction_spec");
        request.StepParameters.InteractionSpec.Title.Should().Be("Approve deploy");
        request.StepParameters.InteractionSpec.Body.Should().Be("Release v1");
        request.StepParameters.InteractionSpec.Disposition.Should().Be(InteractionDisposition.Ephemeral);
        request.StepParameters.InteractionSpec.Actions[0].Label.Should().Be("Approve v1");
        request.StepParameters.InteractionSpec.Actions[0].Value.Should().Be("v1");
        request.StepParameters.InteractionSpec.Actions[0].Style.Should().Be(InteractionActionStyle.Primary);
    }

    [Fact]
    public async Task StartWorkflow_WithRoleAndStepToolScopes_ShouldDispatchIntersection()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowDefinition
        {
            Name = "tool-scope-workflow",
            Roles =
            [
                new RoleDefinition
                {
                    Id = "worker",
                    Name = "Worker",
                    AgentToolScope = new WorkflowAgentToolScopeDefinition
                    {
                        AllowedToolNames = ["search", "calendar"],
                        ToolSetRefs = ["nyxid.connected_services", "shared"],
                    },
                },
            ],
            Steps =
            [
                new StepDefinition
                {
                    Id = "scoped",
                    Type = "llm_call",
                    TargetRole = "worker",
                    AgentToolScope = new WorkflowAgentToolScopeDefinition
                    {
                        AllowedToolNames = ["calendar", "forbidden"],
                        ToolSetRefs = ["nyxid.connected_services", "other"],
                    },
                },
            ],
        };
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-tools", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.Should().NotBeNull();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().Equal("calendar");
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
    }

    [Fact]
    public async Task StartWorkflow_WhenStepRestrictsOnlyAllowedTools_ShouldInheritRoleToolSets()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowParser().Parse(
            """
            name: independent-tool-scope
            roles:
              - id: worker
                tool_sets: [nyxid.connected_services]
            steps:
              - id: scoped
                type: llm_call
                target_role: worker
                allowed_tools: [search]
            """);
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-independent-static", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().Equal("search");
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
    }

    [Fact]
    public async Task StartWorkflow_WhenStepRestrictsOnlyToolSets_ShouldInheritRoleAllowedTools()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowParser().Parse(
            """
            name: independent-tool-set-scope
            roles:
              - id: worker
                allowed_tools: [search]
            steps:
              - id: scoped
                type: llm_call
                target_role: worker
                tool_sets: [nyxid.connected_services]
            """);
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-independent-tool-set", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().Equal("search");
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
    }

    [Fact]
    public async Task StartWorkflow_WithExplicitEmptyAllowedTools_ShouldClearOnlyStaticTools()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowParser().Parse(
            """
            name: empty-static-tool-scope
            roles:
              - id: worker
                allowed_tools: [search]
                tool_sets: [nyxid.connected_services]
            steps:
              - id: scoped
                type: llm_call
                target_role: worker
                allowed_tools: []
            """);
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-empty-static", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().BeEmpty();
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
    }

    [Fact]
    public async Task StartWorkflow_WithExplicitEmptyToolSets_ShouldClearOnlyDynamicTools()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowParser().Parse(
            """
            name: empty-dynamic-tool-scope
            roles:
              - id: worker
                allowed_tools: [search]
                tool_sets: [nyxid.connected_services]
            steps:
              - id: scoped
                type: llm_call
                target_role: worker
                tool_sets: []
            """);
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-empty-dynamic", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().Equal("search");
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task StartWorkflow_WithExternalApprovalYaml_ShouldDispatchEvaluatedTypedWaitOptions()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: approval_yaml
            roles: []
            steps:
              - id: wait_approval
                type: wait_signal
                parameters:
                  external_approval.source_id: "NyxID"
                  external_approval.external_id_kind: "Instance_Code"
                  external_approval.external_id: "${instance_code}"
                  external_approval.signal_name: "approval-${input}"
                  external_approval.callback_idempotency_key: "${concat('idem-', instance_code)}"
                  external_approval.request_id: "${request_id}"
            """);
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-approval", Input = "deploy" };
        start.Parameters["instance_code"] = "APP-42";
        start.Parameters["request_id"] = "REQ-42";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var externalApproval = StepRequests(ctx)
            .Single()
            .StepParameters
            .ExternalApproval;
        externalApproval.Should().NotBeNull();
        externalApproval.SourceId.Should().Be("NyxID");
        externalApproval.ExternalIdKind.Should().Be("Instance_Code");
        externalApproval.ExternalId.Should().Be("APP-42");
        externalApproval.SignalName.Should().Be("approval-deploy");
        externalApproval.CallbackIdempotencyKey.Should().Be("idem-APP-42");
        externalApproval.RequestId.Should().Be("REQ-42");
    }

    // ──── Test infrastructure ────

    private static WorkflowDefinition CreateNormalizedExpressionWorkflow() => new()
    {
        Name = "normalized-expression-matrix",
        Roles = [],
        Steps =
        [
            new StepDefinition
            {
                Id = "producer",
                Type = "notify",
                Next = "route",
                Retry = new StepRetryPolicy { MaxAttempts = 2, Backoff = "fixed", DelayMs = 0 },
            },
            new StepDefinition
            {
                Id = "route",
                Type = "switch",
                Parameters = { ["on"] = "${steps.producer.json.route}" },
                Branches = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["match"] = "assign",
                    ["_default"] = "unexpected",
                },
            },
            new StepDefinition
            {
                Id = "assign",
                Type = "assign",
                Next = "consumer",
                Parameters =
                {
                    ["target"] = "selected",
                    ["value"] = "$input",
                },
            },
            new StepDefinition
            {
                Id = "consumer",
                Type = "notify",
                Parameters =
                {
                    ["legacy_step"] = "${producer}",
                    ["step_output"] = "${steps.producer.output}",
                    ["json_field"] = "${steps.producer.json.detail}",
                    ["input_value"] = "${input}",
                    ["assigned_value"] = "${selected}",
                    ["switch_branch"] = "${steps.route.branch_key}",
                    ["assign_output"] = "${steps.assign.output}",
                },
            },
            new StepDefinition { Id = "unexpected", Type = "notify" },
        ],
    };

    private static StepCompletedEvent CreateProducedCompletion(
        string runId,
        string stepId,
        string executionId,
        string output,
        bool success = true,
        string error = "") =>
        new()
        {
            RunId = runId,
            StepId = stepId,
            ExecutionId = executionId,
            Success = success,
            Output = output,
            Error = error,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };

    private static RecordingStateHost CreateNormalizedStateHost(string runId)
    {
        var now = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var host = CreateAdoptedStateHost(
            new FixedSchemaContextAccessor(CreateNormalizedSchemaContext(now)),
            new MutableAdmissionReader(CreateNormalizedAdmission(now)),
            new FixedMembershipReader(new RuntimeLocalMembershipIdentity(
                7,
                "digest-a",
                "revision-a",
                "member-a",
                "inc-a")),
            new FixedTimeProvider(now));
        host.RunId = runId;
        return host;
    }

    private static RecordingStateHost CreateValueLifecycleStateHost(string runId)
    {
        var now = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var host = CreateAdoptedStateHost(
            new FixedSchemaContextAccessor(CreateValueLifecycleSchemaContext(now)),
            new MutableAdmissionReader(CreateNormalizedAdmission(
                now,
                WorkflowNormalizedStateWriteAdmission.ValueLifecycleRequiredReaderContractVersion)),
            new FixedMembershipReader(new RuntimeLocalMembershipIdentity(
                7,
                "digest-a",
                "revision-a",
                "member-a",
                "inc-a")),
            new FixedTimeProvider(now));
        host.RunId = runId;
        return host;
    }

    private static readonly string[] DefaultStartVariableKeys =
    [
        "input",
        "workflow.usage.prompt_tokens",
        "workflow.usage.completion_tokens",
        "workflow.usage.total_tokens",
        "workflow.usage.model",
        "workflow.usage.cost",
        "workflow.usage.latency_ms",
    ];

    private static IReadOnlyList<StepRequestEvent> StepRequests(RecordingEventHandlerContext ctx) =>
        ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .ToList();

    private static IReadOnlyList<StepCompletedEvent> StepCompletions(RecordingEventHandlerContext ctx) =>
        ctx.Published
            .Select(static publication => publication.Event)
            .Where(static payload => payload.Is(StepCompletedEvent.Descriptor))
            .Select(static payload => payload.Unpack<StepCompletedEvent>())
            .ToList();

    private static IReadOnlyList<WorkflowCompletedEvent> WorkflowCompletions(RecordingEventHandlerContext ctx) =>
        ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Select(e => e.Unpack<WorkflowCompletedEvent>())
            .ToList();

    private static WorkflowExecutionKernelState LoadKernelState(RecordingStateHost host) =>
        host.GetExecutionState("workflow_execution_kernel")!.Unpack<WorkflowExecutionKernelState>();

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = "image_key_1",
            FileName = $"{fileId}.png",
            MediaType = "image/png",
            SizeBytes = 3,
            Sha256 = $"sha-{fileId}",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
        };

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IAgent Agent { get; } = new StubAgent("agent-1");

        public IServiceProvider Services { get; } = new NullServiceProvider();

        public RecordingLogger RecordingLogger { get; } = new();

        public ILogger Logger => RecordingLogger;

        public List<(Any Event, TopologyAudience Direction)> Published { get; } = [];

        public List<(string TargetActorId, Any Event)> Sent { get; } = [];

        public List<RecordedCallback> ScheduledTimeouts { get; } = [];

        public List<RecordedTimer> ScheduledTimers { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public System.Type? FailNextPublishType { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (FailNextPublishType == typeof(TEvent))
            {
                FailNextPublishType = null;
                throw new EventStoreOptimisticConcurrencyException("test-publish", 1, 2);
            }
            Published.Add((Any.Pack(evt), direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((targetActorId, Any.Pack(evt)));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduledTimeouts.Add(new RecordedCallback(callbackId, dueTime, Any.Pack(evt), options));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduledTimers.Add(new RecordedTimer(callbackId, dueTime, period, Any.Pack(evt), options));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 2, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        public string RunId { get; set; } = "run-1";

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public IRuntimeActorStateSchemaContextReader? RuntimeStateSchemaContextReader { get; init; }

        public IRuntimeFleetCapabilityAdmissionReader? RuntimeFleetCapabilityAdmissionReader { get; init; }

        public IRuntimeLocalMembershipIdentityReader? RuntimeLocalMembershipIdentityReader { get; init; }

        public TimeProvider? RuntimeFleetAdmissionTimeProvider { get; init; }

        public RuntimeActorStateMigrationAdmissionOptions? RuntimeFleetAdmissionOptions { get; init; }

        public bool FailNextCompensableOutcomeCommit { get; set; }

        public bool FailNextCompensationCompletionAfterCommit { get; set; }

        public List<CompensableStepOutputCapturedEvent> CompensableOutcomes { get; } = [];

        public List<CompensationStepCompletedEvent> CompensationCompletions { get; } = [];

        public WorkflowCompensationTransitionResult CompensationCompletionResult { get; set; } =
            NoCompensableLedger();

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
        {
            ApplyDelta(ExecutionContextState, delta);
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
            return Task.CompletedTask;
        }

        public Dictionary<string, Any> States { get; } = new(StringComparer.Ordinal);

        public Any? GetExecutionState(string scopeKey) =>
            States.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            States.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            States[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            States.Remove(scopeKey);
            return Task.CompletedTask;
        }

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepOutcomeAsync(
            CompensableStepOutputCapturedEvent evt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailNextCompensableOutcomeCommit)
            {
                FailNextCompensableOutcomeCommit = false;
                throw new EventStoreOptimisticConcurrencyException("run-compensable", 3, 4);
            }

            CompensableOutcomes.Add(evt.Clone());
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!CompensationCompletions.Any(existing => existing.Equals(completion)))
                CompensationCompletions.Add(completion.Clone());
            if (FailNextCompensationCompletionAfterCommit)
            {
                FailNextCompensationCompletionAfterCommit = false;
                throw new EventStoreOptimisticConcurrencyException("run-compensation", 5, 6);
            }

            return Task.FromResult(CompensationCompletionResult);
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
    }

    private static RecordingStateHost CreateAdoptedStateHost(
        IRuntimeActorStateSchemaContextReader schemaReader,
        IRuntimeFleetCapabilityAdmissionReader admissionReader,
        IRuntimeLocalMembershipIdentityReader membershipReader,
        TimeProvider timeProvider) =>
        new()
        {
            RuntimeStateSchemaContextReader = schemaReader,
            RuntimeFleetCapabilityAdmissionReader = admissionReader,
            RuntimeLocalMembershipIdentityReader = membershipReader,
            RuntimeFleetAdmissionTimeProvider = timeProvider,
            RuntimeFleetAdmissionOptions = new RuntimeActorStateMigrationAdmissionOptions(),
        };

    private static RuntimeActorStateSchemaContext CreateNormalizedSchemaContext(DateTimeOffset adoptedAt)
    {
        var receipt = new RuntimeActorStateSchemaAdoptionReceipt
        {
            StateSchemaVersion = 1,
            RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RequiredContractId = WorkflowNormalizedStateWriteAdmission.ContractId,
            RequiredContractVersion = WorkflowNormalizedStateWriteAdmission.RequiredReaderContractVersion,
            CapabilityEpoch = 3,
            AuthorityStateVersion = 9,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            AdoptedAt = Timestamp.FromDateTimeOffset(adoptedAt),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = "digest-a",
        };
        return new RuntimeActorStateSchemaContext("workflow.run", 1, [receipt]);
    }

    private static RuntimeActorStateSchemaContext CreateValueLifecycleSchemaContext(
        DateTimeOffset adoptedAt)
    {
        var v1Receipt = CreateNormalizedSchemaContext(adoptedAt).AdoptionReceipts.Single().Clone();
        var v2Receipt = new RuntimeActorStateSchemaAdoptionReceipt
        {
            StateSchemaVersion = 2,
            RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RequiredContractId = WorkflowNormalizedStateWriteAdmission.ContractId,
            RequiredContractVersion =
                WorkflowNormalizedStateWriteAdmission.ValueLifecycleRequiredReaderContractVersion,
            CapabilityEpoch = 3,
            AuthorityStateVersion = 9,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            AdoptedAt = Timestamp.FromDateTimeOffset(adoptedAt),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = "digest-a",
        };
        return new RuntimeActorStateSchemaContext("workflow.run", 2, [v1Receipt, v2Receipt]);
    }

    private static RuntimeFleetCapabilityAdmission CreateNormalizedAdmission(
        DateTimeOffset now,
        int readerContractVersion = WorkflowNormalizedStateWriteAdmission.RequiredReaderContractVersion)
    {
        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            Status = RuntimeFleetCapabilityGateStatus.Open,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 9,
            CapabilityEpoch = 3,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            MinimumReaderContractVersion = readerContractVersion,
            MembershipObservedAt = Timestamp.FromDateTimeOffset(now.AddSeconds(-5)),
            MembershipValidUntil = Timestamp.FromDateTimeOffset(now.AddMinutes(1)),
            ActiveMemberCount = 1,
            ConfirmedMemberCount = 1,
            MembershipDigest = "digest-a",
            ContractId = WorkflowNormalizedStateWriteAdmission.ContractId,
        };
        admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember
        {
            MemberId = "member-a",
            Incarnation = "inc-a",
        });
        return admission;
    }

    private sealed class FixedSchemaContextAccessor(RuntimeActorStateSchemaContext current)
        : IRuntimeActorStateSchemaContextReader
    {
        public RuntimeActorStateSchemaContext? Current { get; } = current;

        public IDisposable Bind(RuntimeActorIdentity identity) =>
            throw new NotSupportedException("The fixed test accessor cannot be rebound.");
    }

    private sealed class MutableAdmissionReader(RuntimeFleetCapabilityAdmission admission)
        : IRuntimeFleetCapabilityAdmissionReader
    {
        public RuntimeFleetCapabilityAdmission Admission { get; } = admission;

        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<RuntimeFleetCapabilityAdmission?>(Admission.Clone());
        }
    }

    private sealed class FixedMembershipReader(RuntimeLocalMembershipIdentity membership)
        : IRuntimeLocalMembershipIdentityReader
    {
        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(membership);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    private static void ApplyDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta delta)
    {
        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearCallerCredential)
            state.CallerCredential = null;
        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                RoutePreference = delta.Llm.RoutePreference,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.CallerCredential != null)
        {
            state.CallerCredential = new WorkflowCallerCredentialState
            {
                BearerToken = delta.CallerCredential.BearerToken,
            };
        }
    }

    internal record RecordedCallback(string CallbackId, TimeSpan DueTime, Any Event, EventEnvelopePublishOptions? Options);
    internal record RecordedTimer(string CallbackId, TimeSpan DueTime, TimeSpan Period, Any Event, EventEnvelopePublishOptions? Options);
}
