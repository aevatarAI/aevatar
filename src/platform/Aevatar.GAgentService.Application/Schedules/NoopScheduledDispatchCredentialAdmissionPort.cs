using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class NoopScheduledDispatchCredentialAdmissionPort : IScheduledDispatchCredentialAdmissionPort
{
    public Task<ScheduledDispatchCredentialAdmissionResult> AdmitAsync(
        ScheduledDispatchCredentialAdmissionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ScheduledDispatchCredentialAdmissionResult.Unsupported(
            "Scheduled dispatch scope owner NyxID admission is not configured."));
    }
}
