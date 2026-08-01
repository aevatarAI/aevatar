using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunActorPortBranchTests
{
    [Fact]
    public async Task EnsureDefinitionAsync_WhenDefinitionIsInvalid_ShouldRejectBeforeLifecycleMutation()
    {
        var runtime = new RecordingActorRuntime();
        var preflight = new RejectingArtifactCompatibilityPreflight("WORKFLOW_DEFINITION_INVALID");
        var port = CreatePort(runtime, artifactPreflight: preflight);

        var act = () => port.EnsureDefinitionAsync(
            InteractiveBinding(definitionActorId: string.Empty),
            ct: CancellationToken.None);

        var error = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        error.Which.Readiness.Blockers.Should().ContainSingle()
            .Which.Code.Should().Be("WORKFLOW_DEFINITION_INVALID");
        AssertNoLifecycleMutations(runtime);
    }

    [Fact]
    public async Task CreateRunAsync_WhenNyxIdAuthoringIsRetired_ShouldRejectBeforeLifecycleMutation()
    {
        var runtime = new RecordingActorRuntime();
        var preflight = new RejectingArtifactCompatibilityPreflight(
            "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
        var port = CreatePort(runtime, artifactPreflight: preflight);

        var act = () => port.CreateRunAsync(
            InteractiveBinding(definitionActorId: string.Empty),
            CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        AssertNoLifecycleMutations(runtime);
    }

    [Fact]
    public async Task EnsureRunAsync_WhenAdmissionPlanIsAbsent_ShouldRejectBeforeLifecycleMutation()
    {
        var runtime = new RecordingActorRuntime();
        var preflight = new RejectingArtifactCompatibilityPreflight("CAPABILITY_ADMISSION_REBIND_REQUIRED");
        var port = CreatePort(runtime, artifactPreflight: preflight);

        var act = () => port.EnsureRunAsync(
            InteractiveBinding(definitionActorId: string.Empty),
            "run-alpha",
            CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        AssertNoLifecycleMutations(runtime);
    }

    [Fact]
    public async Task EnsureRunAndDispatchAsync_WhenAdmissionPlanMismatches_ShouldRejectBeforeLifecycleMutation()
    {
        var runtime = new RecordingActorRuntime();
        var preflight = new RejectingArtifactCompatibilityPreflight("CAPABILITY_ADMISSION_REBIND_REQUIRED");
        var port = CreatePort(runtime, artifactPreflight: preflight);

        var act = () => port.EnsureRunAndDispatchAsync(
            InteractiveBinding(definitionActorId: string.Empty),
            "run-alpha",
            new WorkflowChatRequestEvent { Prompt = "execute" },
            "cmd-alpha",
            "corr-alpha",
            CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        AssertNoLifecycleMutations(runtime);
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_WhenModeIsUnspecified_ShouldRejectBeforeDispatch()
    {
        var runtime = new RecordingActorRuntime();
        var preflight = new RejectingArtifactCompatibilityPreflight("CAPABILITY_ADMISSION_REBIND_REQUIRED");
        var port = CreatePort(runtime, artifactPreflight: preflight);

        var act = () => port.BindWorkflowDefinitionAsync(
            "definition-alpha",
            "name: direct\nroles: []\nsteps: []\n",
            "direct",
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: null,
            capabilityAdmissionPlan: null,
            workflowId: null,
            revisionId: null,
            expectedExecutionMode: ExternalCapabilityExecutionMode.Unspecified,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        AssertNoLifecycleMutations(runtime);
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionModeDiffers_ShouldRejectBeforePreflightOrMutation()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = CreateBoundDefinitionAgent(
            "name: direct\nroles: []\nsteps: []\n",
            CreateCapabilityAdmissionPlan("name: direct\nroles: []\nsteps: []\n"));
        runtime.StoredActors["definition-alpha"] = new RecordingActor("definition-alpha", definitionAgent);
        var preflight = new RecordingArtifactCompatibilityPreflight();
        var port = CreatePort(runtime, artifactPreflight: preflight);

        var act = () => port.CreateRunAsync(
            InteractiveBinding("definition-alpha") with
            {
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected execution mode*");
        preflight.Calls.Should().BeEmpty();
        AssertNoLifecycleMutations(runtime);
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionIsCompatible_ShouldPreflightAuthoritativeArtifactOnce()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var runtime = new RecordingActorRuntime();
        var authoritativePlan = CreateCapabilityAdmissionPlan(workflowYaml);
        var definitionAgent = CreateBoundDefinitionAgent(workflowYaml, authoritativePlan);
        runtime.StoredActors["definition-alpha"] = new RecordingActor("definition-alpha", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-alpha", new StubAgent("run-alpha")));
        var preflight = new RecordingArtifactCompatibilityPreflight(
            _ => runtime.CreateRequests.Should().BeEmpty());
        var port = CreatePort(runtime, artifactPreflight: preflight);

        await port.CreateRunAsync(
            InteractiveBinding("definition-alpha"),
            CancellationToken.None);

        var request = preflight.Calls.Should().ContainSingle().Subject;
        request.WorkflowYaml.Should().Be(workflowYaml);
        request.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        request.CapabilityAdmissionPlan!.AdmissionDigest.Should().Be(authoritativePlan.AdmissionDigest);
        runtime.CreateRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureDefinitionAsync_WithRealPort_ShouldBindDefinitionOnce()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = CreateWorkflowDefinitionAgent();
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-once", definitionAgent, forwardToAgent: true));
        var port = CreatePort(runtime);

        var receipt = await port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                "definition-once",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            "definition-once",
            CancellationToken.None);

        receipt.ActorId.Should().Be("definition-once");
        definitionAgent.State.Version.Should().Be(1);
        definitionAgent.State.WorkflowName.Should().Be("direct");
        definitionAgent.State.WorkflowYaml.Should().Be("name: direct\nroles: []\nsteps: []\n");
    }

    [Fact]
    public async Task EnsureDefinitionAsync_ShouldForwardPreferredActorId()
    {
        var runtime = new RecordingActorRuntime();
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-preferred", new WorkflowGAgent()));
        var port = CreatePort(runtime);

        var receipt = await port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                "definition-preferred",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            "definition-preferred",
            CancellationToken.None);

        receipt.ActorId.Should().Be("definition-preferred");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowGAgent), "definition-preferred"));
    }

    [Fact]
    public async Task EnsureDefinitionAsync_WhenExistingPayloadDiffers_ShouldRebindDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor(
            "definition-writer",
            new WorkflowGAgent
            {
                State =
                {
                    WorkflowName = "direct",
                    WorkflowYaml = "name: direct\nroles: []\nsteps:\n  - id: old\n    type: delay\n",
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                },
            });
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        var port = CreatePort(runtime);

        var receipt = await port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            definitionActor.Id,
            CancellationToken.None);

        receipt.ActorId.Should().Be(definitionActor.Id);
        receipt.CreatedNow.Should().BeFalse();
        definitionActor.LastHandledEnvelope.Should().NotBeNull();
        definitionActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionHasAdmissionPlan_ShouldReuseWithoutRebinding()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = workflowYaml;
        definitionAgent.State.CapabilityAdmissionPlan = CreateCapabilityAdmissionPlan(workflowYaml);
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        var definitionActor = new RecordingActor("definition-1", definitionAgent);
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-1", new StubAgent("run-1")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-1",
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-1");
        result.CreatedActorIds.Should().Equal("run-1");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Match<(Type AgentType, string? RequestedId)>(x =>
                x.AgentType == typeof(WorkflowRunGAgent) &&
                x.RequestedId != null &&
                x.RequestedId.StartsWith("definition-1:run:", StringComparison.Ordinal));
        runtime.Linked.Should().ContainSingle(x => x.ParentId == "definition-1" && x.ChildId == "run-1");
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task EnsureDefinitionAsync_WhenExistingExplicitIdentityDiffers_ShouldRejectWithoutRebinding()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var plan = CreateExplicitCapabilityAdmissionPlan("wf-alpha", "rev-alpha");
        var runtime = new RecordingActorRuntime();
        var definitionAgent = CreateBoundDefinitionAgent(
            workflowYaml,
            plan,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha");
        var definitionActor = new RecordingActor("definition-explicit", definitionAgent);
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        var port = CreatePort(runtime);

        var act = () => port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                CapabilityAdmissionPlan: plan,
                WorkflowId: "wf-beta",
                RevisionId: "rev-beta"),
            definitionActor.Id,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow revision identity*");
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingExplicitIdentityDiffers_ShouldRejectBeforeCreatingRun()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var plan = CreateExplicitCapabilityAdmissionPlan("wf-alpha", "rev-alpha");
        var runtime = new RecordingActorRuntime();
        var definitionAgent = CreateBoundDefinitionAgent(
            workflowYaml,
            plan,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha");
        var definitionActor = new RecordingActor("definition-explicit-run", definitionAgent);
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        runtime.ActorsToCreate.Enqueue(new RecordingActor("unexpected-run", new StubAgent("unexpected-run")));
        var port = CreatePort(runtime);

        var act = () => port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                CapabilityAdmissionPlan: plan,
                WorkflowId: "wf-beta",
                RevisionId: "rev-beta"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow revision identity*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureDefinitionAsync_WhenExplicitBindingIdentityIsMissing_ShouldRequireRebind()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var plan = CreateExplicitCapabilityAdmissionPlan("wf-alpha", "rev-alpha");
        var runtime = new RecordingActorRuntime();
        var definitionAgent = CreateBoundDefinitionAgent(workflowYaml, plan);
        var definitionActor = new RecordingActor("definition-legacy-explicit", definitionAgent);
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        var port = CreatePort(runtime);

        var act = () => port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                CapabilityAdmissionPlan: plan,
                WorkflowId: "wf-alpha",
                RevisionId: "rev-alpha"),
            definitionActor.Id,
            CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowCapabilityAdmissionRebindRequiredException>();
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task EnsureDefinitionAsync_WhenExistingExplicitIdentityMatches_ShouldReuseWithoutRebinding()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var plan = CreateExplicitCapabilityAdmissionPlan("wf-alpha", "rev-alpha");
        var runtime = new RecordingActorRuntime();
        var definitionAgent = CreateBoundDefinitionAgent(
            workflowYaml,
            plan,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha");
        var definitionActor = new RecordingActor("definition-explicit-same", definitionAgent);
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        var port = CreatePort(runtime);

        var receipt = await port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                CapabilityAdmissionPlan: plan,
                WorkflowId: "wf-alpha",
                RevisionId: "rev-alpha"),
            definitionActor.Id,
            CancellationToken.None);

        receipt.ActorId.Should().Be(definitionActor.Id);
        receipt.CreatedNow.Should().BeFalse();
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task EnsureDefinitionAsync_WhenExistingRevisionOnlyDiffers_ShouldRejectWithoutRebinding()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = workflowYaml;
        definitionAgent.State.RevisionId = "rev-alpha";
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        var definitionActor = new RecordingActor("definition-revision-only", definitionAgent);
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        var port = CreatePort(runtime);

        var act = () => port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                RevisionId: "rev-beta"),
            definitionActor.Id,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow revision identity*");
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task CreateRunAsync_ShouldPropagateScopeIdIntoRunBindingEvent()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        runtime.StoredActors["definition-scope"] = new RecordingActor("definition-scope", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-scope", new StubAgent("run-scope")));
        var port = CreatePort(runtime);

        await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-scope",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                "scope-user-1"),
            CancellationToken.None);

        var bindEvent = ((RecordingActor)runtime.StoredActors["run-scope"]).LastHandledEnvelope!.Payload!
            .Unpack<BindWorkflowRunDefinitionEvent>();
        bindEvent.ScopeId.Should().Be("scope-user-1");
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionHasNoPayload_ShouldFailWithoutBinding()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        var definitionActor = new RecordingActor("definition-2", definitionAgent);
        runtime.StoredActors["definition-2"] = definitionActor;
        var port = CreatePort(runtime);

        var act = () => port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-2",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not have a materialized definition payload*");
        runtime.CreateRequests.Should().BeEmpty();
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionYamlDiffers_ShouldFailWithoutRebinding()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps:\n  - id: old\n    type: delay\n";
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        runtime.StoredActors["definition-3"] = new RecordingActor("definition-3", definitionAgent);
        var port = CreatePort(runtime);

        var act = () => port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-3",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*payload does not match the requested Run definition*");
        runtime.CreateRequests.Should().BeEmpty();
        ((RecordingActor)runtime.StoredActors["definition-3"]).LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionWorkflowNameDiffers_ShouldFailFast()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "other";
        definitionAgent.State.WorkflowYaml = "name: other\nroles: []\nsteps: []\n";
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        runtime.StoredActors["definition-3"] = new RecordingActor("definition-3", definitionAgent);
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-3",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to workflow 'other'*cannot switch to 'direct'*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRunAsync_WhenRequestedDefinitionActorIsMissing_ShouldFailWithoutCreatingIt()
    {
        var runtime = new RecordingActorRuntime();
        runtime.ActorsToCreate.Enqueue(new RecordingActor("unexpected-definition", new WorkflowGAgent()));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("unexpected-run", new StubAgent("unexpected-run")));
        var port = CreatePort(runtime);

        var act = () => port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-missing",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*definition-missing*does not exist*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRunAsync_WhenRequestedDefinitionIdBelongsToUnsupportedActor_ShouldFailFast()
    {
        var runtime = new RecordingActorRuntime();
        runtime.StoredActors["definition-4"] = new RecordingActor("definition-4", new StubAgent("unsupported"));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-4b", new StubAgent("definition-4b")));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-4", new StubAgent("run-4")));
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-4",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a workflow definition actor*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRunAsync_WhenDefinitionBindingInvalid_ShouldThrow()
    {
        var runtime = new RecordingActorRuntime();
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-5",
                " ",
                " ",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid workflow definition binding*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRunAsync_WhenBindingReaderReturnsNullForExistingActor_ShouldFailWithoutSelfHealing()
    {
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("definition-missing-binding", new WorkflowGAgent());
        runtime.StoredActors["definition-missing-binding"] = definitionActor;
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                ["definition-missing-binding"] = null,
            }));

        var act = () => port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-missing-binding",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not have an available Definition binding read model*");
        runtime.CreateRequests.Should().BeEmpty();
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionSlotHoldsRunKind_ShouldFailWithoutSelfHealing()
    {
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("workflow-definition:studio", new WorkflowGAgent());
        runtime.StoredActors["workflow-definition:studio"] = definitionActor;
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                ["workflow-definition:studio"] = new(
                    WorkflowActorKind.Run,
                    "workflow-definition:studio",
                    "workflow-definition:studio",
                    "workflow-definition:studio:run:old",
                    "studio",
                    "name: studio\nroles: []\nsteps: []\n",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                    SourceVersion: 701),
            }));

        var act = () => port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "workflow-definition:studio",
                "studio",
                "name: studio\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a workflow definition actor*");
        runtime.CreateRequests.Should().BeEmpty();
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenEmptyOrMissingName_ShouldReturnInvalid()
    {
        var port = CreatePort(new RecordingActorRuntime());

        var empty = await port.ParseWorkflowYamlAsync(" ", CancellationToken.None);
        var missingName = await port.ParseWorkflowYamlAsync(
            """
            roles: []
            steps: []
            """,
            CancellationToken.None);

        empty.Succeeded.Should().BeFalse();
        empty.Error.Should().Contain("required");
        missingName.Succeeded.Should().BeFalse();
        missingName.Error.Should().Contain("name");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenStepTypeUnknown_ShouldReturnInvalid()
    {
        var port = CreatePort(new RecordingActorRuntime());

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            steps:
              - id: step1
                type: does_not_exist
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("does_not_exist");
    }

    [Theory]
    [InlineData("{\"query\":{}}", "", "", "exact connected service and operation")]
    [InlineData("{\"headers\":{\"Authorization\":\"forbidden\"}}", "us-home-alpha", "list-items", "sensitive header")]
    public async Task ParseWorkflowYamlAsync_WhenNyxIdCapabilityIsNotExact_ShouldReturnInvalid(
        string arguments,
        string userServiceId,
        string endpointId,
        string expectedError)
    {
        var port = CreatePort(new RecordingActorRuntime());

        var result = await port.ParseWorkflowYamlAsync(
            $$"""
            name: sample
            roles: []
            steps:
              - id: proxy
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: '{{userServiceId}}'
                    endpoint_id: '{{endpointId}}'
                parameters:
                  tool: nyxid_proxy
                  arguments: '{{arguments}}'
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(expectedError);
        result.AuthorizationDependencies.Should().BeNull();
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenRoleAgentKindIsDefaultPrimary_ShouldReturnSuccess()
    {
        var port = CreatePort(
            new RecordingActorRuntime(),
            agentKindRegistry: CreateRoleAgentKindRegistry());

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            roles:
              - id: assistant
                name: Assistant
                agent_kind: workflow.role-agent
            steps:
              - id: step1
                type: llm_call
                target_role: assistant
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.WorkflowName.Should().Be("sample");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenRoleAgentKindIsMissing_ShouldDefaultAndReturnSuccess()
    {
        var port = CreatePort(
            new RecordingActorRuntime(),
            agentKindRegistry: CreateRoleAgentKindRegistry());

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            roles:
              - id: assistant
                name: Assistant
            steps:
              - id: step1
                type: llm_call
                target_role: assistant
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.WorkflowName.Should().Be("sample");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenDefaultRoleImplementationIsNotLocallyRegistered_ShouldReturnSuccess()
    {
        var port = CreatePort(
            new RecordingActorRuntime(),
            agentKindRegistry: new AgentKindRegistry([]));

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            roles:
              - id: assistant
                name: Assistant
            steps:
              - id: step1
                type: llm_call
                target_role: assistant
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.WorkflowName.Should().Be("sample");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenRoleAgentKindIsUnknown_ShouldReturnActionableInvalidResult()
    {
        var port = CreatePort(
            new RecordingActorRuntime(),
            agentKindRegistry: CreateRoleAgentKindRegistry());

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            roles:
              - id: bridge
                name: Bridge
                agent_kind: workflow.missing-kind
            steps:
              - id: step1
                type: llm_call
                target_role: bridge
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("bridge");
        result.Error.Should().Contain("workflow.missing-kind");
        result.Error.Should().Contain(WorkflowRoleConventions.DefaultAgentKind);
    }

    [Fact]
    public async Task CreateRunAsync_WhenInlineDefinitionsDiffer_ShouldFailWithoutRebinding()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        definitionAgent.State.InlineWorkflowYamls["child"] = "name: child\nroles: []\nsteps: []\n";
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        runtime.StoredActors["definition-inline"] = new RecordingActor("definition-inline", definitionAgent);
        var port = CreatePort(runtime);

        var act = () => port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-inline",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["child"] = "name: child-updated\nroles: []\nsteps: []\n",
                },
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*payload does not match the requested Run definition*");
        runtime.CreateRequests.Should().BeEmpty();
        ((RecordingActor)runtime.StoredActors["definition-inline"]).LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task CreateRunAsync_WhenGlobalDefinitionIsUsedByDifferentScopes_ShouldNotMutateDefinition()
    {
        const string workflowYaml = "name: studio\nroles: []\nsteps: []\n";
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("workflow-definition:studio", new StubAgent("studio-definition"));
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        runtime.ActorsToCreate.Enqueue(new RecordingActor("studio-run-a", new StubAgent("studio-run-a")));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("studio-run-b", new StubAgent("studio-run-b")));
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                [definitionActor.Id] = new(
                    WorkflowActorKind.Definition,
                    definitionActor.Id,
                    definitionActor.Id,
                    string.Empty,
                    "studio",
                    workflowYaml,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                    ScopeId: string.Empty,
                    CapabilityAdmissionPlan: CreateCapabilityAdmissionPlan(workflowYaml)),
            }));

        var runA = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "studio",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-a"),
            CancellationToken.None);
        var runB = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "studio",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-b"),
            CancellationToken.None);

        runA.ActorId.Should().Be("studio-run-a");
        runB.ActorId.Should().Be("studio-run-b");
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task CreateRunAsync_WhenScopeOwnedDefinitionBelongsToAnotherScope_ShouldFailWithoutRebinding()
    {
        const string workflowYaml = "name: private\nroles: []\nsteps: []\n";
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("scope-definition", new StubAgent("scope-definition"));
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                [definitionActor.Id] = new(
                    WorkflowActorKind.Definition,
                    definitionActor.Id,
                    definitionActor.Id,
                    string.Empty,
                    "private",
                    workflowYaml,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                    ScopeId: "scope-a"),
            }));

        var act = () => port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "private",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-b"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to scope 'scope-a'*cannot switch to 'scope-b'*");
        runtime.CreateRequests.Should().BeEmpty();
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_ShouldValidateMissingActorIdInput()
    {
        var port = CreatePort(new RecordingActorRuntime());

        await FluentActions.Invoking(() => port.BindWorkflowDefinitionAsync(
                " ",
                "name: x",
                "x",
                inlineWorkflowYamls: null,
                scopeId: null,
                sourceKind: null,
                capabilityAdmissionPlan: null,
                workflowId: null,
                revisionId: null,
                ExternalCapabilityExecutionMode.Interactive,
                CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_ShouldDispatchEnvelopeWithInlineWorkflowMap()
    {
        var runtime = new RecordingActorRuntime();
        var actor = new RecordingActor("definition-inline-bind", new WorkflowGAgent());
        runtime.StoredActors[actor.Id] = actor;
        var port = CreatePort(runtime);
        var capabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: direct\nroles: []\nsteps: []\n",
            new Dictionary<string, string>
            {
                ["child"] = "name: child\nroles: []\nsteps: []\n",
            },
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);

        await port.BindWorkflowDefinitionAsync(
            actor.Id,
            "name: direct\nroles: []\nsteps: []\n",
            "direct",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["child"] = "name: child\nroles: []\nsteps: []\n",
            },
            scopeId: null,
            sourceKind: "service_revision",
            capabilityAdmissionPlan: capabilityAdmissionPlan,
            workflowId: "wf-direct-alpha",
            revisionId: "rev-direct-alpha",
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            ct: CancellationToken.None);

        actor.LastHandledEnvelope.Should().NotBeNull();
        actor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
        var bind = actor.LastHandledEnvelope.Payload.Unpack<BindWorkflowDefinitionEvent>();
        bind.WorkflowName.Should().Be("direct");
        bind.InlineWorkflowYamls.Should().ContainKey("child");
        bind.HasScopeId.Should().BeFalse();
        bind.SourceKind.Should().Be("service_revision");
        bind.CapabilityAdmissionPlan.AdmissionDigest.Should().Be(capabilityAdmissionPlan.AdmissionDigest);
        bind.WorkflowId.Should().Be("wf-direct-alpha");
        bind.RevisionId.Should().Be("rev-direct-alpha");
    }

    [Fact]
    public async Task CreateRunAsync_ShouldDispatchRunBindingWithoutProjectionActivationPorts()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        runtime.StoredActors["definition-projection"] = new RecordingActor("definition-projection", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-projection", new StubAgent("run-projection")));
        var port = CreatePort(runtime);

        await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-projection",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        ((RecordingActor)runtime.StoredActors["run-projection"]).LastHandledEnvelope.Should().NotBeNull();
        ((RecordingActor)runtime.StoredActors["run-projection"]).LastHandledEnvelope!.Payload!
            .Is(BindWorkflowRunDefinitionEvent.Descriptor)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CreateRunAsync_ShouldNotRollback_WhenProjectionActivationPortWouldHaveFailed()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        runtime.StoredActors["definition-projection-fail"] = new RecordingActor("definition-projection-fail", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-projection-fail", new StubAgent("run-projection-fail")));
        var port = CreatePort(runtime);

        await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-projection-fail",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        runtime.Destroyed.Should().BeEmpty();
        ((RecordingActor)runtime.StoredActors["run-projection-fail"]).LastHandledEnvelope.Should().NotBeNull();
    }

    [Fact]
    public async Task DestroyAsync_ShouldRejectBlankActorId()
    {
        var port = CreatePort(new RecordingActorRuntime());

        var act = async () => await port.DestroyAsync(" ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateRunAsync_WhenBindingReaderMarksProxyAsDefinition_ShouldReuseDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        runtime.StoredActors["definition-proxy"] = new RecordingActor("definition-proxy", new StubAgent("proxy"));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-proxy", new StubAgent("run-proxy")));
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                ["definition-proxy"] = new(
                    WorkflowActorKind.Definition,
                    "definition-proxy",
                    "definition-proxy",
                    string.Empty,
                    "direct",
                    "name: direct\nroles: []\nsteps: []\n",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            }));

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-proxy",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-proxy");
        result.CreatedActorIds.Should().Equal("run-proxy");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Match<(Type AgentType, string? RequestedId)>(x =>
                x.AgentType == typeof(WorkflowRunGAgent) &&
                x.RequestedId != null &&
                x.RequestedId.StartsWith("definition-proxy:run:", StringComparison.Ordinal));
        runtime.Linked.Should().ContainSingle(x => x.ParentId == "definition-proxy" && x.ChildId == "run-proxy");
    }

    [Fact]
    public async Task CreateRunAsync_WhenDefinitionBindFails_ShouldDestroyCreatedDefinitionActor()
    {
        var runtime = new RecordingActorRuntime
        {
            DispatchExceptionFactory = (actorId, envelope) =>
                actorId == "definition-fail" &&
                envelope.Payload?.Is(BindWorkflowDefinitionEvent.Descriptor) == true
                    ? new InvalidOperationException("definition bind failed")
                    : null,
        };
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-fail", new WorkflowGAgent()));
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                string.Empty,
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("definition bind failed");
        runtime.Destroyed.Should().Equal("definition-fail");
    }

    [Fact]
    public async Task CreateRunAsync_WhenRunBindFails_ShouldDestroyCreatedRunAndDefinitionActors()
    {
        var runtime = new RecordingActorRuntime
        {
            DispatchExceptionFactory = (actorId, envelope) =>
                actorId == "run-fail" &&
                envelope.Payload?.Is(BindWorkflowRunDefinitionEvent.Descriptor) == true
                    ? new InvalidOperationException("run bind failed")
                    : null,
        };
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-fail", new WorkflowGAgent()));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-fail", new StubAgent("run-fail")));
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                string.Empty,
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("run bind failed");
        runtime.Destroyed.Should().Equal("run-fail", "definition-fail");
    }

    [Fact]
    public async Task CreateRunAsync_WhenDefinitionActorIdIsEmpty_ShouldCreateIsolatedDefinition()
    {
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("definition-isolated", new WorkflowGAgent());
        runtime.ActorsToCreate.Enqueue(definitionActor);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-isolated", new StubAgent("run-isolated")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                string.Empty,
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                ScopeId: "scope-a"),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-isolated");
        result.CreatedActorIds.Should().Equal("definition-isolated", "run-isolated");
        definitionActor.LastHandledEnvelope.Should().NotBeNull();
        definitionActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureDefinitionAsync_WhenDefinitionCreateRaces_ShouldReuseWinnerAndContinue()
    {
        var runtime = new RecordingActorRuntime();
        var racedDefinitionAgent = new WorkflowGAgent();
        racedDefinitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        var racedDefinition = new RecordingActor("definition-race", racedDefinitionAgent);
        runtime.CreateExceptionFactory = (agentType, requestedId) =>
        {
            if (agentType == typeof(WorkflowGAgent) &&
                string.Equals(requestedId, "definition-race", StringComparison.Ordinal))
            {
                runtime.StoredActors["definition-race"] = racedDefinition;
                return new InvalidOperationException("Actor definition-race already exists");
            }

            return null;
        };
        var port = CreatePort(runtime);

        var result = await port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                "definition-race",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive),
            "definition-race",
            CancellationToken.None);

        result.ActorId.Should().Be("definition-race");
        result.CreatedNow.Should().BeFalse();
        runtime.CreateRequests.Should().Contain((typeof(WorkflowGAgent), "definition-race"));
        racedDefinition.LastHandledEnvelope.Should().NotBeNull();
        racedDefinition.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureDefinitionAsync_WhenDefinitionCreateRaceWinnerHasDifferentExplicitIdentity_ShouldReject()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var plan = CreateExplicitCapabilityAdmissionPlan("wf-alpha", "rev-alpha");
        var runtime = new RecordingActorRuntime();
        var racedDefinition = new RecordingActor(
            "definition-explicit-race",
            CreateBoundDefinitionAgent(
                workflowYaml,
                plan,
                workflowId: "wf-alpha",
                revisionId: "rev-alpha"));
        runtime.CreateExceptionFactory = (agentType, requestedId) =>
        {
            if (agentType == typeof(WorkflowGAgent) &&
                string.Equals(requestedId, racedDefinition.Id, StringComparison.Ordinal))
            {
                runtime.StoredActors[racedDefinition.Id] = racedDefinition;
                return new InvalidOperationException($"Actor {racedDefinition.Id} already exists");
            }

            return null;
        };
        var port = CreatePort(runtime);

        var act = () => port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                racedDefinition.Id,
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                CapabilityAdmissionPlan: plan,
                WorkflowId: "wf-beta",
                RevisionId: "rev-beta"),
            racedDefinition.Id,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow revision identity*");
        racedDefinition.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task EnsureRunAsync_ShouldUseExactRunIdentityAndIdempotentBindingCommand()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = workflowYaml;
        definitionAgent.State.CapabilityAdmissionPlan = CreateCapabilityAdmissionPlan(workflowYaml);
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        var definitionActor = new RecordingActor("definition-stable", definitionAgent);
        runtime.StoredActors[definitionActor.Id] = definitionActor;
        runtime.ActorsToCreate.Enqueue(new RecordingActor("work-order-run-1", new StubAgent("work-order-run-1")));
        var port = CreatePort(runtime);

        var result = await port.EnsureRunAsync(
            new WorkflowDefinitionBinding(
                "definition-stable",
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                "scope-1",
                WorkflowRunOrigins.WorkOrder),
            "work-order-run-1",
            CancellationToken.None);

        result.ActorId.Should().Be("work-order-run-1");
        runtime.CreateRequests.Should().Contain((typeof(WorkflowRunGAgent), "work-order-run-1"));
        var envelope = ((RecordingActor)runtime.StoredActors["work-order-run-1"]).LastHandledEnvelope;
        envelope.Should().NotBeNull();
        envelope!.Id.Should().Be("ensure-workflow-run-work-order-run-1");
        var ensure = envelope.Payload!.Unpack<EnsureWorkflowRunDefinitionEvent>();
        ensure.Binding.RunId.Should().Be("work-order-run-1");
        ensure.Binding.ScopeId.Should().Be("scope-1");
        ensure.Binding.RunOrigin.Should().Be(WorkflowRunOrigins.WorkOrder);
        definitionActor.LastHandledEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task EnsureRunAsync_ShouldNotMutateTopologyBeforeAcceptedBindingIsHandled()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        runtime.StoredActors["definition-stable"] = new RecordingActor("definition-stable", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("work-order-run-1", new StubAgent("work-order-run-1")));
        var acceptedOnlyDispatch = new AcceptedOnlyDispatchPort();
        var port = new WorkflowRunActorPort(
            runtime,
            acceptedOnlyDispatch,
            new RuntimeBackedWorkflowActorBindingReader(runtime),
            new AcceptingArtifactCompatibilityPreflight(),
            [new WorkflowCoreModulePack()]);

        await port.EnsureRunAsync(
            new WorkflowDefinitionBinding(
                "definition-stable",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                "scope-1",
                WorkflowRunOrigins.WorkOrder),
            "work-order-run-1",
            CancellationToken.None);

        runtime.Linked.Should().BeEmpty();
        acceptedOnlyDispatch.Envelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureRunAndDispatchAsync_ShouldSendOneCombinedExactRunCommand()
    {
        const string workflowYaml = "name: direct\nroles: []\nsteps: []\n";
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = workflowYaml;
        definitionAgent.State.CapabilityAdmissionPlan = CreateCapabilityAdmissionPlan(workflowYaml);
        definitionAgent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        runtime.StoredActors["definition-stable"] = new RecordingActor("definition-stable", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("work-order-run-1", new StubAgent("work-order-run-1")));
        var acceptedOnlyDispatch = new AcceptedOnlyDispatchPort();
        var port = new WorkflowRunActorPort(
            runtime,
            acceptedOnlyDispatch,
            new RuntimeBackedWorkflowActorBindingReader(runtime),
            new AcceptingArtifactCompatibilityPreflight(),
            [new WorkflowCoreModulePack()]);

        var result = await port.EnsureRunAndDispatchAsync(
            new WorkflowDefinitionBinding(
                "definition-stable",
                "direct",
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                "scope-1",
                WorkflowRunOrigins.WorkOrder),
            "work-order-run-1",
            new WorkflowChatRequestEvent { Prompt = "execute once" },
            "work-order-command-1",
            "work-order-correlation-1",
            CancellationToken.None);

        result.ActorId.Should().Be("work-order-run-1");
        var envelope = acceptedOnlyDispatch.Envelopes.Should().ContainSingle().Subject;
        envelope.Id.Should().Be("work-order-command-1");
        envelope.Propagation.CorrelationId.Should().Be("work-order-correlation-1");
        var command = envelope.Payload!.Unpack<EnsureWorkflowRunDefinitionEvent>();
        command.Binding.RunId.Should().Be("work-order-run-1");
        command.ExecutionRequest.Prompt.Should().Be("execute once");
        runtime.Linked.Should().BeEmpty();
    }

    private static WorkflowRunActorPort CreatePort(
        RecordingActorRuntime runtime,
        IWorkflowActorBindingReader? bindingReader = null,
        IAgentKindRegistry? agentKindRegistry = null,
        IWorkflowArtifactCompatibilityPreflight? artifactPreflight = null) =>
        new(
            runtime,
            runtime,
            bindingReader ?? new RuntimeBackedWorkflowActorBindingReader(runtime),
            artifactPreflight ?? new AcceptingArtifactCompatibilityPreflight(),
            [new WorkflowCoreModulePack()],
            agentKindRegistry);

    private static IAgentKindRegistry CreateRoleAgentKindRegistry() =>
        new AgentKindRegistry(
        [
            new AgentRegistration(
                Kind: "workflow.role-agent",
                ImplementationType: typeof(StubAgent),
                StateContractType: typeof(object)),
        ]);

    private static WorkflowCapabilityAdmissionPlan CreateCapabilityAdmissionPlan(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null) =>
        WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            inlineWorkflowYamls ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);

    private static WorkflowDefinitionBinding InteractiveBinding(string definitionActorId) =>
        new(
            definitionActorId,
            "direct",
            "name: direct\nroles: []\nsteps: []\n",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Interactive);

    private static void AssertNoLifecycleMutations(RecordingActorRuntime runtime)
    {
        runtime.CreateRequests.Should().BeEmpty();
        runtime.Linked.Should().BeEmpty();
        runtime.Destroyed.Should().BeEmpty();
        runtime.Dispatches.Should().BeEmpty();
    }

    private static WorkflowCapabilityAdmissionPlan CreateExplicitCapabilityAdmissionPlan(
        string workflowId,
        string revisionId)
    {
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            DefinitionDigest = "definition-digest-explicit",
            AdmissionDigest = "admission-digest-explicit",
        };
        plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "direct/request",
            NyxIdExplicitRequestGrant = new NyxIdExplicitRequestGrant
            {
                WorkflowId = workflowId,
                RevisionId = revisionId,
            },
        });
        return plan;
    }

    private static WorkflowGAgent CreateBoundDefinitionAgent(
        string workflowYaml,
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan,
        string workflowId = "",
        string revisionId = "")
    {
        var agent = new WorkflowGAgent();
        agent.State.WorkflowName = "direct";
        agent.State.WorkflowYaml = workflowYaml;
        agent.State.CapabilityAdmissionPlan = capabilityAdmissionPlan.Clone();
        agent.State.WorkflowId = workflowId;
        agent.State.RevisionId = revisionId;
        agent.State.ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        return agent;
    }

    private static WorkflowGAgent CreateWorkflowDefinitionAgent()
    {
        var eventStore = new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
                sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new WorkflowGAgent();
        agent.Services = services;
        agent.EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowState>>();
        return agent;
    }

    private static string ResolveRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "aevatar.slnx")))
                return current;

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        throw new InvalidOperationException("Could not resolve repository root.");
    }

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        private IActor? _lastCreatedActor;

        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);

        public Queue<IActor> ActorsToCreate { get; } = new();

        public List<(Type AgentType, string? RequestedId)> CreateRequests { get; } = [];

        public List<(string ParentId, string ChildId)> Linked { get; } = [];
        public List<string> Destroyed { get; } = [];
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];
        public Func<Type, string?, Exception?>? CreateExceptionFactory { get; set; }
        public Func<string, EventEnvelope, Exception?>? DispatchExceptionFactory { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateRequests.Add((agentType, id));
            var createException = CreateExceptionFactory?.Invoke(agentType, id);
            if (createException != null)
                throw createException;

            if (ActorsToCreate.Count > 0)
            {
                var createdActor = ActorsToCreate.Dequeue();
                StoredActors[createdActor.Id] = createdActor;
                _lastCreatedActor = createdActor;
                return Task.FromResult(createdActor);
            }

            var generatedActor = new RecordingActor(id ?? Guid.NewGuid().ToString("N"), new StubAgent("generated"));
            StoredActors[generatedActor.Id] = generatedActor;
            _lastCreatedActor = generatedActor;
            return Task.FromResult<IActor>(generatedActor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Destroyed.Add(id);
            StoredActors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(
                StoredActors.TryGetValue(id, out var actor)
                    ? actor
                    : _lastCreatedActor != null && string.Equals(_lastCreatedActor.Id, id, StringComparison.Ordinal)
                        ? _lastCreatedActor
                        : null);

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatches.Add((actorId, envelope.Clone()));
            var dispatchException = DispatchExceptionFactory?.Invoke(actorId, envelope);
            if (dispatchException != null)
                throw dispatchException;

            var actor = await GetAsync(actorId) ?? throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(StoredActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Linked.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptedOnlyDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Envelopes.Add(envelope.Clone());
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor : IActor
    {
        private readonly bool _forwardToAgent;

        public RecordingActor(string id, IAgent agent, bool forwardToAgent = false)
        {
            Id = id;
            Agent = agent;
            _forwardToAgent = forwardToAgent;
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public EventEnvelope? LastHandledEnvelope { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            LastHandledEnvelope = envelope;
            if (_forwardToAgent)
                await Agent.HandleEventAsync(envelope, ct);
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

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RuntimeBackedWorkflowActorBindingReader(RecordingActorRuntime runtime) : IWorkflowActorBindingReader
    {
        public async Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actor = await runtime.GetAsync(actorId);
            if (actor == null)
                return null;

            return actor.Agent switch
            {
                WorkflowGAgent definition => new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    actor.Id,
                    actor.Id,
                    string.Empty,
                    definition.State.WorkflowName,
                    definition.State.WorkflowYaml,
                    definition.State.InlineWorkflowYamls.ToDictionary(
                        static x => x.Key,
                        static x => x.Value,
                        StringComparer.OrdinalIgnoreCase),
                    definition.State.ExpectedExecutionMode,
                    ScopeId: definition.State.ScopeId,
                    SourceKind: definition.State.SourceKind,
                    CapabilityAdmissionPlan: definition.State.CapabilityAdmissionPlan?.Clone(),
                    WorkflowId: definition.State.WorkflowId,
                    RevisionId: definition.State.RevisionId),
                WorkflowRunGAgent run => new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    actor.Id,
                    run.State.DefinitionActorId,
                    run.State.RunId,
                    run.State.WorkflowName.Trim(),
                    run.State.WorkflowYaml,
                    run.State.InlineWorkflowYamls.ToDictionary(
                        static x => x.Key,
                        static x => x.Value,
                        StringComparer.OrdinalIgnoreCase),
                    run.State.ExpectedExecutionMode),
                _ => WorkflowActorBinding.Unsupported(actor.Id),
            };
        }
    }

    private sealed class AcceptingArtifactCompatibilityPreflight : IWorkflowArtifactCompatibilityPreflight
    {
        public Task ValidateAsync(
            WorkflowArtifactCompatibilityRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingArtifactCompatibilityPreflight(
        Action<WorkflowArtifactCompatibilityRequest>? onValidate = null)
        : IWorkflowArtifactCompatibilityPreflight
    {
        public List<WorkflowArtifactCompatibilityRequest> Calls { get; } = [];

        public Task ValidateAsync(
            WorkflowArtifactCompatibilityRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            Calls.Add(request with { CapabilityAdmissionPlan = request.CapabilityAdmissionPlan?.Clone() });
            onValidate?.Invoke(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingArtifactCompatibilityPreflight(string code)
        : IWorkflowArtifactCompatibilityPreflight
    {
        public Task ValidateAsync(
            WorkflowArtifactCompatibilityRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            throw new WorkflowExternalCapabilityAdmissionException(new ExternalCapabilityReadiness
            {
                Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
                Blockers =
                {
                    new ExternalCapabilityBlocker
                    {
                        Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
                        Code = code,
                        SafeMessage = "Workflow admission was rejected.",
                    },
                },
            });
        }
    }

    private sealed class StaticWorkflowActorBindingReader(IReadOnlyDictionary<string, WorkflowActorBinding?> mappings) : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            mappings.TryGetValue(actorId, out var binding);
            return Task.FromResult(binding);
        }
    }

    private sealed class FakeStepExecutorFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            _ = name;
            module = null;
            return false;
        }
    }
}
