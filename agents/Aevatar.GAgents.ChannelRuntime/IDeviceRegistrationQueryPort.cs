namespace Aevatar.GAgents.ChannelRuntime;

public interface IDeviceRegistrationQueryPort
{
    Task<DeviceRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default);

    Task<IReadOnlyList<DeviceRegistrationEntry>> ListAsync(CancellationToken ct = default);
}
