using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberInvocationReadinessQueryPort(
    IStudioMemberService memberService) : IStudioMemberInvocationReadinessQueryPort
{
    public async Task<StudioMemberInvocationReadinessQueryResult?> GetAsync(
        string scopeId,
        string memberId,
        string endpointId,
        CancellationToken ct = default)
    {
        var contract = await memberService.GetEndpointContractAsync(
            scopeId,
            memberId,
            endpointId,
            ct);
        if (contract is null)
            return null;

        var readiness = contract.InvocationReadiness;
        return new StudioMemberInvocationReadinessQueryResult(
            contract.ScopeId,
            contract.MemberId,
            contract.PublishedServiceId,
            contract.EndpointId,
            contract.RevisionId,
            readiness.CanInvoke,
            readiness.Status,
            readiness.ReasonCode,
            readiness.Message,
            readiness.DeploymentId,
            readiness.ObservedAtUtc);
    }
}
