using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.Projectors;

public sealed class AppAuthLookupProjector(
    IProjectionDocumentStore<AppAuthLookupReadModel, string> store,
    IEnumerable<IProjectionEventReducer<AppAuthLookupReadModel, AppProjectionContext>> reducers)
    : AppProjectorBase<AppAuthLookupReadModel>(store, reducers)
{
    protected override string ActorPrefix => "authlookup:";

    protected override AppAuthLookupReadModel CreateInitialReadModel(string actorId) =>
        new() { Id = actorId };
}
