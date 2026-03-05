using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.Projectors;

public sealed class AppUserProfileProjector(
    IProjectionDocumentStore<AppUserProfileReadModel, string> store,
    IEnumerable<IProjectionEventReducer<AppUserProfileReadModel, AppProjectionContext>> reducers)
    : AppProjectorBase<AppUserProfileReadModel>(store, reducers)
{
    protected override string ActorPrefix => "userprofile:";

    protected override AppUserProfileReadModel CreateInitialReadModel(string actorId) =>
        new() { Id = actorId };
}
