using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Runtime;

public interface ITombstoneCompactionTarget
{
    string ActorId { get; }
    string ProjectionKind { get; }
    string TargetName { get; }
    IMessage CreateCommand(long safeStateVersion);
}
