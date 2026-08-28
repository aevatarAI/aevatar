using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class SubWorkflowOrchestratorTests
{
    private const string ValidSubFlowYaml = """
                                           name: sub_flow
                                           roles:
                                             - id: role_a
                                               name: RoleA
                                               system_prompt: "helpful role"
                                           steps:
                                             - id: step_1
                                               type: transform
                                           """;

    private const string ValidSubFlowWithSpaceYaml = """
                                                    name: sub flow
                                                    roles:
                                                      - id: role_a
                                                        name: RoleA
                                                        system_prompt: "helpful role"
                                                    steps:
                                                      - id: step_1
                                                        type: transform
                                                    """;

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenParentStepMissing_ShouldPublishFailure()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                ParentRunId = " parent-run ",
                ParentStepId = " ",
                WorkflowName = "sub_flow",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().ContainSingle();
        var failure = harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.RunId.Should().Be("parent-run");
        failure.StepId.Should().BeEmpty();
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("parent_step_id");
        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Persisted.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenWorkflowNameMissing_ShouldPublishFailure()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = " ",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().ContainSingle();
        harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Which.Error
            .Should().Contain("missing workflow parameter");
        harness.Runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenLifecycleUnsupported_ShouldPublishFailure()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = "sub_flow",
                Lifecycle = "invalid",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().ContainSingle();
        harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Which.Error
            .Should().Contain(WorkflowCallLifecycle.AllowedValuesText);
        harness.Runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenInvocationIdEqualsParentRunId_ShouldFailClosed()
    {
        // R1a (06-20-observatory-run-state-feed): a child sub-workflow run id is its invocation id and
        // must never equal the parent run id, otherwise a relayed child terminal could be adopted by the
        // projection-root as its own terminal (R1) and clobber the parent's current-state doc.
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "parent-run",
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = "sub_flow",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        var failure = harness.Published.Should().ContainSingle().Subject
            .Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.RunId.Should().Be("parent-run");
        failure.StepId.Should().Be("step-a");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("must differ from the parent run id");
        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Persisted.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenInvocationIdMatchesParentAfterTrim_ShouldFailClosed()
    {
        // The collision check normalizes both sides, so whitespace-padded collisions also fail closed.
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "  parent-run  ",
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = "sub_flow",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().ContainSingle().Subject
            .Message.Should().BeOfType<StepCompletedEvent>().Which.Error
            .Should().Contain("must differ from the parent run id");
        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Persisted.Should().BeEmpty();
    }

    [Fact]
    public void ApplySubWorkflowInvocationRegistered_ShouldClearUnavailableReasonWhenLineageBecomesAvailable()
    {
        var current = new WorkflowRunState
        {
            Lineage = WorkflowRunGAgent.CreateUnavailableLineage("Run lineage is unavailable for this run."),
        };

        var next = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(
            current,
            new SubWorkflowInvocationRegisteredEvent
            {
                InvocationId = "invoke-child-beta",
                ParentRunId = "run-parent-alpha",
                ParentStepId = "step-call-child",
                WorkflowName = "child_workflow",
                ChildActorId = "actor-child-delta",
                ChildRunId = "run-child-beta",
                RootRunId = "run-root-omega",
                Depth = 2,
            });

        next.Lineage.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        next.Lineage.UnavailableReason.Should().BeEmpty();
        next.Lineage.SubWorkflow.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        next.Lineage.SubWorkflow.ChildRuns.Should().ContainSingle(child =>
            child.RunId == "run-child-beta" &&
            child.ActorId == "actor-child-delta" &&
            child.RelationshipId == "invoke-child-beta" &&
            child.RelationKind == WorkflowRunLineageRelationKind.SubWorkflow);
    }

    [Fact]
    public async Task ExistingRegisteredInvocation_ShouldRecoverDefinitionOwnedAdmissionPlan()
    {
        var childPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            AdmissionDigest = "registered-child-plan",
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        };
        var state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(
            new WorkflowRunState
            {
                RunId = "parent-run",
                CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                {
                    SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
                    AdmissionDigest = "parent-plan",
                    ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                },
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
            new SubWorkflowInvocationRegisteredEvent
            {
                InvocationId = "invoke-recover-registered",
                ParentRunId = "parent-run",
                ParentStepId = "call-child",
                WorkflowName = "sub_flow",
                ChildActorId = "owner-1:workflow:registered-recovery",
                ChildRunId = "invoke-recover-registered",
                Lifecycle = WorkflowCallLifecycle.Transient,
                DefinitionActorId = "workflow-definition:sub_flow",
                DefinitionVersion = 9,
                DefinitionYaml = ValidSubFlowYaml,
                ScopeId = "scope-child",
                WorkflowId = "wf-registered-child",
                RevisionId = "rev-registered-child-9",
                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                CapabilityAdmissionPlan = childPlan,
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                HandoffPhase = (int)SubWorkflowInvocationHandoffPhase.Registered,
                ValueRepresentation = WorkflowExecutionValueRepresentation.Legacy,
            });
        state.InlineWorkflowYamls["parent_only"] = ValidSubFlowYaml;
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-recover-registered",
                ParentRunId = "parent-run",
                ParentStepId = "call-child",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
                ValueRepresentation = WorkflowExecutionValueRepresentation.Legacy,
            },
            state,
            CancellationToken.None);

        var childActor = harness.Runtime.StoredActors["owner-1:workflow:registered-recovery"];
        var binding = childActor.LastHandledEnvelope!.Payload!
            .Unpack<BindWorkflowRunDefinitionEvent>();
        binding.DefinitionActorId.Should().Be("workflow-definition:sub_flow");
        binding.DefinitionVersion.Should().Be(9);
        binding.WorkflowId.Should().Be("wf-registered-child");
        binding.RevisionId.Should().Be("rev-registered-child-9");
        binding.CapabilityAdmissionPlan.Should().BeEquivalentTo(childPlan);
        binding.CapabilityAdmissionPlan.AdmissionDigest.Should().NotBe("parent-plan");
        binding.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        binding.InlineWorkflowYamls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenDefinitionActorMustBeResolved_ShouldRegisterResolutionAndScheduleTimeout()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-1",
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = "sub_flow",
                RootRunId = "root-run",
                RequestedDepth = 2,
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().BeEmpty();
        harness.Runtime.CreateRequests.Should().BeEmpty();
        var registered = harness.Persisted.Should().ContainSingle()
            .Subject.Should().BeOfType<SubWorkflowDefinitionResolutionRegisteredEvent>().Subject;
        registered.InvocationId.Should().Be("invoke-1");
        registered.DefinitionActorId.Should().Be("workflow-definition:sub_flow");
        registered.TimeoutCallbackId.Should().NotBeNullOrWhiteSpace();
        registered.TimeoutMs.Should().Be(30_000);
        registered.RootRunId.Should().Be("root-run");
        registered.RequestedDepth.Should().Be(2);
        harness.ScheduledTimeouts.Should().ContainSingle(x =>
            x.CallbackId == registered.TimeoutCallbackId &&
            x.DueTime == TimeSpan.FromMilliseconds(30_000));
        harness.Sent.Should().ContainSingle(x => x.TargetActorId == "workflow-definition:sub_flow");
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenInlineWorkflowYamlIsEmpty_ShouldPublishValidationFailure()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.InlineWorkflowYamls["sub_flow"] = " ";

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-inline",
                ParentRunId = "parent-run",
                ParentStepId = "step-inline",
                WorkflowName = "sub_flow",
            },
            state,
            CancellationToken.None);

        harness.ScheduledTimeouts.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
        harness.Persisted.Should().ContainSingle(x => x is SubWorkflowDefinitionResolutionClearedEvent);
        var failure = harness.Published.Should().ContainSingle().Subject.Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("inline workflow 'sub_flow' YAML is empty");
    }

    [Fact]
    public async Task HandleDefinitionResolvedAsync_WhenPublisherIsNotRequestedDefinitionActor_ShouldIgnoreReply()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(
            new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
            {
                InvocationId = "invoke-spoofed-definition",
                ParentRunId = "parent-run",
                ParentStepId = "call-child",
                WorkflowName = "sub_flow",
                DefinitionActorId = "workflow-definition:sub_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
            });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId[
            "invoke-spoofed-definition"] = 0;

        await harness.Orchestrator.HandleDefinitionResolvedAsync(
            new SubWorkflowDefinitionResolvedEvent
            {
                InvocationId = "invoke-spoofed-definition",
                Definition = new WorkflowDefinitionSnapshot
                {
                    DefinitionActorId = "workflow-definition:sub_flow",
                    WorkflowName = "sub_flow",
                    WorkflowYaml = ValidSubFlowYaml,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            },
            "workflow-run:attacker",
            state,
            CancellationToken.None);

        harness.Persisted.Should().BeEmpty();
        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
        harness.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDefinitionResolvedAsync_WhenSingletonBindingExistsAndActorIsAlive_ShouldReuseActor()
    {
        const string definitionActorId = "workflow-definition:sub_flow";
        const string childActorId = "owner-1:workflow:workflow-definition-sub_flow";
        var harness = CreateHarness();
        harness.Runtime.StoredActors[definitionActorId] = new RecordingActor(definitionActorId);
        harness.Runtime.StoredActors[childActorId] = new RecordingActor(childActorId);

        var parentPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            AdmissionDigest = "parent-plan",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
        };
        var childPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            AdmissionDigest = "registered-child-plan",
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        };
        var state = new WorkflowRunState
        {
            CapabilityAdmissionPlan = parentPlan,
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Durable,
            ExecutionContext = new WorkflowRunExecutionContextState
            {
                CallerCredential = new WorkflowCallerCredentialState
                {
                    DurableCallerCredential = new Aevatar.Foundation.Abstractions.Credentials.DurableCallerCredentialRef
                    {
                        Ref = "scheduled-secret",
                        Purpose = Aevatar.Foundation.Abstractions.Credentials.CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                        OwnerScopeKey = "scope-parent",
                        SubjectId = "subject-parent",
                        SourceKind = Aevatar.Foundation.Abstractions.Credentials.DurableCallerCredentialSourceKind.ScheduledDispatch,
                    },
                    DurableCredentialCleanupResponsibility =
                        WorkflowCallerCredentialCleanupResponsibility.Owner,
                },
                UnattendedEffectAuthorization = new WorkflowUnattendedEffectAuthorization
                {
                    AuthorizationDigest = "parent-only-authorization",
                },
            },
        };
        state.InlineWorkflowYamls["parent_only"] = ValidSubFlowYaml;
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "sub_flow",
            ChildActorId = childActorId,
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = definitionActorId,
            DefinitionVersion = 7,
            BindingGeneration = 1,
        });
        var fileRef = BuildWorkflowFileRef("file-sub-1");

        var invokeRequested = new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-1",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            Input = "payload-a",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            RootRunId = "root-run",
            RequestedDepth = 2,
        };
        invokeRequested.InputFileRefs.Add(fileRef.Clone());

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            invokeRequested,
            state,
            CancellationToken.None);

        var registeredResolution = harness.Persisted
            .OfType<SubWorkflowDefinitionResolutionRegisteredEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        registeredResolution.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-sub-1");
        harness.Sent.Should().ContainSingle(x => x.TargetActorId == definitionActorId);
        var resolutionState = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered(
            state,
            registeredResolution);
        resolutionState.PendingSubWorkflowDefinitionResolutions.Should()
            .ContainSingle()
            .Which.InputFileRefs.Should()
            .ContainSingle()
            .Which.FileId.Should()
            .Be("file-sub-1");

        harness.Persisted.Clear();
        harness.Sent.Clear();

        await harness.Orchestrator.HandleDefinitionResolvedAsync(
            new SubWorkflowDefinitionResolvedEvent
            {
                InvocationId = "invoke-1",
                Definition = new WorkflowDefinitionSnapshot
                {
                    DefinitionActorId = definitionActorId,
                    WorkflowName = "sub_flow",
                    WorkflowYaml = ValidSubFlowYaml,
                    DefinitionVersion = 7,
                    WorkflowId = "wf-registered-child",
                    RevisionId = "rev-registered-child",
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    CapabilityAdmissionPlan = childPlan,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            },
            definitionActorId,
            resolutionState,
            CancellationToken.None);

        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Runtime.Linked.Should().ContainSingle(x =>
            x.ParentId == "owner-1" &&
            x.ChildId == childActorId);
        harness.Persisted.OfType<SubWorkflowDefinitionResolvedEvent>().Should().ContainSingle(x => x.InvocationId == "invoke-1");
        var registeredInvocation = harness.Persisted.OfType<SubWorkflowInvocationRegisteredEvent>().Should().ContainSingle(x => x.InvocationId == "invoke-1").Subject;
        registeredInvocation.RootRunId.Should().Be("root-run");
        registeredInvocation.Depth.Should().Be(2);
        registeredInvocation.BindingGeneration.Should().Be(2);
        registeredInvocation.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-sub-1");
        registeredInvocation.WorkflowId.Should().Be("wf-registered-child");
        registeredInvocation.RevisionId.Should().Be("rev-registered-child");
        registeredInvocation.CapabilityAdmissionPlan.Should().BeEquivalentTo(childPlan);
        registeredInvocation.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        harness.Persisted.OfType<SubWorkflowBindingUpsertedEvent>()
            .Should().ContainSingle(x => x.BindingGeneration == 2);
        harness.CancelledLeases.Should().ContainSingle(x => x.CallbackId == resolutionState.PendingSubWorkflowDefinitionResolutions[0].TimeoutCallbackId);
        harness.Sent.Should().ContainSingle(x => x.TargetActorId == childActorId);
        var start = harness.Sent.Single().Message.Should().BeOfType<StartWorkflowEvent>().Subject;
        start.RunId.Should().Be("invoke-1");
        start.BindingGeneration.Should().Be(2);
        start.Parameters["workflow_call.parent_run_id"].Should().Be("parent-run");
        start.Parameters["workflow_call.parent_step_id"].Should().Be("step-a");
        start.WorkflowRuntime.ParentActorId.Should().Be("owner-1");
        start.WorkflowRuntime.ParentRunId.Should().Be("parent-run");
        start.WorkflowRuntime.ParentStepId.Should().Be("step-a");
        start.WorkflowRuntime.RootRunId.Should().Be("root-run");
        start.WorkflowRuntime.Depth.Should().Be(2);
        start.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-sub-1");
        start.Parameters.Keys.Should().NotContain(key => key.StartsWith("workflow_runtime.", StringComparison.Ordinal));
        start.ExecutionContextDelta.CallerCredential.DurableCredentialCleanupResponsibility
            .Should().Be(WorkflowCallerCredentialCleanupResponsibility.Borrowed);
        start.ExecutionContextDelta.UnattendedEffectAuthorization.Should().BeNull();
        var childBinding = harness.Runtime.StoredActors[childActorId].LastHandledEnvelope!.Payload!
            .Unpack<BindWorkflowRunDefinitionEvent>();
        childBinding.WorkflowId.Should().Be("wf-registered-child");
        childBinding.RevisionId.Should().Be("rev-registered-child");
        childBinding.CapabilityAdmissionPlan.Should().BeEquivalentTo(childPlan);
        childBinding.CapabilityAdmissionPlan.Should().NotBeEquivalentTo(parentPlan);
        childBinding.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        childBinding.InlineWorkflowYamls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenInlineChildIsBound_ShouldCarryParentAdmissionAndBindingIdentity()
    {
        var harness = CreateHarness();
        var parentPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            DefinitionDigest = "root-definition-digest",
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            AdmissionDigest = "root-admission-digest",
        };
        parentPlan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "sub_flow/poll_conversation",
        });
        parentPlan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "sibling_flow/read_status",
        });
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
            DefinitionActorId = "workflow-definition:parent",
            DefinitionVersion = 42,
            RunOrigin = WorkflowRunOrigins.Webhook,
            ScheduleId = "schedule-parent",
            WorkflowId = "wf-parent",
            RevisionId = "rev-parent-1",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            CapabilityAdmissionPlan = parentPlan,
            ExecutionContext = new WorkflowRunExecutionContextState
            {
                CallerCredential = new WorkflowCallerCredentialState
                {
                    RuntimeSecretReference = new Aevatar.Foundation.Abstractions.Credentials.RuntimeSecretReference
                    {
                        Ref = "runtime-secret-ref",
                        Purpose = "workflow-caller-bearer-token",
                        OwnerRunId = "parent-run",
                        OwnerStepId = "workflow.caller",
                    },
                    Kind = NyxIdCallerCredentialKind.AgentKey,
                },
                UnattendedEffectAuthorization = new WorkflowUnattendedEffectAuthorization
                {
                    AuthorizationDigest = "parent-authorization",
                },
            },
        };
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-admitted-child",
                ParentRunId = "parent-run",
                ParentStepId = "call-child",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
            },
            state,
            CancellationToken.None);

        var childActor = harness.Runtime.StoredActors.Values
            .Should().ContainSingle(actor => actor.Id.Contains(":workflow:", StringComparison.Ordinal))
            .Subject;
        var binding = childActor.LastHandledEnvelope!.Payload!
            .Unpack<BindWorkflowRunDefinitionEvent>();
        binding.WorkflowId.Should().Be("wf-parent");
        binding.RevisionId.Should().Be("rev-parent-1");
        binding.DefinitionActorId.Should().Be("workflow-definition:parent");
        binding.DefinitionVersion.Should().Be(42);
        binding.RunOrigin.Should().Be(WorkflowRunOrigins.Webhook);
        binding.ScheduleId.Should().Be("schedule-parent");
        binding.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        binding.CapabilityAdmissionPlan.Should().NotBeSameAs(parentPlan);
        binding.CapabilityAdmissionPlan.Should().BeEquivalentTo(parentPlan);
        binding.CapabilityAdmissionPlan.InvocationAdmissions.Select(x => x.CallSiteId)
            .Should().Equal("sub_flow/poll_conversation", "sibling_flow/read_status");
        var start = harness.Sent.Should().ContainSingle().Subject.Message
            .Should().BeOfType<StartWorkflowEvent>().Subject;
        start.ExecutionContextDelta.Should().NotBeNull();
        start.ExecutionContextDelta.ClearCallerCredential.Should().BeTrue();
        start.ExecutionContextDelta.CallerCredential.BearerToken.Should().BeEmpty();
        start.ExecutionContextDelta.CallerCredential.RuntimeSecretReference.Ref
            .Should().Be("runtime-secret-ref");
        start.ExecutionContextDelta.CallerCredential.RuntimeSecretReference
            .Should().NotBeSameAs(state.ExecutionContext.CallerCredential.RuntimeSecretReference);
        start.ExecutionContextDelta.CallerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.AgentKey);
        start.ExecutionContextDelta.UnattendedEffectAuthorization.AuthorizationDigest
            .Should().Be("parent-authorization");
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenRequestedDepthExceedsLimit_ShouldPublishFailureBeforeSideEffects()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
            MaxSubWorkflowDepth = 2,
        };
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-too-deep",
                ParentRunId = "parent-run",
                ParentStepId = "step-depth",
                WorkflowName = "sub_flow",
                RequestedDepth = 3,
            },
            state,
            CancellationToken.None);

        var failure = harness.Published.Should().ContainSingle().Subject.Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.StepId.Should().Be("step-depth");
        failure.RunId.Should().Be("parent-run");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("depth");
        harness.Persisted.Should().BeEmpty();
        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenActiveFanoutLimitIsReached_ShouldPublishFailureBeforeSideEffects()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
            MaxActiveSubWorkflows = 1,
        };
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "existing",
            ParentRunId = "parent-run",
            ParentStepId = "step-existing",
            WorkflowName = "sub_flow",
            ChildRunId = "existing-child",
        });

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-fanout",
                ParentRunId = "parent-run",
                ParentStepId = "step-fanout",
                WorkflowName = "sub_flow",
            },
            state,
            CancellationToken.None);

        var failure = harness.Published.Should().ContainSingle().Subject.Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.StepId.Should().Be("step-fanout");
        failure.RunId.Should().Be("parent-run");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("active");
        harness.Persisted.Should().BeEmpty();
        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDefinitionResolvedAsync_WhenBindingStale_ShouldCreateAndBindNewChildActor()
    {
        const string definitionActorId = "workflow-definition:sub flow";
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.InlineWorkflowYamls["sub flow"] = ValidSubFlowWithSpaceYaml;
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "invoke-2",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub flow",
            DefinitionActorId = definitionActorId,
            Input = "payload-b",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-2"] = 0;
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "sub flow",
            ChildActorId = "owner-1:workflow:workflow-definition-sub-flow",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = definitionActorId,
            DefinitionVersion = 1,
        });

        await harness.Orchestrator.HandleDefinitionResolvedAsync(
            new SubWorkflowDefinitionResolvedEvent
            {
                InvocationId = "invoke-2",
                Definition = new WorkflowDefinitionSnapshot
                {
                    DefinitionActorId = definitionActorId,
                    WorkflowName = "sub flow",
                    WorkflowYaml = ValidSubFlowWithSpaceYaml,
                    ScopeId = "scope-a",
                    DefinitionVersion = 2,
                },
            },
            definitionActorId,
            state,
            CancellationToken.None);

        harness.Runtime.CreateRequests.Should().ContainSingle();
        var createdRequest = harness.Runtime.CreateRequests.Single();
        createdRequest.AgentType.Should().Be(typeof(WorkflowRunGAgent));
        createdRequest.RequestedId.Should().Be("owner-1:workflow:workflow-definition-sub-flow:serial-v1");
        harness.Runtime.Linked.Should().ContainSingle(x =>
            x.ParentId == "owner-1" &&
            x.ChildId == "owner-1:workflow:workflow-definition-sub-flow:serial-v1");
        harness.Persisted.OfType<SubWorkflowBindingUpsertedEvent>().Should().ContainSingle(x =>
            x.WorkflowName == "sub flow" &&
            x.ChildActorId == "owner-1:workflow:workflow-definition-sub-flow:serial-v1" &&
            x.DefinitionActorId == definitionActorId &&
            x.DefinitionVersion == 2 &&
            x.BindingGeneration == 1);
        harness.Persisted.OfType<SubWorkflowDefinitionResolvedEvent>().Should().ContainSingle(x => x.InvocationId == "invoke-2");
        harness.Persisted.OfType<SubWorkflowInvocationRegisteredEvent>().Should().ContainSingle(x =>
            x.ChildRunId == "invoke-2" &&
            x.DefinitionActorId == definitionActorId &&
            x.DefinitionVersion == 2);
        state.ScopeId = "scope-a";
        var childActor = harness.Runtime.StoredActors["owner-1:workflow:workflow-definition-sub-flow:serial-v1"];
        childActor.LastHandledEnvelope.Should().NotBeNull();
        childActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowRunDefinitionEvent.Descriptor).Should().BeTrue();
        var bindEvent = childActor.LastHandledEnvelope.Payload.Unpack<BindWorkflowRunDefinitionEvent>();
        bindEvent.RunId.Should().Be("invoke-2");
        bindEvent.WorkflowName.Should().Be("sub flow");
        bindEvent.DefinitionActorId.Should().Be(definitionActorId);
        bindEvent.BindingGeneration.Should().Be(1);
        bindEvent.InlineWorkflowYamls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenCreateRaces_ShouldReuseRacedChildActor()
    {
        const string childActorId = "owner-1:workflow:sub_flow:serial-v1";
        var racedActor = new RecordingActor(childActorId);
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        harness.Runtime.EnqueueGet(childActorId, null);
        harness.Runtime.EnqueueGet(childActorId, racedActor);
        harness.Runtime.FailCreateActorIds.Add(childActorId);

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-race",
                ParentRunId = "parent-run",
                ParentStepId = "step-race",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Singleton,
            },
            state,
            CancellationToken.None);

        racedActor.LastHandledEnvelope.Should().NotBeNull();
        racedActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowRunDefinitionEvent.Descriptor).Should().BeTrue();
        harness.Persisted.Should().Contain(x => x is SubWorkflowBindingUpsertedEvent);
        harness.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenRegistrationPersistFails_ShouldNotCreateChild()
    {
        var harness = CreateHarness();
        harness.FailPersistTypes.Add(typeof(SubWorkflowInvocationRegisteredEvent));
        var state = new WorkflowRunState();
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-registration-fails",
                ParentRunId = "parent-run",
                ParentStepId = "step-register",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
            },
            state,
            CancellationToken.None);

        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Runtime.Linked.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
        AssertPublishedFailureContains(harness, "persist failed");
        harness.Operations.Should().Contain("persist-fail:SubWorkflowInvocationRegisteredEvent");
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenCreateFails_ShouldPersistInvocationBeforeCreate()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
        };
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        var childActorId = "owner-1:workflow:sub_flow:parent-run:invoke-create-fails";
        harness.Runtime.FailCreateActorIds.Add(childActorId);

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-create-fails",
                ParentRunId = "parent-run",
                ParentStepId = "step-create",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
            },
            state,
            CancellationToken.None);

        var registered = harness.Persisted.OfType<SubWorkflowInvocationRegisteredEvent>().Should().ContainSingle().Subject;
        registered.ChildActorId.Should().Be(childActorId);
        registered.Input.Should().BeEmpty();
        registered.HandoffPhase.Should().Be((int)SubWorkflowInvocationHandoffPhase.Registered);
        harness.Runtime.Linked.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
        AssertPublishedFailureContains(harness, "failed to create or get sub-workflow actor");
        harness.Operations.Should().ContainInOrder(
            "persist:SubWorkflowInvocationRegisteredEvent",
            $"create:{childActorId}");
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenLinkFails_ShouldKeepRegisteredInvocationAndActorResolvedPhase()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
        };
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        var childActorId = "owner-1:workflow:sub_flow:parent-run:invoke-link-fails";
        harness.Runtime.FailLinkChildIds.Add(childActorId);

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-link-fails",
                ParentRunId = "parent-run",
                ParentStepId = "step-link",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
            },
            state,
            CancellationToken.None);

        harness.Persisted.OfType<SubWorkflowInvocationRegisteredEvent>().Should().ContainSingle(x => x.ChildActorId == childActorId);
        harness.Persisted.OfType<SubWorkflowInvocationHandoffAdvancedEvent>().Should().ContainSingle(x =>
            x.ChildRunId == "invoke-link-fails" &&
            x.HandoffPhase == (int)SubWorkflowInvocationHandoffPhase.ActorResolved);
        harness.Sent.Should().BeEmpty();
        AssertPublishedFailureContains(harness, "link failed");
        harness.Operations.Should().ContainInOrder(
            "persist:SubWorkflowInvocationRegisteredEvent",
            $"create:{childActorId}",
            "persist:SubWorkflowInvocationHandoffAdvancedEvent",
            $"link:{childActorId}");
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenBindFails_ShouldKeepLinkedPhase()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
        };
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        var childActorId = "owner-1:workflow:sub_flow:parent-run:invoke-bind-fails";
        harness.Runtime.FailDispatchActorIds.Add(childActorId);

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-bind-fails",
                ParentRunId = "parent-run",
                ParentStepId = "step-bind",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
            },
            state,
            CancellationToken.None);

        harness.Persisted.OfType<SubWorkflowInvocationRegisteredEvent>().Should().ContainSingle(x => x.ChildActorId == childActorId);
        harness.Persisted.OfType<SubWorkflowInvocationHandoffAdvancedEvent>().Should().Contain(x =>
            x.ChildRunId == "invoke-bind-fails" &&
            x.HandoffPhase == (int)SubWorkflowInvocationHandoffPhase.Linked);
        harness.Persisted.OfType<SubWorkflowInvocationHandoffAdvancedEvent>().Should().NotContain(x =>
            x.HandoffPhase == (int)SubWorkflowInvocationHandoffPhase.Bound);
        harness.Sent.Should().BeEmpty();
        AssertPublishedFailureContains(harness, "dispatch failed");
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenStartFails_ShouldKeepDurablePendingHandoffForRetry()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
        };
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        var childActorId = "owner-1:workflow:sub_flow:parent-run:invoke-start-fails";
        harness.FailStartActorIds.Add(childActorId);

        var act = () => harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-start-fails",
                ParentRunId = "parent-run",
                ParentStepId = "step-start",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
            },
            state,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<SubWorkflowOrchestrator.SubWorkflowStartDispatchPendingException>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        harness.Persisted.OfType<SubWorkflowInvocationHandoffAdvancedEvent>().Should().Contain(x =>
            x.ChildRunId == "invoke-start-fails" &&
            x.HandoffPhase == (int)SubWorkflowInvocationHandoffPhase.StartDispatchPending);
        harness.Persisted.OfType<SubWorkflowInvocationHandoffAdvancedEvent>().Should().NotContain(x =>
            x.HandoffPhase == (int)SubWorkflowInvocationHandoffPhase.StartFailed);
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>().Should().BeEmpty();
        harness.Runtime.Unlinked.Should().BeEmpty();
        harness.Runtime.Destroyed.Should().BeEmpty();
        harness.Published.OfType<PublishedMessage>()
            .Where(x => x.Message is StepCompletedEvent)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task RecoverPendingSubWorkflowInvocationsAsync_ShouldResumeFromStoredPhaseWithoutChangingChildActorId()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
        };
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-recover",
            ParentRunId = "parent-run",
            ParentStepId = "step-recover",
            WorkflowName = "sub_flow",
            ChildActorId = "child-recover",
            ChildRunId = "invoke-recover",
            Lifecycle = WorkflowCallLifecycle.Transient,
            HandoffPhase = SubWorkflowInvocationHandoffPhase.Linked,
            Input = "payload-recover",
            DefinitionYaml = ValidSubFlowYaml,
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["invoke-recover"] = 0;
        harness.Runtime.StoredActors["child-recover"] = new RecordingActor("child-recover");

        await harness.Orchestrator.RecoverPendingSubWorkflowInvocationsAsync(state, CancellationToken.None);

        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Runtime.Linked.Should().BeEmpty();
        harness.Runtime.StoredActors.Should().ContainKey("child-recover");
        harness.Persisted.OfType<SubWorkflowInvocationHandoffAdvancedEvent>().Select(x => x.HandoffPhase)
            .Should()
            .ContainInOrder(
                (int)SubWorkflowInvocationHandoffPhase.Bound,
                (int)SubWorkflowInvocationHandoffPhase.StartDispatchPending,
                (int)SubWorkflowInvocationHandoffPhase.StartDispatched);
        harness.Sent.Should().ContainSingle(x => x.TargetActorId == "child-recover");
        var start = harness.Sent.Single().Message.Should().BeOfType<StartWorkflowEvent>().Subject;
        start.RunId.Should().Be("invoke-recover");
        start.Input.Should().Be("payload-recover");
    }

    [Theory]
    [InlineData(SubWorkflowInvocationHandoffPhase.Registered, true, true, true, true)]
    [InlineData(SubWorkflowInvocationHandoffPhase.ActorResolved, false, true, true, true)]
    [InlineData(SubWorkflowInvocationHandoffPhase.Bound, false, false, false, true)]
    [InlineData(SubWorkflowInvocationHandoffPhase.StartDispatchPending, false, false, false, true)]
    [InlineData(SubWorkflowInvocationHandoffPhase.StartFailed, false, false, false, true)]
    public async Task RecoverPendingSubWorkflowInvocationsAsync_ShouldResumeFromPhaseWithoutRepeatingCompletedHandoff(
        SubWorkflowInvocationHandoffPhase phase,
        bool expectCreate,
        bool expectLink,
        bool expectBind,
        bool expectStart)
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
        };
        var childActorId = $"child-{phase}";
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-recover-" + (int)phase,
            ParentRunId = "parent-run",
            ParentStepId = "step-recover",
            WorkflowName = "sub_flow",
            ChildActorId = childActorId,
            ChildRunId = "invoke-recover-" + (int)phase,
            Lifecycle = WorkflowCallLifecycle.Transient,
            HandoffPhase = phase,
            Input = "payload-recover",
            DefinitionYaml = ValidSubFlowYaml,
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["invoke-recover-" + (int)phase] = 0;
        if (!expectCreate)
            harness.Runtime.StoredActors[childActorId] = new RecordingActor(childActorId);

        await harness.Orchestrator.RecoverPendingSubWorkflowInvocationsAsync(state, CancellationToken.None);

        harness.Runtime.CreateRequests.Any(x => x.RequestedId == childActorId).Should().Be(expectCreate);
        harness.Runtime.Linked.Any(x => x.ChildId == childActorId).Should().Be(expectLink);
        harness.Operations.Any(x => x == $"dispatch:{childActorId}").Should().Be(expectBind);
        harness.Sent.Any(x => x.TargetActorId == childActorId).Should().Be(expectStart);
        harness.Persisted.OfType<SubWorkflowInvocationHandoffAdvancedEvent>().Should().Contain(x =>
            x.HandoffPhase == (int)SubWorkflowInvocationHandoffPhase.StartDispatched);
    }

    [Fact]
    public async Task RecoverPendingSubWorkflowInvocationsAsync_WhenLegacySingletonIsLinked_ShouldFinishOnOriginalActorAsSingleRun()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        };
        const string childActorId = "owner-1:workflow:sub_flow";
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "legacy-invoke",
            ParentRunId = "parent-run",
            ParentStepId = "legacy-step",
            WorkflowName = "sub_flow",
            ChildActorId = childActorId,
            ChildRunId = "legacy-invoke",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            HandoffPhase = SubWorkflowInvocationHandoffPhase.Linked,
            DefinitionYaml = ValidSubFlowYaml,
            BindingGeneration = 0,
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["legacy-invoke"] = 0;
        var child = new RecordingActor(childActorId);
        harness.Runtime.StoredActors[childActorId] = child;

        await harness.Orchestrator.RecoverPendingSubWorkflowInvocationsAsync(state, CancellationToken.None);

        var binding = child.LastHandledEnvelope!.Payload!.Unpack<BindWorkflowRunDefinitionEvent>();
        binding.RunId.Should().Be("legacy-invoke");
        binding.ReusePolicy.Should().Be(WorkflowRunActorReusePolicy.SingleRun);
        binding.BindingGeneration.Should().Be(0);
        binding.ReuseAuthorityActorId.Should().BeEmpty();
        var start = harness.Sent.Should().ContainSingle(x => x.TargetActorId == childActorId)
            .Subject.Message.Should().BeOfType<StartWorkflowEvent>().Subject;
        start.RunId.Should().Be("legacy-invoke");
        start.BindingGeneration.Should().Be(0);
    }

    [Fact]
    public async Task RecoverPendingSubWorkflowInvocationsAsync_WhenReenteredWithSameInvocationAndChildActor_ShouldNotDuplicateActorLinkOrBind()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState
        {
            RunId = "parent-run",
        };
        const string childActorId = "child-reenter";
        state.InlineWorkflowYamls["sub_flow"] = ValidSubFlowYaml;
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-reenter",
            ParentRunId = "parent-run",
            ParentStepId = "step-reenter",
            WorkflowName = "sub_flow",
            ChildActorId = childActorId,
            ChildRunId = "invoke-reenter",
            Lifecycle = WorkflowCallLifecycle.Transient,
            HandoffPhase = SubWorkflowInvocationHandoffPhase.StartDispatchPending,
            Input = "payload-reenter",
            DefinitionYaml = ValidSubFlowYaml,
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["invoke-reenter"] = 0;
        harness.Runtime.StoredActors[childActorId] = new RecordingActor(childActorId);

        await harness.Orchestrator.RecoverPendingSubWorkflowInvocationsAsync(state, CancellationToken.None);
        await harness.Orchestrator.RecoverPendingSubWorkflowInvocationsAsync(state, CancellationToken.None);

        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Runtime.Linked.Should().BeEmpty();
        harness.Operations.Should().NotContain($"dispatch:{childActorId}");
        harness.Sent.Should().ContainSingle(x => x.TargetActorId == childActorId);
    }

    [Fact]
    public async Task HandleDefinitionResolutionTimeoutFiredAsync_WhenLeaseMatches_ShouldClearAndPublishFailure()
    {
        var harness = CreateHarness();
        var state = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered(
            new WorkflowRunState(),
            new SubWorkflowDefinitionResolutionRegisteredEvent
            {
                InvocationId = "invoke-timeout",
                ParentRunId = "parent-run",
                ParentStepId = "step-timeout",
                WorkflowName = "sub_flow",
                DefinitionActorId = "workflow-definition:sub_flow",
                Lifecycle = WorkflowCallLifecycle.Singleton,
                TimeoutCallbackId = "cb-timeout",
                TimeoutCallbackActorId = "owner-1",
                TimeoutCallbackGeneration = 7,
                TimeoutCallbackBackend = (int)WorkflowRuntimeCallbackBackendState.InMemory,
                TimeoutMs = 30_000,
            });
        var inboundEnvelope = new EventEnvelope
        {
            Payload = Any.Pack(new SubWorkflowDefinitionResolutionTimeoutFiredEvent
            {
                InvocationId = "invoke-timeout",
                TimeoutMs = 30_000,
            }),
            Runtime = new EnvelopeRuntime
            {
                Callback = new EnvelopeCallbackContext
                {
                    CallbackId = "cb-timeout",
                    Generation = 7,
                },
            },
        };

        await harness.Orchestrator.HandleDefinitionResolutionTimeoutFiredAsync(
            new SubWorkflowDefinitionResolutionTimeoutFiredEvent
            {
                InvocationId = "invoke-timeout",
                ParentRunId = "parent-run",
                ParentStepId = "step-timeout",
                WorkflowName = "sub_flow",
                DefinitionActorId = "workflow-definition:sub_flow",
                TimeoutMs = 30_000,
            },
            inboundEnvelope,
            state,
            CancellationToken.None);

        harness.Persisted.OfType<SubWorkflowDefinitionResolutionTimeoutFiredEvent>().Should().ContainSingle(x => x.InvocationId == "invoke-timeout");
        harness.Persisted.OfType<SubWorkflowDefinitionResolutionClearedEvent>().Should().ContainSingle(x => x.InvocationId == "invoke-timeout");
        var failure = harness.Published.Should().ContainSingle().Subject.Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.RunId.Should().Be("parent-run");
        failure.StepId.Should().Be("step-timeout");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("timed out waiting for definition resolution");
    }

    [Fact]
    public async Task HandleDefinitionResolveFailedAsync_WhenPendingResolutionExists_ShouldClearAndPublishFailure()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "invoke-failed",
            ParentRunId = "parent-run",
            ParentStepId = "step-failed",
            WorkflowName = "sub_flow",
            DefinitionActorId = "workflow-definition:sub_flow",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            TimeoutLease = new WorkflowRuntimeCallbackLeaseState
            {
                ActorId = "owner-1",
                CallbackId = "cb-failed",
                Generation = 3,
                Backend = WorkflowRuntimeCallbackBackendState.InMemory,
            },
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-failed"] = 0;

        await harness.Orchestrator.HandleDefinitionResolveFailedAsync(
            new SubWorkflowDefinitionResolveFailedEvent
            {
                InvocationId = "invoke-failed",
                DefinitionActorId = "workflow-definition:sub_flow",
                Error = "definition lookup failed",
            },
            "workflow-definition:sub_flow",
            state,
            CancellationToken.None);

        harness.Persisted.OfType<SubWorkflowDefinitionResolveFailedEvent>().Should().ContainSingle(x => x.InvocationId == "invoke-failed");
        harness.Persisted.OfType<SubWorkflowDefinitionResolutionClearedEvent>().Should().ContainSingle(x => x.InvocationId == "invoke-failed");
        harness.CancelledLeases.Should().ContainSingle(x => x.CallbackId == "cb-failed" && x.Generation == 3);
        var failure = harness.Published.Should().ContainSingle().Subject.Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.RunId.Should().Be("parent-run");
        failure.StepId.Should().Be("step-failed");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Be("definition lookup failed");
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenRunIdMissingOrUnknown_ShouldReturnFalse()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState();

        var missingRunId = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent { RunId = " " },
            "child-1",
            state,
            CancellationToken.None);
        var unknownRunId = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent { RunId = "child-404" },
            "child-1",
            state,
            CancellationToken.None);

        missingRunId.Should().BeFalse();
        unknownRunId.Should().BeFalse();
        harness.Persisted.Should().BeEmpty();
        harness.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenPublisherMismatch_ShouldReturnTrueWithoutCompleting()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-1",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            ChildActorId = "child-1",
            ChildRunId = "child-run-1",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        var handled = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent
            {
                RunId = "child-run-1",
                Success = true,
                Output = "done",
            },
            "child-2",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.Should().BeEmpty();
        harness.Published.Should().BeEmpty();
        harness.Runtime.Destroyed.Should().BeEmpty();
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenTransientChildCompletes_ShouldPublishParentCompletionAndCleanup()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-2",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            ChildActorId = "child-transient",
            ChildRunId = "child-run-2",
            Lifecycle = WorkflowCallLifecycle.Transient,
        });

        var handled = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent
            {
                RunId = "child-run-2",
                Success = true,
                Output = "child-output",
            },
            "child-transient",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>().Should().ContainSingle(x =>
            x.InvocationId == "invoke-2" &&
            x.Success);
        var parentCompletion = harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Subject;
        parentCompletion.StepId.Should().Be("step-b");
        parentCompletion.RunId.Should().Be("parent-run");
        parentCompletion.Annotations["workflow_call.child_actor_id"].Should().Be("child-transient");
        parentCompletion.Annotations["workflow_call.child_run_id"].Should().Be("child-run-2");
        harness.Runtime.Unlinked.Should().ContainSingle("child-transient");
        harness.Runtime.Destroyed.Should().ContainSingle("child-transient");
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenChildActorIdMissing_ShouldCompleteWithoutCleanup()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-3",
            ParentRunId = "parent-run",
            ParentStepId = "step-c",
            WorkflowName = "sub_flow",
            ChildActorId = " ",
            ChildRunId = "child-run-3",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        var handled = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent
            {
                RunId = "child-run-3",
                Success = false,
                Error = "failed",
            },
            "publisher-ignored",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>().Should().ContainSingle(x =>
            !x.Success);
        harness.Published.Should().ContainSingle();
        harness.Runtime.Unlinked.Should().BeEmpty();
        harness.Runtime.Destroyed.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvocationCompletedAsync_WhenCompensatedChildFails_ShouldPersistCompensatedTerminalOutcome()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-compensated",
            ParentRunId = "parent-run",
            ParentStepId = "step-compensated",
            WorkflowName = "sub_flow",
            ChildActorId = "child-compensated",
            ChildRunId = "child-run-compensated",
            Lifecycle = WorkflowCallLifecycle.Transient,
        });

        var handled = await harness.Orchestrator.HandleInvocationCompletedAsync(
            new SubWorkflowInvocationCompletedEvent
            {
                InvocationId = "invoke-compensated",
                ChildRunId = "child-run-compensated",
                Success = false,
                Error = "child failed after compensation",
                Compensated = true,
            },
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        var completion = harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        completion.Compensated.Should().BeTrue();
        completion.Success.Should().BeFalse();
        var parentCompletion = harness.Published.Should().ContainSingle().Subject.Message.Should().BeOfType<StepCompletedEvent>().Subject;
        parentCompletion.Success.Should().BeFalse();
        parentCompletion.Error.Should().Be("child failed after compensation");
        harness.Runtime.Unlinked.Should().ContainSingle("child-compensated");
        harness.Runtime.Destroyed.Should().ContainSingle("child-compensated");
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenOrdinaryChildCompletes_ShouldPersistCompensatedFalse()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-ordinary",
            ParentRunId = "parent-run",
            ParentStepId = "step-ordinary",
            WorkflowName = "sub_flow",
            ChildActorId = "child-ordinary",
            ChildRunId = "child-run-ordinary",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        var handled = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent
            {
                RunId = "child-run-ordinary",
                Success = false,
                Error = "ordinary child failure",
            },
            "child-ordinary",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>()
            .Should()
            .ContainSingle()
            .Which.Compensated.Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleStoppedAsync_WhenTransientChildStops_ShouldPublishParentFailureAndCleanup()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-stop-1",
            ParentRunId = "parent-run",
            ParentStepId = "step-stop-a",
            WorkflowName = "sub_flow",
            ChildActorId = "child-stop-1",
            ChildRunId = "child-run-stop-1",
            Lifecycle = WorkflowCallLifecycle.Transient,
        });

        var handled = await harness.Orchestrator.TryHandleStoppedAsync(
            new WorkflowStoppedEvent
            {
                RunId = "child-run-stop-1",
                Reason = "manual",
            },
            "child-stop-1",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>().Should().ContainSingle(x =>
            x.InvocationId == "invoke-stop-1" &&
            !x.Success &&
            x.Error.Contains("manual", StringComparison.Ordinal) &&
            !x.Compensated);
        var parentCompletion = harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Subject;
        parentCompletion.StepId.Should().Be("step-stop-a");
        parentCompletion.RunId.Should().Be("parent-run");
        parentCompletion.Success.Should().BeFalse();
        parentCompletion.Error.Should().Contain("manual");
        parentCompletion.Annotations["workflow_call.child_actor_id"].Should().Be("child-stop-1");
        parentCompletion.Annotations["workflow_call.child_run_id"].Should().Be("child-run-stop-1");
        harness.Runtime.Unlinked.Should().ContainSingle("child-stop-1");
        harness.Runtime.Destroyed.Should().ContainSingle("child-stop-1");
    }

    [Fact]
    public async Task TryHandleRunStoppedAsync_WhenChildActorIdMissing_ShouldPublishFailureWithoutCleanup()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-stop-2",
            ParentRunId = "parent-run",
            ParentStepId = "step-stop-b",
            WorkflowName = "sub_flow",
            ChildActorId = " ",
            ChildRunId = "child-run-stop-2",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        var handled = await harness.Orchestrator.TryHandleRunStoppedAsync(
            new WorkflowRunStoppedEvent
            {
                RunId = "child-run-stop-2",
                Reason = "manual",
            },
            "publisher-ignored",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>().Should().ContainSingle(x =>
            x.InvocationId == "invoke-stop-2" &&
            !x.Success &&
            x.Error.Contains("manual", StringComparison.Ordinal) &&
            !x.Compensated);
        harness.Published.Should().ContainSingle();
        harness.Runtime.Unlinked.Should().BeEmpty();
        harness.Runtime.Destroyed.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupPendingInvocationsForRunAsync_ShouldPersistCompletions_AndCleanupOnlyNonSingletonChildren()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "indexed-singleton",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            ChildActorId = "child-singleton",
            ChildRunId = "child-run-a",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-run-a"] = 0;
        state.PendingChildRunIdsByParentRunId["parent-run"] = new WorkflowRunState.Types.ChildRunIdSet
        {
            ChildRunIds = { "child-run-a" },
        };
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "scan-transient",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            ChildActorId = "child-transient",
            ChildRunId = "child-run-b",
            Lifecycle = WorkflowCallLifecycle.Transient,
        });

        await harness.Orchestrator.CleanupPendingInvocationsForRunAsync(" parent-run ", state, CancellationToken.None);

        harness.Persisted.Should().HaveCount(2);
        harness.Persisted.Should().OnlyContain(x => x is SubWorkflowInvocationCompletedEvent);
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>()
            .Should()
            .OnlyContain(x => !x.Compensated);
        harness.Runtime.Unlinked.Should().ContainSingle("child-transient");
        harness.Runtime.Destroyed.Should().ContainSingle("child-transient");
        harness.Runtime.Unlinked.Should().NotContain("child-singleton");
    }

    [Fact]
    public void ApplyStateTransitions_ShouldMaintainBindingAndInvocationIndexes()
    {
        var state = new WorkflowRunState();
        state = SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted(state, new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = "sub_flow",
            ChildActorId = "child-1",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 1,
        });
        state = SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted(state, new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = "sub_flow",
            ChildActorId = "child-2",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 2,
        });
        state = SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted(state, new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = " ",
            ChildActorId = "child-ignored",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        state.SubWorkflowBindings.Should().ContainSingle();
        state.SubWorkflowBindings.Single().ChildActorId.Should().Be("child-2");
        state.SubWorkflowBindings.Single().DefinitionVersion.Should().Be(2);

        state = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered(state, new SubWorkflowDefinitionResolutionRegisteredEvent
        {
            InvocationId = "invoke-a",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            DefinitionActorId = "workflow-definition:sub_flow",
            Input = "payload-a",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            TimeoutCallbackId = "cb-a",
            TimeoutCallbackActorId = "owner-1",
            TimeoutCallbackGeneration = 11,
            TimeoutCallbackBackend = (int)WorkflowRuntimeCallbackBackendState.InMemory,
            TimeoutMs = 30_000,
        });
        state.PendingSubWorkflowDefinitionResolutions.Should().ContainSingle(x => x.InvocationId == "invoke-a");
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-a"].Should().Be(0);
        state.PendingSubWorkflowDefinitionResolutions.Single().TimeoutCallbackId.Should().Be("cb-a");
        state.PendingSubWorkflowDefinitionResolutions.Single().TimeoutMs.Should().Be(30_000);

        state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state, new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = "invoke-a",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            ChildActorId = "child-2",
            ChildRunId = "child-run-a",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 2,
        });
        state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state, new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = "invoke-b",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            ChildActorId = "child-3",
            ChildRunId = "child-run-b",
            Lifecycle = WorkflowCallLifecycle.Transient,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 2,
        });
        state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state, new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = "invoke-a",
            ParentRunId = "parent-run",
            ParentStepId = "step-a2",
            WorkflowName = "sub_flow",
            ChildActorId = "child-4",
            ChildRunId = "child-run-a",
            Lifecycle = WorkflowCallLifecycle.Scope,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 2,
        });

        state.PendingSubWorkflowInvocations.Should().HaveCount(2);
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().NotContainKey("invoke-a");
        state.PendingSubWorkflowDefinitionResolutions.Should().BeEmpty();
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-run-a"].Should().BeGreaterThanOrEqualTo(0);
        state.PendingChildRunIdsByParentRunId["parent-run"].ChildRunIds.Should().Contain(["child-run-a", "child-run-b"]);

        state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationCompleted(state, new SubWorkflowInvocationCompletedEvent
        {
            InvocationId = "invoke-a",
            ChildRunId = "child-run-a",
        });

        state.PendingSubWorkflowInvocations.Should().ContainSingle(x => x.ChildRunId == "child-run-b");
        state.PendingSubWorkflowInvocationIndexByChildRunId.Should().ContainKey("child-run-b");
        state.PendingSubWorkflowInvocationIndexByChildRunId.Should().NotContainKey("child-run-a");
        state.PendingChildRunIdsByParentRunId["parent-run"].ChildRunIds.Should().ContainSingle(x => x == "child-run-b");
    }

    [Fact]
    public void ApplySubWorkflowInvocationRegistered_ShouldAppendTypedSubWorkflowChildLineage()
    {
        var state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(
            new WorkflowRunState
            {
                RunId = "run-parent-alpha",
            },
            new SubWorkflowInvocationRegisteredEvent
            {
                InvocationId = "invoke-sub-001",
                ParentRunId = "run-parent-alpha",
                ParentStepId = "step-call-child",
                WorkflowName = "sub_flow",
                ChildActorId = "actor-child-delta",
                ChildRunId = "run-child-beta",
                Lifecycle = WorkflowCallLifecycle.Transient,
                DefinitionActorId = "workflow-definition:sub_flow",
                DefinitionVersion = 2,
                RootRunId = "run-root-omega",
                Depth = 1,
            });

        state.Lineage.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        state.Lineage.SubWorkflow.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        state.Lineage.RetryFork.Availability.Should().Be(WorkflowRunLineageAvailability.Unavailable);
        var child = state.Lineage.SubWorkflow.ChildRuns.Should().ContainSingle().Subject;
        child.RunId.Should().Be("run-child-beta");
        child.ActorId.Should().Be("actor-child-delta");
        child.RelationshipId.Should().Be("invoke-sub-001");
        child.StepId.Should().Be("step-call-child");
        child.RelationKind.Should().Be(WorkflowRunLineageRelationKind.SubWorkflow);
    }

    [Fact]
    public void PruneIdleSubWorkflowBindings_ShouldKeepReferencedAndPendingSingletons()
    {
        var state = new WorkflowRunState();
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_ref",
            ChildActorId = "child-ref",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_nested",
            ChildActorId = "child-nested",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_pending",
            ChildActorId = "child-pending",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_idle",
            ChildActorId = "child-idle",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_transient",
            ChildActorId = "child-transient",
            Lifecycle = WorkflowCallLifecycle.Transient,
        });
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-pending",
            ParentRunId = "parent-run",
            ParentStepId = "step-pending",
            WorkflowName = "wf_pending",
            ChildActorId = "child-pending",
            ChildRunId = "child-run-pending",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        var workflow = new WorkflowDefinition
        {
            Name = "wf-parent",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "call-root",
                    Type = "workflow_call",
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["workflow"] = "wf_ref",
                        ["lifecycle"] = WorkflowCallLifecycle.Singleton,
                    },
                    Children =
                    [
                        new StepDefinition
                        {
                            Id = "call-nested",
                            Type = "workflow_call",
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["workflow"] = "wf_nested",
                                ["lifecycle"] = WorkflowCallLifecycle.Singleton,
                            },
                        },
                    ],
                },
            ],
        };

        SubWorkflowOrchestrator.PruneIdleSubWorkflowBindings(state, workflow);

        state.SubWorkflowBindings.Select(x => x.WorkflowName).Should().BeEquivalentTo("wf_ref", "wf_nested", "wf_pending");
    }

    private static OrchestratorHarness CreateHarness()
    {
        var runtime = new RecordingActorRuntime();
        var persisted = new List<IMessage>();
        var published = new List<PublishedMessage>();
        var sent = new List<SentMessage>();
        var scheduledTimeouts = new List<ScheduledTimeout>();
        var cancelledLeases = new List<RuntimeCallbackLease>();
        var operations = new List<string>();
        var failPersistTypes = new HashSet<global::System.Type>();
        var failStartActorIds = new HashSet<string>(StringComparer.Ordinal);
        runtime.Operations = operations;

        var orchestrator = new SubWorkflowOrchestrator(
            runtime,
            runtime,
            () => "owner-1",
            () => NullLogger.Instance,
            (evt, _) =>
            {
                operations.Add($"persist:{evt.GetType().Name}");
                if (failPersistTypes.Contains(evt.GetType()))
                {
                    operations[^1] = $"persist-fail:{evt.GetType().Name}";
                    throw new InvalidOperationException($"persist failed for {evt.GetType().Name}");
                }

                persisted.Add(evt);
                return Task.CompletedTask;
            },
            (events, _) =>
            {
                foreach (var evt in events)
                {
                    operations.Add($"persist:{evt.GetType().Name}");
                    if (failPersistTypes.Contains(evt.GetType()))
                    {
                        operations[^1] = $"persist-fail:{evt.GetType().Name}";
                        throw new InvalidOperationException($"persist failed for {evt.GetType().Name}");
                    }
                }

                persisted.AddRange(events);
                return Task.CompletedTask;
            },
            (evt, direction, _) =>
            {
                published.Add(new PublishedMessage(evt, direction));
                return Task.CompletedTask;
            },
            (targetActorId, evt, _) =>
            {
                operations.Add($"send:{targetActorId}:{evt.GetType().Name}");
                if (evt is StartWorkflowEvent && failStartActorIds.Contains(targetActorId))
                    throw new InvalidOperationException($"start failed for {targetActorId}");

                sent.Add(new SentMessage(targetActorId, evt));
                return Task.CompletedTask;
            },
            (callbackId, dueTime, evt, _) =>
            {
                var lease = new RuntimeCallbackLease("owner-1", callbackId, scheduledTimeouts.Count + 1, RuntimeCallbackBackend.InMemory);
                scheduledTimeouts.Add(new ScheduledTimeout(callbackId, dueTime, evt, lease));
                return Task.FromResult(lease);
            },
            (lease, _) =>
            {
                cancelledLeases.Add(lease);
                return Task.CompletedTask;
            });

        return new OrchestratorHarness(
            orchestrator,
            runtime,
            persisted,
            published,
            sent,
            scheduledTimeouts,
            cancelledLeases,
            operations,
            failPersistTypes,
            failStartActorIds);
    }

    private static WorkflowRunState BuildStateWithPending(
        params WorkflowRunState.Types.PendingSubWorkflowInvocation[] pendingInvocations)
    {
        var state = new WorkflowRunState();
        for (var i = 0; i < pendingInvocations.Length; i++)
        {
            var pending = pendingInvocations[i];
            state.PendingSubWorkflowInvocations.Add(pending);
            if (!string.IsNullOrWhiteSpace(pending.ChildRunId))
                state.PendingSubWorkflowInvocationIndexByChildRunId[pending.ChildRunId] = i;

            if (!string.IsNullOrWhiteSpace(pending.ParentRunId) &&
                !string.IsNullOrWhiteSpace(pending.ChildRunId))
            {
                if (!state.PendingChildRunIdsByParentRunId.TryGetValue(pending.ParentRunId, out var childRuns))
                {
                    childRuns = new WorkflowRunState.Types.ChildRunIdSet();
                    state.PendingChildRunIdsByParentRunId[pending.ParentRunId] = childRuns;
                }

                childRuns.ChildRunIds.Add(pending.ChildRunId);
            }
        }

        return state;
    }

    private static void AssertPublishedFailureContains(OrchestratorHarness harness, string expectedText)
    {
        harness.Published.Should().ContainSingle();
        var failure = harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain(expectedText);
    }

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = "file_key_1",
            FileName = $"{fileId}.pdf",
            MediaType = "application/pdf",
            SizeBytes = 1234,
            Sha256 = $"sha-{fileId}",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
            OwnerRunId = "parent-run",
            OwnerScopeId = "scope-1",
        };

    private sealed record OrchestratorHarness(
        SubWorkflowOrchestrator Orchestrator,
        RecordingActorRuntime Runtime,
        List<IMessage> Persisted,
        List<PublishedMessage> Published,
        List<SentMessage> Sent,
        List<ScheduledTimeout> ScheduledTimeouts,
        List<RuntimeCallbackLease> CancelledLeases,
        List<string> Operations,
        HashSet<global::System.Type> FailPersistTypes,
        HashSet<string> FailStartActorIds);

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        private readonly Dictionary<string, Queue<IActor?>> _queuedGets = new(StringComparer.Ordinal);
        private int _createdCount;

        public List<string> Operations { get; set; } = [];

        public Dictionary<string, RecordingActor> StoredActors { get; } = new(StringComparer.Ordinal);

        public List<(global::System.Type AgentType, string? RequestedId)> CreateRequests { get; } = [];

        public List<(string ParentId, string ChildId)> Linked { get; } = [];

        public List<string> Unlinked { get; } = [];

        public List<string> Destroyed { get; } = [];

        public HashSet<string> FailCreateActorIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailLinkChildIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailDispatchActorIds { get; } = new(StringComparer.Ordinal);

        public void EnqueueGet(string actorId, IActor? actor)
        {
            if (!_queuedGets.TryGetValue(actorId, out var queue))
            {
                queue = new Queue<IActor?>();
                _queuedGets[actorId] = queue;
            }

            queue.Enqueue(actor);
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var resolvedId = id ?? $"created-{++_createdCount}";
            Operations.Add($"create:{resolvedId}");
            CreateRequests.Add((agentType, resolvedId));
            if (FailCreateActorIds.Contains(resolvedId))
                throw new InvalidOperationException($"create failed for {resolvedId}");

            var actor = new RecordingActor(resolvedId);
            StoredActors[resolvedId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Operations.Add($"destroy:{id}");
            Destroyed.Add(id);
            StoredActors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            if (_queuedGets.TryGetValue(id, out var queue) && queue.Count > 0)
            {
                var queuedActor = queue.Dequeue();
                if (queuedActor is RecordingActor recordingActor)
                    StoredActors[id] = recordingActor;

                return Task.FromResult(queuedActor);
            }

            return Task.FromResult<IActor?>(StoredActors.TryGetValue(id, out var actor) ? actor : null);
        }

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Operations.Add($"dispatch:{actorId}");
            if (FailDispatchActorIds.Contains(actorId))
                throw new InvalidOperationException($"dispatch failed for {actorId}");

            var actor = await GetAsync(actorId) ?? throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(StoredActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Operations.Add($"link:{childId}");
            if (FailLinkChildIds.Contains(childId))
                throw new InvalidOperationException($"link failed for {childId}");

            Linked.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Operations.Add($"unlink:{childId}");
            Unlinked.Add(childId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new StubAgent(id + ":agent");

        public EventEnvelope? LastHandledEnvelope { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastHandledEnvelope = envelope;
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<global::System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<global::System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record PublishedMessage(
        IMessage Message,
        TopologyAudience Direction);

    private sealed record SentMessage(
        string TargetActorId,
        IMessage Message);

    private sealed record ScheduledTimeout(
        string CallbackId,
        TimeSpan DueTime,
        IMessage Message,
        RuntimeCallbackLease Lease);
}
