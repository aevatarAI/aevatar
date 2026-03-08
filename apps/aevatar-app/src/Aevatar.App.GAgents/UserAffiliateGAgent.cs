using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.GAgents;

public sealed class UserAffiliateGAgent : GAgentBase<UserAffiliateState>
{
    [EventHandler]
    public async Task HandleCreateAffiliate(UserAffiliateCreatedEvent evt)
    {
        if (State.CustomerId is { Length: > 0 })
            return;

        await PersistDomainEventAsync(evt);
    }

    protected override UserAffiliateState TransitionState(
        UserAffiliateState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<UserAffiliateCreatedEvent>((s, e) =>
            {
                s.UserId = e.UserId;
                s.CustomerId = e.CustomerId;
                s.Platform = e.Platform;
                s.CreatedAt = e.CreatedAt;
                return s;
            })
            .OrCurrent();
}
