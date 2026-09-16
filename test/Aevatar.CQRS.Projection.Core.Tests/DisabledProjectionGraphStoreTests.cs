using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class DisabledProjectionGraphStoreTests
{
    [Fact]
    public async Task DisabledStore_ShouldAcceptWritesAndExposeNoGraphData()
    {
        var store = new DisabledProjectionGraphStore();

        await store.ReplaceOwnerGraphAsync(new ProjectionOwnedGraph
        {
            ProjectionKind = "disabled-test",
            StateVersion = 1,
        });
        await store.UpsertNodeAsync(new ProjectionGraphNode());
        await store.UpsertEdgeAsync(new ProjectionGraphEdge());
        await store.DeleteNodeAsync("scope", "node-1");
        await store.DeleteEdgeAsync("scope", "edge-1");
        var deltaResult = await store.ApplyDeltaAsync(new ProjectionGraphDelta());

        deltaResult.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        (await store.ListNodesByOwnerAsync("scope", "owner-1")).Should().BeEmpty();
        (await store.ListEdgesByOwnerAsync("scope", "owner-1")).Should().BeEmpty();
        (await store.GetNeighborsAsync(new ProjectionGraphQuery())).Should().BeEmpty();
        var subgraph = await store.GetSubgraphAsync(new ProjectionGraphQuery());
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();

        var snapshot = await store.ReadOwnerSnapshotAsync(new ProjectionGraphRouteFingerprint());
        snapshot.Disposition.Should().Be(ProjectionGraphOwnerSnapshotReadDisposition.NotFound);
        snapshot.Snapshot.Should().BeNull();
    }

    [Fact]
    public async Task DisabledStore_ShouldHonorCancellationForEveryOperation()
    {
        var store = new DisabledProjectionGraphStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var ct = cancellation.Token;
        Func<Task>[] operations =
        [
            () => store.ReplaceOwnerGraphAsync(new ProjectionOwnedGraph
            {
                ProjectionKind = "disabled-test",
                StateVersion = 1,
            }, ct),
            () => store.UpsertNodeAsync(new ProjectionGraphNode(), ct),
            () => store.UpsertEdgeAsync(new ProjectionGraphEdge(), ct),
            () => store.DeleteNodeAsync("scope", "node", ct),
            () => store.DeleteEdgeAsync("scope", "edge", ct),
            () => store.ListNodesByOwnerAsync("scope", "owner", ct: ct),
            () => store.ListEdgesByOwnerAsync("scope", "owner", ct: ct),
            () => store.GetNeighborsAsync(new ProjectionGraphQuery(), ct),
            () => store.GetSubgraphAsync(new ProjectionGraphQuery(), ct),
            () => store.ApplyDeltaAsync(new ProjectionGraphDelta(), ct),
            () => store.ReadOwnerSnapshotAsync(new ProjectionGraphRouteFingerprint(), ct),
        ];

        foreach (var operation in operations)
            await operation.Should().ThrowAsync<OperationCanceledException>();
    }
}
