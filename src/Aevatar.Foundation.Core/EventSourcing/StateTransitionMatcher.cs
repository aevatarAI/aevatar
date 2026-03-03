using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Helper for extracting typed events from raw or Any-packed payloads.
/// </summary>
public static class StateTransitionMatcher
{
    public static bool TryExtract<TEvent>(IMessage evt, out TEvent extracted)
        where TEvent : class, IMessage<TEvent>, new()
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt is TEvent typed)
        {
            extracted = typed;
            return true;
        }

        if (evt is Any any && any.TryUnpack<TEvent>(out var unpacked))
        {
            extracted = unpacked;
            return true;
        }

        extracted = null!;
        return false;
    }
}
