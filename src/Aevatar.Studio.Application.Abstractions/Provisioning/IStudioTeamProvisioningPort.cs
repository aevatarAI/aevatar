namespace Aevatar.Studio.Application.Provisioning;

/// <summary>
/// Narrow, tool-facing port for local Studio team creation.
/// Agent tool providers depend on this abstraction instead of referencing the
/// Studio application implementation assembly or calling HTTP endpoints.
/// </summary>
public interface IStudioTeamProvisioningPort
{
    Task<StudioTeamProvisioningResult> CreateAsync(
        StudioTeamProvisioningRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Narrow, tool-facing port for local Studio team discovery.
/// Agent tool providers depend on this abstraction instead of reading the
/// Studio application implementation or calling HTTP endpoints.
/// </summary>
public interface IStudioTeamQueryProvisioningPort
{
    Task<StudioTeamListProvisioningResult> ListAsync(
        StudioTeamListProvisioningRequest request,
        CancellationToken ct = default);
}
