using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberProvisioningPort : IStudioMemberProvisioningPort
{
    private readonly IStudioMemberService _memberService;

    public StudioMemberProvisioningPort(IStudioMemberService memberService)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
    }

    public async Task<StudioMemberProvisioningResult> CreateAsync(
        StudioMemberProvisioningRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var summary = await _memberService.CreateAsync(
            request.ScopeId,
            new CreateStudioMemberRequest(
                request.DisplayName,
                request.ImplementationKind,
                request.Description,
                request.MemberId,
                request.TeamId),
            ct);

        return new StudioMemberProvisioningResult(
            Success: true,
            ScopeId: summary.ScopeId,
            MemberId: summary.MemberId,
            DisplayName: summary.DisplayName,
            Description: summary.Description,
            ImplementationKind: summary.ImplementationKind,
            LifecycleStage: summary.LifecycleStage,
            PublishedServiceId: summary.PublishedServiceId,
            LastBoundRevisionId: summary.LastBoundRevisionId,
            CreatedAt: summary.CreatedAt,
            UpdatedAt: summary.UpdatedAt)
        {
            TeamId = summary.TeamId,
        };
    }
}
