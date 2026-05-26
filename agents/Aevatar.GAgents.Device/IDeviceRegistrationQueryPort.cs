namespace Aevatar.GAgents.Device;

public interface IDeviceRegistrationQueryPort
{
    Task<DeviceRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default);

    Task<IReadOnlyList<DeviceRegistrationEntry>> QueryAllAsync(CancellationToken ct = default);
}
