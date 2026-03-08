using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunActorPortBranchTests
{
    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionMatches_ShouldReuseDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-1"] = new RecordingActor("definition-1", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-1", new StubAgent("run-1")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-1",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-1");
        result.CreatedActorIds.Should().Equal("run-1");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowRunGAgent), (string?)null));
        runtime.Linked.Should().ContainSingle(x => x.ParentId == "definition-1" && x.ChildId == "run-1");
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionActorIsUnbound_ShouldBindItInPlace()
    {
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("definition-2", new WorkflowGAgent());
        runtime.StoredActors["definition-2"] = definitionActor;
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-2", new StubAgent("run-2")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-2",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-2");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowRunGAgent), (string?)null));
        definitionActor.LastHandledEnvelope.Should().NotBeNull();
        definitionActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionDiffers_ShouldCreateFreshDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "other";
        definitionAgent.State.WorkflowYaml = "name: other\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-3"] = new RecordingActor("definition-3", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-new", new StubAgent("definition-new")));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-3", new StubAgent("run-3")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-3",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-new");
        result.CreatedActorIds.Should().Equal("definition-new", "run-3");
        runtime.CreateRequests.Should().ContainInOrder(
            (typeof(WorkflowGAgent), (string?)null),
            (typeof(WorkflowRunGAgent), (string?)null));
    }

    [Fact]
    public async Task CreateRunAsync_WhenRequestedDefinitionIdBelongsToUnsupportedActor_ShouldCreateFreshDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        runtime.StoredActors["definition-4"] = new RecordingActor("definition-4", new StubAgent("unsupported"));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-4b", new StubAgent("definition-4b")));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-4", new StubAgent("run-4")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-4",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-4b");
        runtime.CreateRequests.Should().ContainInOrder(
            (typeof(WorkflowGAgent), (string?)null),
            (typeof(WorkflowRunGAgent), (string?)null));
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
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid workflow definition binding*");
        runtime.CreateRequests.Should().BeEmpty();
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
    public async Task CreateRunAsync_WhenInlineDefinitionsDiffer_ShouldCreateFreshDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        definitionAgent.State.InlineWorkflowYamls["child"] = "name: child\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-inline"] = new RecordingActor("definition-inline", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-inline-new", new StubAgent("definition-inline-new")));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-inline", new StubAgent("run-inline")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-inline",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["child"] = "name: child-updated\nroles: []\nsteps: []\n",
                }),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-inline-new");
        runtime.CreateRequests.Should().ContainInOrder(
            (typeof(WorkflowGAgent), (string?)null),
            (typeof(WorkflowRunGAgent), (string?)null));
    }

    [Fact]
    public async Task ActorApis_ShouldValidateNullActorInputs()
    {
        var port = CreatePort(new RecordingActorRuntime());

        await FluentActions.Invoking(() => port.DescribeAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => port.IsWorkflowDefinitionActorAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => port.IsWorkflowRunActorAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => port.GetBoundWorkflowNameAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => port.BindWorkflowDefinitionAsync(null!, "name: x", "x", null, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DescribeAsync_AndGetBoundWorkflowName_ShouldHandleRunActorsAndTrimWhitespace()
    {
        var runtime = new RecordingActorRuntime();
        var runAgent = new WorkflowRunGAgent(
            runtime,
            new FakeRoleAgentTypeResolver(),
            new FakeStepExecutorFactory(),
            [new WorkflowCoreModulePack()]);
        runAgent.State.WorkflowName = " direct ";
        runAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runAgent.State.DefinitionActorId = "definition-9";
        runAgent.State.RunId = "run-9";
        var actor = new RecordingActor("run-actor-9", runAgent);
        var port = CreatePort(runtime);

        var binding = await port.DescribeAsync(actor, CancellationToken.None);
        var workflowName = await port.GetBoundWorkflowNameAsync(actor, CancellationToken.None);

        binding.ActorKind.Should().Be(WorkflowActorKind.Run);
        binding.DefinitionActorId.Should().Be("definition-9");
        binding.RunId.Should().Be("run-9");
        workflowName.Should().Be("direct");
    }

    private static WorkflowRunActorPort CreatePort(RecordingActorRuntime runtime)
    {
        var verifier = new DefaultAgentTypeVerifier(new RuntimeBackedActorTypeProbe(runtime));
        return new WorkflowRunActorPort(runtime, verifier, [new WorkflowCoreModulePack()]);
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);

        public Queue<IActor> ActorsToCreate { get; } = new();

        public List<(Type AgentType, string? RequestedId)> CreateRequests { get; } = [];

        public List<(string ParentId, string ChildId)> Linked { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateRequests.Add((agentType, id));
            if (ActorsToCreate.Count > 0)
                return Task.FromResult(ActorsToCreate.Dequeue());

            return Task.FromResult<IActor>(new RecordingActor(id ?? Guid.NewGuid().ToString("N"), new StubAgent("generated")));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StoredActors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(StoredActors.TryGetValue(id, out var actor) ? actor : null);

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

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id, IAgent agent)
        {
            Id = id;
            Agent = agent;
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public EventEnvelope? LastHandledEnvelope { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
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

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RuntimeBackedActorTypeProbe(IActorRuntime runtime) : IActorTypeProbe
    {
        public async Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actor = await runtime.GetAsync(actorId);
            return actor?.Agent.GetType().FullName;
        }
    }

    private sealed class FakeRoleAgentTypeResolver : IRoleAgentTypeResolver
    {
        public Type ResolveRoleAgentType() => typeof(StubAgent);
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
