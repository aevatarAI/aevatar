using Aevatar.App.Application.Completion;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Projection.Stores;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.App.Application.Tests;

public sealed class SyncAppServiceTests
{
    private static (SyncAppService Svc, IProjectionDocumentStore<AppSyncEntityReadModel, string> SyncStore) Create(TestActorFactory? factory = null)
    {
        factory ??= new TestActorFactory();
        var syncStore = new AppInMemoryDocumentStore<AppSyncEntityReadModel, string>(m => m.Id);
        var port = new TestCompletionPort();
        var actors = new ProjectingActorAccessService(factory, syncStore);
        var svc = new SyncAppService(
            actors,
            port,
            syncStore,
            NullLogger<SyncAppService>.Instance);
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

internal sealed class TestCompletionPort : ICompletionPort
{
    public Task WaitAsync(string completionKey, CancellationToken ct = default) => Task.CompletedTask;
    public void Complete(string completionKey) { }
}

internal sealed class ProjectingActorAccessService : IActorAccessAppService
{
    private readonly TestActorFactory _inner;
    private readonly IProjectionDocumentStore<AppSyncEntityReadModel, string> _store;

    public ProjectingActorAccessService(
        TestActorFactory inner,
        IProjectionDocumentStore<AppSyncEntityReadModel, string> store)
    {
        _inner = inner;
        _store = store;
    }

    public async Task SendCommandAsync<TAgent>(string id, IMessage command, CancellationToken ct = default)
        where TAgent : class, IAgent
    {
        await _inner.SendCommandAsync<TAgent>(id, command, ct);

        var agent = await _inner.GetOrCreateAgentAsync<TAgent>(id);
        if (agent is GAgentBase<SyncEntityState> syncAgent)
        {
            var actorId = _inner.ResolveActorId<TAgent>(id);
            var state = syncAgent.State;
            await _store.MutateAsync(actorId, m =>
            {
                m.Id = actorId;
                m.UserId = id;
                m.ServerRevision = state.Meta?.Revision ?? 0;
                foreach (var kv in state.Entities)
                    m.Entities[kv.Key] = ProtoToEntry(kv.Value);

                var last = state.LastSyncResult;
                if (last is not null && !string.IsNullOrEmpty(last.SyncId))
                {
                    m.SyncResults[last.SyncId] = new SyncResultEntry
                    {
                        SyncId = last.SyncId,
                        ClientRevision = last.ClientRevision,
                        ServerRevision = last.ServerRevision,
                        Accepted = [.. last.Accepted],
                        Rejected = last.Rejected
                            .Select(r => new RejectedEntityEntry
                            {
                                ClientId = r.ClientId,
                                ServerRevision = r.ServerRevision,
                                Reason = r.Reason,
                            })
                            .ToList(),
                    };
                    if (!m.SyncResultOrder.Contains(last.SyncId))
                        m.SyncResultOrder.Add(last.SyncId);
                }
            }, ct);
        }
    }

    public string ResolveActorId<TAgent>(string id) where TAgent : class, IAgent
        => _inner.ResolveActorId<TAgent>(id);

    private static SyncEntityEntry ProtoToEntry(SyncEntity e) => new()
    {
        ClientId = e.ClientId,
        EntityType = e.EntityType,
        UserId = e.UserId,
        Revision = e.Revision,
        Position = e.Position,
        Source = e.Source switch
        {
            EntitySource.Bank => "bank",
            EntitySource.User => "user",
            EntitySource.Edited => "edited",
            _ => "ai"
        },
        BankEligible = e.BankEligible,
        BankHash = e.BankHash,
    };
}
