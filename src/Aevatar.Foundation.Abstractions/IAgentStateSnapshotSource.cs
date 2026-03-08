using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions;

public interface IAgentStateSnapshotSource
{
    string StateTypeName { get; }

    byte[] GetStateSnapshotBytes();

    long StateVersion { get; }
}
