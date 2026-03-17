using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Orchestration;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowExecutionTopologyResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldReturnEmpty_WhenRootActorIdBlank()
    {
        var runtime = new FakeActorRuntime();
        var resolver = new ActorRuntimeWorkflowExecutionTopologyResolver(runtime);

        var result = await resolver.ResolveAsync(" ", CancellationToken.None);

        result.Should().BeEmpty();
        runtime.GetRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnEmpty_WhenRootActorMissing()
    {
        var resolver = new ActorRuntimeWorkflowExecutionTopologyResolver(new FakeActorRuntime());

        var result = await resolver.ResolveAsync("missing", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldTraverseBreadthFirst_AndDeduplicateCycles()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["root"] = new FakeActor("root", ["child-1", "child-2"]);
        runtime.StoredActors["child-1"] = new FakeActor("child-1", ["child-2", "child-3"]);
        runtime.StoredActors["child-2"] = new FakeActor("child-2", ["root"]);
        runtime.StoredActors["child-3"] = new FakeActor("child-3", []);
        var resolver = new ActorRuntimeWorkflowExecutionTopologyResolver(runtime);

        var result = await resolver.ResolveAsync("root", CancellationToken.None);

        result.Should().Equal(
            new WorkflowTopologyEdge("root", "child-1"),
            new WorkflowTopologyEdge("root", "child-2"),
            new WorkflowTopologyEdge("child-1", "child-2"),
            new WorkflowTopologyEdge("child-1", "child-3"),
            new WorkflowTopologyEdge("child-2", "root"));
        runtime.GetRequests.Should().ContainInOrder("root", "root", "child-1", "child-2", "child-3");
    }

    [Fact]
    public async Task ResolveAsync_ShouldHonorCancellation()
    {
        var resolver = new ActorRuntimeWorkflowExecutionTopologyResolver(new FakeActorRuntime());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await resolver.ResolveAsync("root", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);
        public List<string> GetRequests { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id)
        {
            GetRequests.Add(id);
            return Task.FromResult(StoredActors.TryGetValue(id, out var actor) ? actor : null);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(StoredActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeActor(string id, IReadOnlyList<string> children) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new FakeAgent(id + "-agent");

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult(children);
    }

    private sealed class FakeAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
