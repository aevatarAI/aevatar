using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.App.Application.Tests;

public sealed class SyncAppServiceTests
{
    private static (SyncAppService Svc, IProjectionDocumentStore<AppSyncEntityReadModel, string> SyncStore) Create(TestActorFactory? factory = null)
    {
        factory ??= new TestActorFactory();
        var syncStore = new AppInMemoryDocumentStore<AppSyncEntityReadModel, string>(m => m.Id);
        var svc = new SyncAppService(
            factory,
            syncStore,
            new AppInMemoryDocumentStore<AppSyncEntityLastResultReadModel, string>(m => m.Id));
        return (svc, syncStore);
    }

    [Fact]
    public async Task GetState_EmptyUser_ReturnsEmptyState()
    {
        var (svc, _) = Create();

        var state = await svc.GetStateAsync("user-1");

        state.ServerRevision.Should().Be(0);
        state.GroupedEntities.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAsync_WithEntities_ReturnsSyncResult()
    {
        var factory = new TestActorFactory();
        var (svc, _) = Create(factory);

        var entity = new SyncEntity
        {
            ClientId = "c1",
            EntityType = "manifestation",
            UserId = "user-1",
            Revision = 0,
        };

        var result = await svc.SyncAsync("sync-1", "user-1", 0, [entity]);

        result.SyncId.Should().Be("sync-1");
        result.ServerRevision.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SyncThenGetState_ReflectsChanges()
    {
        var factory = new TestActorFactory();
        var (svc, syncStore) = Create(factory);

        var entity = new SyncEntity
        {
            ClientId = "c1",
            EntityType = "manifestation",
            UserId = "user-1",
            Revision = 0,
        };

        var result = await svc.SyncAsync("sync-1", "user-1", 0, [entity]);

        result.ServerRevision.Should().BeGreaterThan(0);
        result.DeltaEntities.Should().ContainKey("c1");
        result.DeltaEntities["c1"].EntityType.Should().Be("manifestation");
    }
}
