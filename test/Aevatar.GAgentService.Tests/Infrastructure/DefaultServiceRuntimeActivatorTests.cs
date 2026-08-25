using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Infrastructure.Activation;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class DefaultServiceRuntimeActivatorTests
{
    [Fact]
    public async Task ActivateAsync_ShouldCreateStaticActor_WhenMissing()
    {
        var runtime = new RecordingActorRuntime();
        var activator = new DefaultServiceRuntimeActivator(
            runtime,
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            new RecordingWorkflowRunActorPort());
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(revisionId: "r2");

        var result = await activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r2",
                "deployment-actor"));

        result.DeploymentId.Should().Be("deployment-actor:r2");
        result.PrimaryActorId.Should().Be("static:r2:deployment-actor:r2");
        runtime.CreateByKindCalls.Should().ContainSingle(x =>
            x.agentKind == GAgentServiceTestKit.TestStaticServiceAgentKind &&
            x.actorId == "static:r2:deployment-actor:r2");
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldReuseExistingWorkflowDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting("workflow-definition-1:deployment-actor:r1");
        var workflowPort = new RecordingWorkflowRunActorPort();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var activator = new DefaultServiceRuntimeActivator(
            runtime,
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            workflowPort);
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = "r1",
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    DefinitionActorId = "workflow-definition-1",
                    CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                    {
                        ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    },
                },
            },
        };

        var result = await activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                identity,
                artifact,
                "r1",
                "deployment-actor"));

        result.PrimaryActorId.Should().Be("workflow-definition-1:deployment-actor:r1");
        workflowPort.BindCalls.Should().ContainSingle();
        workflowPort.ExplicitBindCalls.Should().BeEmpty();
        workflowPort.CreateDefinitionCalls.Should().ContainSingle("workflow-definition-1:deployment-actor:r1");
        workflowPort.DefinitionBindings.Should().ContainSingle()
            .Which.ScopeId.Should().Be(identity.TenantId);
    }

    [Fact]
    public async Task ActivateAsync_ShouldProvisionScriptingRuntime()
    {
        var runtimePort = new RecordingScriptRuntimeProvisioningPort
        {
            RuntimeActorId = "script-runtime-1",
        };
        var activator = new DefaultServiceRuntimeActivator(
            new RecordingActorRuntime(),
            new RecordingScriptDefinitionSnapshotPort(),
            runtimePort,
            new RecordingWorkflowRunActorPort());
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = "r1",
            ImplementationKind = ServiceImplementationKind.Scripting,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    ScriptId = "script-1",
                    Revision = "script-r1",
                    DefinitionActorId = "definition-1",
                },
            },
        };

        var result = await activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r1",
                "deployment-actor"));

        result.PrimaryActorId.Should().Be("script-runtime-1");
        runtimePort.Calls.Should().ContainSingle();
        runtimePort.Calls[0].definitionActorId.Should().Be("definition-1");
        runtimePort.Calls[0].revision.Should().Be("script-r1");
        runtimePort.Calls[0].runtimeActorId.Should().Be("gagent-service:script-runtime:deployment-actor:r1");
        runtimePort.Calls[0].scopeId.Should().Be(GAgentServiceTestKit.CreateIdentity().TenantId);
    }

    [Fact]
    public async Task ActivateAsync_ShouldRejectUnsupportedDeploymentPlan()
    {
        var activator = new DefaultServiceRuntimeActivator(
            new RecordingActorRuntime(),
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            new RecordingWorkflowRunActorPort());

        var act = () => activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                new PreparedServiceRevisionArtifact
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(),
                    RevisionId = "r1",
                    DeploymentPlan = new ServiceDeploymentPlan(),
                },
                "r1",
                "deployment-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unsupported deployment plan.");
    }

    [Fact]
    public async Task ActivateAsync_ShouldReuseExistingStaticActor_AndHonorDefaultActorId()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting("gagent-service:static-runtime:deployment-actor:r2");
        var activator = new DefaultServiceRuntimeActivator(
            runtime,
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            new RecordingWorkflowRunActorPort());
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(revisionId: "r2");
        artifact.DeploymentPlan.StaticPlan.PreferredActorId = string.Empty;

        var result = await activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r2",
                "deployment-actor"));

        result.PrimaryActorId.Should().Be("gagent-service:static-runtime:deployment-actor:r2");
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldCreateWorkflowDefinitionActor_WhenMissing()
    {
        var runtime = new RecordingActorRuntime();
        var workflowPort = new RecordingWorkflowRunActorPort();
        var activator = new DefaultServiceRuntimeActivator(
            runtime,
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            workflowPort);
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = "r1",
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    DefinitionActorId = string.Empty,
                    CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                    {
                        ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    },
                },
            },
        };

        var result = await activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r1",
                "deployment-actor"));

        result.PrimaryActorId.Should().Be("gagent-service:workflow-definition:deployment-actor:r1");
        workflowPort.CreateDefinitionCalls.Should().ContainSingle("gagent-service:workflow-definition:deployment-actor:r1");
        workflowPort.BindCalls.Should().ContainSingle();
        workflowPort.DefinitionBindings.Should().ContainSingle()
            .Which.RevisionId.Should().Be("r1");
        workflowPort.ExplicitBindCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldRejectWorkflowArtifactRevisionMismatch()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var activator = new DefaultServiceRuntimeActivator(
            new RecordingActorRuntime(),
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            workflowPort);
        var artifact = CreateExplicitWorkflowArtifact(
            artifactRevisionId: "rev-artifact-alpha",
            planRevisionId: "rev-artifact-alpha");

        var act = () => activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "rev-request-beta",
                "deployment-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact revision_id*");
        workflowPort.DefinitionBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldRejectWorkflowPlanRevisionMismatch()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var activator = new DefaultServiceRuntimeActivator(
            new RecordingActorRuntime(),
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            workflowPort);
        var artifact = CreateExplicitWorkflowArtifact(
            artifactRevisionId: "rev-request-alpha",
            planRevisionId: "rev-plan-beta");

        var act = () => activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "rev-request-alpha",
                "deployment-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow plan revision_id*");
        workflowPort.DefinitionBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldPassInlineWorkflowYamlsToWorkflowBinding()
    {
        var runtime = new RecordingActorRuntime();
        var workflowPort = new RecordingWorkflowRunActorPort();
        var activator = new DefaultServiceRuntimeActivator(
            runtime,
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            workflowPort);
        var capabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: workflow",
            new Dictionary<string, string> { ["child"] = "name: child" },
            ExternalCapabilityExecutionMode.Durable,
            [],
            []);
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = "rev-activation-alpha",
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    DefinitionActorId = "workflow-definition-1",
                    WorkflowId = "wf-activation-alpha",
                    RevisionId = "rev-activation-alpha",
                    InlineWorkflowYamls =
                    {
                        ["child"] = "name: child",
                    },
                    CapabilityAdmissionPlan = capabilityAdmissionPlan,
                },
            },
        };

        await activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "rev-activation-alpha",
                "deployment-actor"));

        workflowPort.BindCalls.Should().ContainSingle();
        workflowPort.BindCalls[0].inlineWorkflowYamls.Should().ContainKey("child");
        workflowPort.BindCalls[0].inlineWorkflowYamls["child"].Should().Be("name: child");
        workflowPort.DefinitionBindings.Should().ContainSingle();
        workflowPort.DefinitionBindings[0].CapabilityAdmissionPlan!.AdmissionDigest.Should()
            .Be(capabilityAdmissionPlan.AdmissionDigest);
        workflowPort.DefinitionBindings[0].WorkflowId.Should().Be("wf-activation-alpha");
        workflowPort.DefinitionBindings[0].RevisionId.Should().Be("rev-activation-alpha");
        workflowPort.ExplicitBindCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldUseWorkflowProvisioning_WhenRuntimeClaimsDefinitionExists()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExistsWithoutActor("workflow-definition-1:deployment-actor:r1");
        var workflowPort = new RecordingWorkflowRunActorPort();
        var activator = new DefaultServiceRuntimeActivator(
            runtime,
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            workflowPort);
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = "r1",
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    DefinitionActorId = "workflow-definition-1",
                    CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                    {
                        ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    },
                },
            },
        };

        var result = await activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r1",
                "deployment-actor"));

        result.PrimaryActorId.Should().Be("workflow-definition-1:deployment-actor:r1");
        workflowPort.CreateDefinitionCalls.Should().ContainSingle("workflow-definition-1:deployment-actor:r1");
        workflowPort.BindCalls.Should().ContainSingle();
        workflowPort.ExplicitBindCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_AfterWorkflowBindCommitFailure_ShouldConvergeAcrossFreshRetries()
    {
        const string revisionId = "rev-durable-retry";
        const string workflowId = "wf-durable-retry";
        const string workflowYaml = "name: workflow\nroles: []\nsteps: []\n";
        const string definitionActorId =
            "workflow-definition-retry:deployment-actor:rev-durable-retry";
        var eventStore = new InMemoryEventStore();
        var workflowPort = new RehydratingWorkflowDefinitionProvisioningPort(
            eventStore,
            throwAfterFirstCommit: true);
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ExternalCapabilityExecutionMode.Durable,
            [],
            [],
            workflowId: workflowId,
            revisionId: revisionId);
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "workflow",
                    WorkflowYaml = workflowYaml,
                    ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    DefinitionActorId = "workflow-definition-retry",
                    WorkflowId = workflowId,
                    RevisionId = revisionId,
                    CapabilityAdmissionPlan = admissionPlan,
                },
            },
        };
        var request = new ServiceRuntimeActivationRequest(
            GAgentServiceTestKit.CreateIdentity(),
            artifact,
            revisionId,
            "deployment-actor",
            ActivationAttemptId: "attempt-durable-retry",
            ActivationOperationId: "operation-durable-retry");

        DefaultServiceRuntimeActivator CreateFreshActivator() =>
            new(
                new RecordingActorRuntime(),
                new RecordingScriptDefinitionSnapshotPort(),
                new RecordingScriptRuntimeProvisioningPort(),
                workflowPort);

        await FluentActions.Awaiting(() => CreateFreshActivator().ActivateAsync(request))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflow bind committed before simulated caller failure");

        var retryResults = await Task.WhenAll(
            CreateFreshActivator().ActivateAsync(request),
            CreateFreshActivator().ActivateAsync(request));

        retryResults.Should().OnlyContain(result =>
            result.DeploymentId == "deployment-actor:rev-durable-retry" &&
            result.PrimaryActorId == definitionActorId &&
            result.Status == "active");
        retryResults[0].Should().Be(retryResults[1]);
        workflowPort.RehydrationCount.Should().Be(3);
        (await eventStore.GetEventsAsync(definitionActorId))
            .Should().ContainSingle(evt => evt.EventData.Is(BindWorkflowDefinitionEvent.Descriptor));
        (await eventStore.GetVersionAsync(definitionActorId)).Should().Be(1);
    }

    [Fact]
    public async Task ActivateAsync_ShouldThrow_WhenStaticAgentKindIsMissing()
    {
        var activator = new DefaultServiceRuntimeActivator(
            new RecordingActorRuntime(),
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            new RecordingWorkflowRunActorPort());
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(revisionId: "r2");
        artifact.DeploymentPlan.StaticPlan.AgentKind = string.Empty;

        var act = () => activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r2",
                "deployment-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Static agent_kind is required.");
    }

    [Fact]
    public async Task DeactivateAsync_ShouldDestroyExistingActor_AndIgnoreMissingOrBlankIds()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting("actor-1");
        var activator = new DefaultServiceRuntimeActivator(
            runtime,
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            new RecordingWorkflowRunActorPort());

        await activator.DeactivateAsync(new ServiceRuntimeDeactivationRequest(
            GAgentServiceTestKit.CreateIdentity(),
            "dep-1",
            "r1",
            "actor-1"));
        await activator.DeactivateAsync(new ServiceRuntimeDeactivationRequest(
            GAgentServiceTestKit.CreateIdentity(),
            "dep-2",
            "r2",
            "missing-actor"));
        await activator.DeactivateAsync(new ServiceRuntimeDeactivationRequest(
            GAgentServiceTestKit.CreateIdentity(),
            "dep-3",
            "r3",
            string.Empty));

        runtime.DestroyCalls.Should().ContainSingle("actor-1");
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _existingWithoutActor = new(StringComparer.Ordinal);

        public List<(Type actorType, string actorId)> CreateCalls { get; } = [];
        public List<(string agentKind, string actorId)> CreateByKindCalls { get; } = [];
        public List<string> DestroyCalls { get; } = [];

        public void MarkExisting(string actorId)
        {
            _actors[actorId] = new RecordingActor(actorId);
        }

        public void MarkExistsWithoutActor(string actorId)
        {
            _existingWithoutActor.Add(actorId);
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"created:{agentType.Name}";
            CreateCalls.Add((agentType, actorId));
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"created:{agentKind}";
            CreateByKindCalls.Add((agentKind, actorId));
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyCalls.Add(id);
            _actors.Remove(id);
            _existingWithoutActor.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id) || _existingWithoutActor.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingScriptRuntimeProvisioningPort : IScriptRuntimeProvisioningPort
    {
        public string RuntimeActorId { get; init; } = "script-runtime";

        public List<(string definitionActorId, string revision, string? runtimeActorId, ScriptDefinitionSnapshot definitionSnapshot, string? scopeId)> Calls { get; } = [];

        public Task<string> EnsureRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            ScriptDefinitionSnapshot definitionSnapshot,
            CancellationToken ct)
        {
            Calls.Add((definitionActorId, scriptRevision, runtimeActorId, definitionSnapshot, null));
            return Task.FromResult(RuntimeActorId);
        }

        public Task<string> EnsureRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            ScriptDefinitionSnapshot definitionSnapshot,
            string? scopeId,
            CancellationToken ct)
        {
            Calls.Add((definitionActorId, scriptRevision, runtimeActorId, definitionSnapshot, scopeId));
            return Task.FromResult(RuntimeActorId);
        }
    }

    private sealed class RecordingScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        private readonly ScriptDefinitionSnapshot _snapshot;

        public RecordingScriptDefinitionSnapshotPort(
            ScriptDefinitionSnapshot? snapshot = null)
        {
            _snapshot = snapshot ?? new ScriptDefinitionSnapshot(
                ScriptId: "script-1",
                Revision: "script-r1",
                SourceText: "// source",
                SourceHash: "hash-1",
                StateTypeUrl: "type.googleapis.com/test.State",
                ReadModelTypeUrl: "type.googleapis.com/test.ReadModel",
                ReadModelSchemaVersion: "1",
                ReadModelSchemaHash: "rm-hash");
        }

        public List<(string definitionActorId, string requestedRevision)> Calls { get; } = [];

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            Calls.Add((definitionActorId, requestedRevision));
            return Task.FromResult(_snapshot.Clone());
        }
    }

    private sealed class RecordingWorkflowRunActorPort : IWorkflowDefinitionProvisioningPort, IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public List<string?> CreateDefinitionCalls { get; } = [];
        public List<(string actorId, string workflowName, string workflowYaml, IReadOnlyDictionary<string, string> inlineWorkflowYamls)> BindCalls { get; } = [];
        public List<(string actorId, string workflowName, string workflowYaml, IReadOnlyDictionary<string, string> inlineWorkflowYamls)> ExplicitBindCalls { get; } = [];
        public List<WorkflowDefinitionBinding> DefinitionBindings { get; } = [];

        public Task<WorkflowDefinitionProvisioningReceipt> EnsureDefinitionAsync(
            WorkflowDefinitionBinding definition,
            string? preferredActorId = null,
            CancellationToken ct = default)
        {
            DefinitionBindings.Add(definition);
            CreateDefinitionCalls.Add(preferredActorId);
            RecordBind(
                preferredActorId ?? definition.DefinitionActorId,
                definition.WorkflowYaml,
                definition.WorkflowName,
                definition.InlineWorkflowYamls,
                BindCalls);
            return Task.FromResult(new WorkflowDefinitionProvisioningReceipt(
                preferredActorId ?? definition.DefinitionActorId,
                CreatedNow: true));
        }

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkStoppedAsync(string actorId, string runId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task BindWorkflowDefinitionAsync(
            string actorId,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
            string? scopeId,
            string? sourceKind,
            WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
            string? workflowId,
            string? revisionId,
            ExternalCapabilityExecutionMode expectedExecutionMode,
            CancellationToken ct = default)
        {
            RecordBind(actorId, workflowYaml, workflowName, inlineWorkflowYamls, ExplicitBindCalls);
            return Task.CompletedTask;
        }

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            Task.FromResult(WorkflowYamlParseResult.Success("workflow"));

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            Task.FromResult(WorkflowInlineYamlBundleParseResult.Success(
                "workflow",
                inlineWorkflowDocuments.FirstOrDefault()?.Yaml ?? string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["workflow"] = inlineWorkflowDocuments.FirstOrDefault()?.Yaml ?? string.Empty,
                }));

        private static void RecordBind(
            string actorId,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
            List<(string actorId, string workflowName, string workflowYaml, IReadOnlyDictionary<string, string> inlineWorkflowYamls)> target)
        {
            target.Add((
                actorId,
                workflowName,
                workflowYaml,
                inlineWorkflowYamls?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        }
    }

    private sealed class RehydratingWorkflowDefinitionProvisioningPort(
        InMemoryEventStore eventStore,
        bool throwAfterFirstCommit) : IWorkflowDefinitionProvisioningPort
    {
        private readonly SemaphoreSlim _actorInbox = new(1, 1);
        private int _throwAfterCommit = throwAfterFirstCommit ? 1 : 0;

        public int RehydrationCount { get; private set; }

        public async Task<WorkflowDefinitionProvisioningReceipt> EnsureDefinitionAsync(
            WorkflowDefinitionBinding definition,
            string? preferredActorId = null,
            CancellationToken ct = default)
        {
            var actorId = preferredActorId ?? definition.DefinitionActorId;
            await _actorInbox.WaitAsync(ct);
            try
            {
                var versionBefore = await eventStore.GetVersionAsync(actorId, ct);
                var agent = GAgentServiceTestKit.CreateStatefulAgent<WorkflowGAgent, WorkflowState>(
                    eventStore,
                    actorId,
                    static () => new WorkflowGAgent());
                RehydrationCount++;
                await agent.ActivateAsync(ct);
                await agent.BindWorkflowDefinitionAsync(
                    definition.WorkflowYaml,
                    definition.WorkflowName,
                    definition.InlineWorkflowYamls,
                    definition.ScopeId,
                    definition.SourceKind,
                    definition.CapabilityAdmissionPlan,
                    definition.WorkflowId,
                    definition.RevisionId,
                    definition.ExpectedExecutionMode,
                    ct);

                if (Interlocked.Exchange(ref _throwAfterCommit, 0) == 1)
                {
                    throw new InvalidOperationException(
                        "workflow bind committed before simulated caller failure");
                }

                return new WorkflowDefinitionProvisioningReceipt(
                    actorId,
                    CreatedNow: versionBefore == 0);
            }
            finally
            {
                _actorInbox.Release();
            }
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async Task BindWorkflowDefinitionAsync(
            string actorId,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
            string? scopeId,
            string? sourceKind,
            WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
            string? workflowId,
            string? revisionId,
            ExternalCapabilityExecutionMode expectedExecutionMode,
            CancellationToken ct = default)
        {
            await EnsureDefinitionAsync(
                new WorkflowDefinitionBinding(
                    actorId,
                    workflowName,
                    workflowYaml,
                    inlineWorkflowYamls ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    expectedExecutionMode,
                    ScopeId: scopeId ?? string.Empty,
                    SourceKind: sourceKind ?? string.Empty,
                    CapabilityAdmissionPlan: capabilityAdmissionPlan,
                    WorkflowId: workflowId ?? string.Empty,
                    RevisionId: revisionId ?? string.Empty),
                actorId,
                ct);
        }
    }

    private static PreparedServiceRevisionArtifact CreateExplicitWorkflowArtifact(
        string artifactRevisionId,
        string planRevisionId)
    {
        var admissionPlan = new WorkflowCapabilityAdmissionPlan();
        admissionPlan.ExecutionMode = ExternalCapabilityExecutionMode.Durable;
        admissionPlan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "workflow/request-alpha",
            NyxIdExplicitRequestGrant = new NyxIdExplicitRequestGrant(),
        });
        return new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = artifactRevisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    WorkflowId = "wf-activation-alpha",
                    RevisionId = planRevisionId,
                    CapabilityAdmissionPlan = admissionPlan,
                },
            },
        };
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent { get; } = new TestStaticServiceAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
