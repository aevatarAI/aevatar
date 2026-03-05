using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.Projectors;

public sealed class AppSyncEntityProjector(
    IProjectionDocumentStore<AppSyncEntityReadModel, string> store,
    IEnumerable<IProjectionEventReducer<AppSyncEntityReadModel, AppProjectionContext>> reducers)
    : AppProjectorBase<AppSyncEntityReadModel>(store, reducers)
{
    protected override string ActorPrefix => "syncentity:";

    protected override AppSyncEntityReadModel CreateInitialReadModel(string actorId) =>
        new() { Id = actorId };
}
