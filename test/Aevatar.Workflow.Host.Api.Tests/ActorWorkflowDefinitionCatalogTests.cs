using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ActorWorkflowDefinitionCatalogTests
{
    [Fact]
    public async Task GetNamesAsync_ShouldReadFromSnapshotReader()
    {
        var catalog = CreateCatalog(new WorkflowDefinitionCatalogState
        {
            Entries =
            {
                ["zeta"] = new WorkflowDefinitionCatalogEntryState { WorkflowName = "zeta", WorkflowYaml = "name: zeta" },
                ["alpha"] = new WorkflowDefinitionCatalogEntryState { WorkflowName = "alpha", WorkflowYaml = "name: alpha" },
            },
        });

        var names = await catalog.GetNamesAsync();

        names.Should().Equal("alpha", "zeta");
    }

    [Fact]
    public async Task GetYamlAsync_ShouldReadFromSnapshotReader()
    {
        var catalog = CreateCatalog(new WorkflowDefinitionCatalogState
        {
            Entries =
            {
                ["brainstorm"] = new WorkflowDefinitionCatalogEntryState
                {
                    WorkflowName = "brainstorm",
                    WorkflowYaml = "name: brainstorm\nsteps: []\n",
                },
            },
        });

        var yaml = await catalog.GetYamlAsync("brainstorm");

        yaml.Should().Contain("name: brainstorm");
    }

    [Fact]
    public async Task UpsertAsync_ShouldDispatchCatalogCommandAsExternalEnvelope()
    {
        var actor = new RecordingActor("workflow-definition-catalog");
        var runtime = new RecordingActorRuntime(actor);
        var catalog = new ActorWorkflowDefinitionCatalog(
            runtime,
            new RuntimeWorkflowQueryClient(new FailingStreamProvider(), new RuntimeStreamRequestReplyClient()));

        await catalog.UpsertAsync("demo", "name: demo\nsteps: []\n");

        actor.LastEnvelope.Should().NotBeNull();
        actor.LastEnvelope!.PublisherId.Should().NotBe(actor.Id);
        actor.LastEnvelope.PublisherId.Should().Be("workflow-definition-catalog-client");
        actor.LastEnvelope.Direction.Should().Be(EventDirection.Down);
        actor.LastEnvelope.TargetActorId.Should().Be(actor.Id);
        actor.LastEnvelope.Payload!.Unpack<UpsertWorkflowDefinitionRequestedEvent>().WorkflowName.Should().Be("demo");
    }

    [Fact]
    public async Task UpsertAsync_ShouldPreferDirectEnvelopeDispatcherWhenAvailable()
    {
        var actor = new RecordingActor("workflow-definition-catalog");
        var runtime = new RecordingActorRuntime(actor);
        var dispatcher = new RecordingEnvelopeDispatcher();
        var catalog = new ActorWorkflowDefinitionCatalog(
            runtime,
            new RuntimeWorkflowQueryClient(new FailingStreamProvider(), new RuntimeStreamRequestReplyClient()),
            envelopeDispatcher: dispatcher);

        await catalog.UpsertAsync("demo", "name: demo\nsteps: []\n");

        dispatcher.ActorId.Should().Be(actor.Id);
        dispatcher.LastEnvelope.Should().NotBeNull();
        actor.LastEnvelope.Should().BeNull();
    }

    private static ActorWorkflowDefinitionCatalog CreateCatalog(WorkflowDefinitionCatalogState state) =>
        new(
            new FailingActorRuntime(),
            new RuntimeWorkflowQueryClient(new FailingStreamProvider(), new RuntimeStreamRequestReplyClient()),
            new FixedSnapshotReader(state));

    private sealed class FixedSnapshotReader : IActorStateSnapshotReader
    {
        private readonly WorkflowDefinitionCatalogState _state;

        public FixedSnapshotReader(WorkflowDefinitionCatalogState state)
        {
            _state = state;
        }

        public Task<TState?> GetStateAsync<TState>(string actorId, CancellationToken ct = default)
            where TState : class, IMessage, new()
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<TState?>((TState)(object)_state.Clone());
        }
    }

    private sealed class FailingActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new InvalidOperationException("Snapshot-backed query should not create actors.");

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Snapshot-backed query should not create actors.");

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new InvalidOperationException("Snapshot-backed query should not destroy actors.");

        public Task<IActor?> GetAsync(string id) =>
            throw new InvalidOperationException("Snapshot-backed query should not use runtime lookup.");

        public Task<bool> ExistsAsync(string id) =>
            throw new InvalidOperationException("Snapshot-backed query should not use runtime lookup.");

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Snapshot-backed query should not link actors.");

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Snapshot-backed query should not unlink actors.");
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly IActor _actor;

        public RecordingActorRuntime(IActor actor)
        {
            _actor = actor;
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            Task.FromResult(_actor);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            Task.FromResult(_actor);

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(_actor);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(true);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FailingStreamProvider : IStreamProvider
    {
        public IStream GetStream(string actorId)
        {
            _ = actorId;
            throw new InvalidOperationException("Snapshot-backed query should not use stream request-reply.");
        }
    }

    private sealed class RecordingEnvelopeDispatcher : IActorEnvelopeDispatcher
    {
        public string? ActorId { get; private set; }

        public EventEnvelope? LastEnvelope { get; private set; }

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ActorId = actorId;
            LastEnvelope = envelope.Clone();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
            Agent = new RecordingAgent(id);
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public EventEnvelope? LastEnvelope { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastEnvelope = envelope.Clone();
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent : IAgent
    {
        public RecordingAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("recording");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
