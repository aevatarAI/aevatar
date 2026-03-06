using Aevatar.App.Application.Completion;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.Application.Projection.Projectors;

public sealed class AppSyncEntityProjector(
    IProjectionDocumentStore<AppSyncEntityReadModel, string> store,
    IEnumerable<IProjectionEventReducer<AppSyncEntityReadModel, AppProjectionContext>> reducers,
    ICompletionPort completionPort)
    : AppProjectorBase<AppSyncEntityReadModel>(store, reducers)
{
    private static readonly string EntitiesSyncedTypeUrl =
        Any.Pack(new EntitiesSyncedEvent()).TypeUrl;

    protected override string ActorPrefix => "syncentity:";

    protected override AppSyncEntityReadModel CreateInitialReadModel(string actorId) =>
        new() { Id = actorId };

    protected override ValueTask OnProjectedAsync(
        AppProjectionContext context, EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Payload is not null &&
            string.Equals(envelope.Payload.TypeUrl, EntitiesSyncedTypeUrl, StringComparison.Ordinal))
        {
            var synced = envelope.Payload.Unpack<EntitiesSyncedEvent>();
            if (!string.IsNullOrEmpty(synced.SyncId))
                completionPort.Complete(synced.SyncId);
        }

        return ValueTask.CompletedTask;
    }
}
