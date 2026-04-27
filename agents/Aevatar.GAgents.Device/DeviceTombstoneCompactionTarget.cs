using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;

namespace Aevatar.GAgents.Device;

internal sealed class DeviceTombstoneCompactionTarget : ITombstoneCompactionTarget
{
    public string ActorId => DeviceRegistrationGAgent.WellKnownId;
    public string ProjectionKind => DeviceRegistrationProjectionPort.ProjectionKind;
    public string TargetName => "device registration";

    public async Task EnsureActorAsync(IActorRuntime actorRuntime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actorRuntime);
        _ = await actorRuntime.GetAsync(DeviceRegistrationGAgent.WellKnownId)
            ?? await actorRuntime.CreateAsync<DeviceRegistrationGAgent>(
                DeviceRegistrationGAgent.WellKnownId,
                ct);
    }

    public IMessage CreateCommand(long safeStateVersion) =>
        new DeviceCompactTombstonesCommand { SafeStateVersion = safeStateVersion };
}
