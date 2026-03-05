using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.App.Application.Projection.Reducers;

public sealed class UserRegisteredEventReducer
    : AppEventReducerBase<AppUserAccountReadModel, UserRegisteredEvent>
{
    protected override bool Reduce(
        AppUserAccountReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        UserRegisteredEvent evt,
        DateTimeOffset now)
    {
        readModel.UserId = evt.UserId;
        readModel.AuthProvider = evt.AuthProvider;
        readModel.AuthProviderId = evt.AuthProviderId;
        readModel.Email = evt.Email;
        readModel.EmailVerified = evt.EmailVerified;
        readModel.CreatedAt = evt.RegisteredAt?.ToDateTimeOffset() ?? now;
        readModel.LastLoginAt = readModel.CreatedAt;
        readModel.Deleted = false;
        return true;
    }
}

public sealed class UserProviderLinkedEventReducer
    : AppEventReducerBase<AppUserAccountReadModel, UserProviderLinkedEvent>
{
    protected override bool Reduce(
        AppUserAccountReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        UserProviderLinkedEvent evt,
        DateTimeOffset now)
    {
        readModel.AuthProvider = evt.AuthProvider;
        readModel.AuthProviderId = evt.AuthProviderId;
        readModel.EmailVerified = evt.EmailVerified;
        readModel.LastLoginAt = evt.LinkedAt?.ToDateTimeOffset() ?? now;
        return true;
    }
}

public sealed class UserLoginUpdatedEventReducer
    : AppEventReducerBase<AppUserAccountReadModel, UserLoginUpdatedEvent>
{
    protected override bool Reduce(
        AppUserAccountReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        UserLoginUpdatedEvent evt,
        DateTimeOffset now)
    {
        readModel.Email = evt.Email;
        readModel.EmailVerified = evt.EmailVerified;
        readModel.LastLoginAt = evt.LoginAt?.ToDateTimeOffset() ?? now;
        return true;
    }
}

public sealed class AccountDeletedEventUserReducer
    : AppEventReducerBase<AppUserAccountReadModel, AccountDeletedEvent>
{
    protected override bool Reduce(
        AppUserAccountReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        AccountDeletedEvent evt,
        DateTimeOffset now)
    {
        readModel.Deleted = true;
        return true;
    }
}
