using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class NoopScheduledServiceInvocationCredentialExchangePort : IScheduledServiceInvocationCredentialExchangePort
{
    public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueSenderNyxIdAsync(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ScheduledServiceInvocationCredentialExchangeResult.Failure(
            "Scheduled service invocation sender NyxID credential exchange is not configured."));
    }

    public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueScopeOwnerNyxIdAsync(
        ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource source,
        ServiceIdentity serviceIdentity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(serviceIdentity);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ScheduledServiceInvocationCredentialExchangeResult.Failure(
            "Scheduled service invocation scope owner NyxID credential exchange is not configured."));
    }
}
