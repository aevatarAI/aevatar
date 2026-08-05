using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.GAgentService.Application.Schedules.Authorization;

public sealed class UnavailableNyxIdScheduledOperationAuthorizationPort
    : INyxIdScheduledOperationAuthorizationPort
{
    public static UnavailableNyxIdScheduledOperationAuthorizationPort Instance { get; } = new();

    public Task<NyxIdScheduledOperationAuthorizationResult> EvaluateAsync(
        NyxIdScheduledOperationAuthorizationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new NyxIdScheduledOperationAuthorizationResult(
            NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable));
    }
}
