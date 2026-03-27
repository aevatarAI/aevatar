using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Infrastructure.Activation;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;
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
        runtime.CreateCalls.Should().ContainSingle(x => x.actorId == "static:r2:deployment-actor:r2");
    }

    [Fact]
    public async Task ActivateAsync_ShouldReuseExistingWorkflowDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting("workflow-definition-1:deployment-actor:r1");
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
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    DefinitionActorId = "workflow-definition-1",
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
        workflowPort.BindCalls.Should().ContainSingle();
        workflowPort.CreateDefinitionCalls.Should().BeEmpty();
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
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    DefinitionActorId = string.Empty,
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
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = "r1",
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    DefinitionActorId = "workflow-definition-1",
                    InlineWorkflowYamls =
                    {
                        ["child"] = "name: child",
                    },
                },
            },
        };

        await activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r1",
                "deployment-actor"));

        workflowPort.BindCalls.Should().ContainSingle();
        workflowPort.BindCalls[0].inlineWorkflowYamls.Should().ContainKey("child");
        workflowPort.BindCalls[0].inlineWorkflowYamls["child"].Should().Be("name: child");
    }

    [Fact]
    public async Task ActivateAsync_ShouldRejectMissingWorkflowDefinitionActor_WhenRuntimeClaimsItExists()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExistsWithoutActor("workflow-definition-1:deployment-actor:r1");
        var activator = new DefaultServiceRuntimeActivator(
            runtime,
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            new RecordingWorkflowRunActorPort());
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RevisionId = "r1",
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = "workflow",
                    WorkflowYaml = "name: workflow",
                    DefinitionActorId = "workflow-definition-1",
                },
            },
        };

        var act = () => activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r1",
                "deployment-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found*");
    }

    [Fact]
    public async Task ActivateAsync_ShouldThrow_WhenStaticActorTypeCannotBeResolved()
    {
        var activator = new DefaultServiceRuntimeActivator(
            new RecordingActorRuntime(),
            new RecordingScriptDefinitionSnapshotPort(),
            new RecordingScriptRuntimeProvisioningPort(),
            new RecordingWorkflowRunActorPort());
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(revisionId: "r2");
        artifact.DeploymentPlan.StaticPlan.ActorTypeName = "Missing.StaticActor, Missing.Assembly";

        var act = () => activator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                GAgentServiceTestKit.CreateIdentity(),
                artifact,
                "r2",
                "deployment-actor"));

        await act.Should().ThrowAsync<Exception>()
            .Where(ex => ex is FileNotFoundException &&
                         ex.Message.Contains("Missing.Assembly", StringComparison.Ordinal));
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

    private sealed class RecordingWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public List<string?> CreateDefinitionCalls { get; } = [];
        public List<(string actorId, string workflowName, string workflowYaml, IReadOnlyDictionary<string, string> inlineWorkflowYamls)> BindCalls { get; } = [];

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default)
        {
            CreateDefinitionCalls.Add(actorId);
            return Task.FromResult<IActor>(new RecordingActor(actorId ?? "created-definition"));
        }

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkStoppedAsync(string actorId, string runId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? scopeId = null,
            CancellationToken ct = default)
        {
            BindCalls.Add((actor.Id, workflowName, workflowYaml, inlineWorkflowYamls?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal)));
            return Task.CompletedTask;
        }

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            Task.FromResult(WorkflowYamlParseResult.Success("workflow"));
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
