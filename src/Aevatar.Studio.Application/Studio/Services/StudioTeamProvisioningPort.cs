using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Thin adapter exposing a tool-facing Studio team creation port over the
/// existing application service. It preserves the application boundary while
/// keeping local agent tools off HTTP self-calls and NyxID proxy routing.
/// </summary>
public sealed class StudioTeamProvisioningPort : IStudioTeamProvisioningPort
{
    private readonly IStudioTeamService _teamService;

    public StudioTeamProvisioningPort(IStudioTeamService teamService)
    {
        _teamService = teamService ?? throw new ArgumentNullException(nameof(teamService));
    }

    public async Task<StudioTeamProvisioningResult> CreateAsync(
        StudioTeamProvisioningRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var summary = await _teamService.CreateAsync(
            request.ScopeId,
            new CreateStudioTeamRequest(
                request.DisplayName,
                request.Description,
                request.TeamId),
            ct);

        return new StudioTeamProvisioningResult(
            Success: true,
            ScopeId: summary.ScopeId,
            TeamId: summary.TeamId,
            DisplayName: summary.DisplayName,
            Description: summary.Description,
            LifecycleStage: summary.LifecycleStage,
            MemberCount: summary.MemberCount,
            CreatedAt: summary.CreatedAt,
            UpdatedAt: summary.UpdatedAt)
        {
            EntryMemberId = summary.EntryMemberId,
        };
    }

}
