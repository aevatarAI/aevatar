namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioMemberProvisioningPort
{
    Task<StudioMemberProvisioningResult> CreateAsync(
        StudioMemberProvisioningRequest request,
        CancellationToken ct = default);
}
