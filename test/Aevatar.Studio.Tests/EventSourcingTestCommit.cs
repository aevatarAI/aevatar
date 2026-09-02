using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

internal static class EventSourcingTestCommit
{
    public static EventStoreCommitResult From(
        IEnumerable<IMessage> pending,
        long currentVersion,
        string agentId = "")
    {
        var result = new EventStoreCommitResult
        {
            AgentId = agentId,
        };
        foreach (var evt in pending)
        {
            currentVersion++;
            result.CommittedEvents.Add(new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = currentVersion,
                EventType = evt.Descriptor.FullName,
                EventData = Any.Pack(evt),
                AgentId = agentId,
            });
        }

        result.LatestVersion = currentVersion;
        return result;
    }
}
