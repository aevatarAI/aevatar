using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;

namespace Aevatar.GAgents.Device;

internal sealed class DeviceTombstoneCompactionTarget : ITombstoneCompactionTarget
{
    public string ActorId => DeviceRegistrationGAgent.WellKnownId;
    public string ProjectionKind => DeviceRegistrationProjectionPort.ProjectionKind;
    public string TargetName => "device registration";

    public IMessage CreateCommand(long safeStateVersion) =>
        new DeviceCompactTombstonesCommand { SafeStateVersion = safeStateVersion };
}
