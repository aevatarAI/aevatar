using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

public sealed class ClaimMessageSinkGAgent : GAgentBase<ListValue>
{
    [EventHandler]
    public Task HandleReceived(StringValue evt) =>
        PersistDomainEventAsync(evt.Clone(), CancellationToken.None);

    protected override ListValue TransitionState(ListValue current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<StringValue>((state, message) =>
            {
                var next = state.Clone();
                next.Values.Add(Value.ForString(message.Value ?? string.Empty));
                return next;
            })
            .OrCurrent();
}
