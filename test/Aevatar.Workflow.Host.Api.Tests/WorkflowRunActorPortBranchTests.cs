using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
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
    public async Task CreateDefinitionAsync_ShouldForwardPreferredActorId()
    {
        var runtime = new RecordingActorRuntime();
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-preferred", new WorkflowGAgent()));
        var port = CreatePort(runtime);

        var actor = await port.CreateDefinitionAsync("definition-preferred", CancellationToken.None);

        actor.Id.Should().Be("definition-preferred");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowGAgent), "definition-preferred"));
    }

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
    public async Task CreateRunAsync_WhenExistingDefinitionYamlDiffersButWorkflowNameMatches_ShouldRebindExistingDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps:\n  - id: old\n    type: delay\n";
        runtime.StoredActors["definition-3"] = new RecordingActor("definition-3", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-3", new StubAgent("run-3")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-3",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-3");
        result.CreatedActorIds.Should().Equal("run-3");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowRunGAgent), (string?)null));
        ((RecordingActor)runtime.StoredActors["definition-3"]).LastHandledEnvelope.Should().NotBeNull();
        ((RecordingActor)runtime.StoredActors["definition-3"]).LastHandledEnvelope!.Payload!
            .Is(BindWorkflowDefinitionEvent.Descriptor)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionWorkflowNameDiffers_ShouldFailFast()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "other";
        definitionAgent.State.WorkflowYaml = "name: other\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-3"] = new RecordingActor("definition-3", definitionAgent);
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-3",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to workflow 'other'*cannot switch to 'direct'*");
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
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
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
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid workflow definition binding*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRunAsync_WhenBindingReaderReturnsNullForExistingActor_ShouldFailFast()
    {
        var runtime = new RecordingActorRuntime();
        runtime.StoredActors["definition-missing-binding"] = new RecordingActor("definition-missing-binding", new WorkflowGAgent());
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                ["definition-missing-binding"] = null,
            }));

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-missing-binding",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a workflow definition actor*");
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

    [Fact]
    public async Task CreateRunAsync_WhenInlineDefinitionsDiffer_ShouldRebindExistingDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        definitionAgent.State.InlineWorkflowYamls["child"] = "name: child\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-inline"] = new RecordingActor("definition-inline", definitionAgent);
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

        result.DefinitionActorId.Should().Be("definition-inline");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowRunGAgent), (string?)null));
        ((RecordingActor)runtime.StoredActors["definition-inline"]).LastHandledEnvelope.Should().NotBeNull();
        ((RecordingActor)runtime.StoredActors["definition-inline"]).LastHandledEnvelope!.Payload!
            .Is(BindWorkflowDefinitionEvent.Descriptor)
            .Should().BeTrue();
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_ShouldValidateNullActorInput()
    {
        var port = CreatePort(new RecordingActorRuntime());

        await FluentActions.Invoking(() => port.BindWorkflowDefinitionAsync(null!, "name: x", "x", null, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_ShouldDispatchEnvelopeWithInlineWorkflowMap()
    {
        var runtime = new RecordingActorRuntime();
        var actor = new RecordingActor("definition-inline-bind", new WorkflowGAgent());
        runtime.StoredActors[actor.Id] = actor;
        var port = CreatePort(runtime);

        await port.BindWorkflowDefinitionAsync(
            actor,
            "name: direct\nroles: []\nsteps: []\n",
            "direct",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["child"] = "name: child\nroles: []\nsteps: []\n",
            },
            CancellationToken.None);

        actor.LastHandledEnvelope.Should().NotBeNull();
        actor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
        var bind = actor.LastHandledEnvelope.Payload.Unpack<BindWorkflowDefinitionEvent>();
        bind.WorkflowName.Should().Be("direct");
        bind.InlineWorkflowYamls.Should().ContainKey("child");
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
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            }));

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-proxy",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-proxy");
        result.CreatedActorIds.Should().Equal("run-proxy");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowRunGAgent), (string?)null));
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
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
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
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("run bind failed");
        runtime.Destroyed.Should().Equal("run-fail", "definition-fail");
    }

    [Fact]
    public async Task CreateRunAsync_WhenDefinitionCreateRaces_ShouldReuseWinnerAndContinue()
    {
        var runtime = new RecordingActorRuntime();
        var racedDefinition = new RecordingActor("definition-race", new WorkflowGAgent());
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
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-race", new StubAgent("run-race")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-race",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-race");
        result.CreatedActorIds.Should().Equal("run-race");
        runtime.CreateRequests.Should().Contain((typeof(WorkflowGAgent), "definition-race"));
        runtime.CreateRequests.Should().Contain((typeof(WorkflowRunGAgent), (string?)null));
        racedDefinition.LastHandledEnvelope.Should().NotBeNull();
        racedDefinition.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    private static WorkflowRunActorPort CreatePort(
        RecordingActorRuntime runtime,
        IWorkflowActorBindingReader? bindingReader = null) =>
        new(runtime, runtime, bindingReader ?? new RuntimeBackedWorkflowActorBindingReader(runtime), [new WorkflowCoreModulePack()]);

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        private IActor? _lastCreatedActor;

        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);

        public Queue<IActor> ActorsToCreate { get; } = new();

        public List<(Type AgentType, string? RequestedId)> CreateRequests { get; } = [];

        public List<(string ParentId, string ChildId)> Linked { get; } = [];
        public List<string> Destroyed { get; } = [];
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

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var dispatchException = DispatchExceptionFactory?.Invoke(actorId, envelope);
            if (dispatchException != null)
                throw dispatchException;

            var actor = await GetAsync(actorId) ?? throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
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
                        StringComparer.OrdinalIgnoreCase)),
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
                        StringComparer.OrdinalIgnoreCase)),
                _ => WorkflowActorBinding.Unsupported(actor.Id),
            };
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
