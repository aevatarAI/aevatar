using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.App.Application.Projection.Reducers;

public sealed class AuthLookupSetEventReducer
    : AppEventReducerBase<AppAuthLookupReadModel, AuthLookupSetEvent>
{
    protected override bool Reduce(
        AppAuthLookupReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        AuthLookupSetEvent evt,
        DateTimeOffset now)
    {
        readModel.LookupKey = evt.LookupKey;
        readModel.UserId = evt.UserId;
        return true;
    }
}

public sealed class AuthLookupClearedEventReducer
    : AppEventReducerBase<AppAuthLookupReadModel, AuthLookupClearedEvent>
{
    protected override bool Reduce(
        AppAuthLookupReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        AuthLookupClearedEvent evt,
        DateTimeOffset now)
    {
        readModel.UserId = string.Empty;
        return true;
    }
}
