using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.App.Application.Projection.Reducers;

public sealed class UserAffiliateCreatedEventReducer
    : AppEventReducerBase<AppUserAffiliateReadModel, UserAffiliateCreatedEvent>
{
    protected override bool Reduce(
        AppUserAffiliateReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        UserAffiliateCreatedEvent evt,
        DateTimeOffset now)
    {
        if (readModel.CustomerId is { Length: > 0 })
            return false;

        readModel.UserId = evt.UserId;
        readModel.CustomerId = evt.CustomerId;
        readModel.Platform = evt.Platform;
        readModel.CreatedAt = evt.CreatedAt?.ToDateTimeOffset() ?? now;
        return true;
    }
}
