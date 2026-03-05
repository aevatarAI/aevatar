using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents;

public sealed class UserAccountGAgent : GAgentBase<UserAccountState>
{
    [EventHandler]
    public async Task HandleRegisterUser(UserRegisteredEvent evt)
    {
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        if (State.User?.Id is { Length: > 0 })
        {
            await PersistDomainEventAsync(new UserLoginUpdatedEvent
            {
                UserId = State.User.Id,
                Email = evt.Email,
                EmailVerified = evt.EmailVerified,
                LoginAt = now,
            });
            return;
        }

        evt.RegisteredAt = now;
        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleLinkProvider(UserProviderLinkedEvent evt)
    {
        var user = State.User?.Id is { Length: > 0 } ? State.User : null;
        if (user is null) throw new InvalidOperationException("User not found");

        evt.UserId = user.Id;
        evt.EmailVerified = evt.EmailVerified || user.EmailVerified;
        evt.LinkedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleUpdateLogin(UserLoginUpdatedEvent evt)
    {
        evt.UserId = State.User?.Id ?? string.Empty;
        evt.LoginAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleDeleteAccount(AccountDeletedEvent evt)
    {
        var user = State.User;
        evt.UserId = user?.Id ?? string.Empty;
        evt.Email = user?.Email ?? string.Empty;
        evt.DeletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventAsync(evt);
    }

    protected override UserAccountState TransitionState(UserAccountState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<UserRegisteredEvent>((s, reg) =>
            {
                s.User = new User
                {
                    Id = reg.UserId,
                    AuthProvider = reg.AuthProvider,
                    AuthProviderId = reg.AuthProviderId,
                    Email = reg.Email,
                    EmailVerified = reg.EmailVerified,
                    CreatedAt = reg.RegisteredAt,
                    UpdatedAt = reg.RegisteredAt,
                    LastLoginAt = reg.RegisteredAt
                };
                return s;
            })
            .On<UserProviderLinkedEvent>((s, linked) =>
            {
                if (s.User is not null)
                {
                    s.User.AuthProvider = linked.AuthProvider;
                    s.User.AuthProviderId = linked.AuthProviderId;
                    s.User.EmailVerified = linked.EmailVerified;
                    s.User.UpdatedAt = linked.LinkedAt;
                    s.User.LastLoginAt = linked.LinkedAt;
                }
                return s;
            })
            .On<UserLoginUpdatedEvent>((s, login) =>
            {
                if (s.User is not null)
                {
                    s.User.Email = login.Email;
                    s.User.EmailVerified = login.EmailVerified;
                    s.User.LastLoginAt = login.LoginAt;
                    s.User.UpdatedAt = login.LoginAt;
                }
                return s;
            })
            .On<AccountDeletedEvent>((s, _) =>
            {
                s.User = null;
                return s;
            })
            .OrCurrent();
}
