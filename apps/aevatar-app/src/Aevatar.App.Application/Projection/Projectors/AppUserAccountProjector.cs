using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.Projectors;

public sealed class AppUserAccountProjector(
    IProjectionDocumentStore<AppUserAccountReadModel, string> store,
    IEnumerable<IProjectionEventReducer<AppUserAccountReadModel, AppProjectionContext>> reducers)
    : AppProjectorBase<AppUserAccountReadModel>(store, reducers)
{
    protected override string ActorPrefix => "useraccount:";

    protected override AppUserAccountReadModel CreateInitialReadModel(string actorId) =>
        new() { Id = actorId };
}
