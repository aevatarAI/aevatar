using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.Projectors;

public sealed class AppPaymentTransactionProjector(
    IProjectionDocumentStore<AppPaymentTransactionReadModel, string> store,
    IEnumerable<IProjectionEventReducer<AppPaymentTransactionReadModel, AppProjectionContext>> reducers)
    : AppProjectorBase<AppPaymentTransactionReadModel>(store, reducers)
{
    protected override string ActorPrefix => "paymenttransaction:";

    protected override AppPaymentTransactionReadModel CreateInitialReadModel(string actorId) =>
        new() { Id = actorId };
}
