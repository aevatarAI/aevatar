using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.App.GAgents;

public sealed class AuthLookupGAgent : GAgentBase<AuthLookupState>
{
    [EventHandler]
    public Task HandleSetAuthLookup(AuthLookupSetEvent evt) =>
        PersistDomainEventAsync(evt);

    [EventHandler]
    public Task HandleClearAuthLookup(AuthLookupClearedEvent evt)
    {
        var persistEvt = new AuthLookupClearedEvent { LookupKey = State.LookupKey };
        return PersistDomainEventAsync(persistEvt);
    }

    protected override AuthLookupState TransitionState(AuthLookupState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AuthLookupSetEvent>((s, e) =>
            {
                s.LookupKey = e.LookupKey;
                s.UserId = e.UserId;
                return s;
            })
            .On<AuthLookupClearedEvent>((s, _) =>
            {
                s.LookupKey = string.Empty;
                s.UserId = string.Empty;
                return s;
            })
            .OrCurrent();
}
