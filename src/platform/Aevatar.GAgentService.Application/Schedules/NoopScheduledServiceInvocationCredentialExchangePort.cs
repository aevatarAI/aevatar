using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class NoopScheduledServiceInvocationCredentialExchangePort : IScheduledServiceInvocationCredentialExchangePort
{
    public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueNyxIdAsync(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ScheduledServiceInvocationCredentialExchangeResult.Failure(
            $"Scheduled service invocation {ToErrorSubject(source.Role)} NyxID credential exchange is not configured."));
    }

    private static string ToErrorSubject(ScheduledServiceInvocationNyxIdCredentialRole role) =>
        role == ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner ? "scope owner" : "sender";
}
