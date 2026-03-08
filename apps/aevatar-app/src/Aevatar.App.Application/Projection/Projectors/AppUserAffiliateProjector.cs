using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.Projectors;

public sealed class AppUserAffiliateProjector(
    IProjectionDocumentStore<AppUserAffiliateReadModel, string> store,
    IEnumerable<IProjectionEventReducer<AppUserAffiliateReadModel, AppProjectionContext>> reducers)
    : AppProjectorBase<AppUserAffiliateReadModel>(store, reducers)
{
    protected override string ActorPrefix => "useraffiliate:";

    protected override AppUserAffiliateReadModel CreateInitialReadModel(string actorId) =>
        new() { Id = actorId };
}
