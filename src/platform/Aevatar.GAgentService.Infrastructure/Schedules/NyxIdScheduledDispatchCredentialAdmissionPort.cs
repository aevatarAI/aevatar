using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class NyxIdScheduledDispatchCredentialAdmissionPort : IScheduledDispatchCredentialAdmissionPort
{
    private readonly IExternalIdentityBindingQueryPort _bindingQueryPort;

    public NyxIdScheduledDispatchCredentialAdmissionPort(IExternalIdentityBindingQueryPort bindingQueryPort)
    {
        _bindingQueryPort = bindingQueryPort ?? throw new ArgumentNullException(nameof(bindingQueryPort));
    }

    public async Task<ScheduledDispatchCredentialAdmissionResult> AdmitAsync(
        ScheduledDispatchCredentialAdmissionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var ownerSubject = request.ScopeOwnerNyxId.OwnerSubject;
        if (ownerSubject == null)
        {
            return ScheduledDispatchCredentialAdmissionResult.MissingBinding(
                "Authenticated NyxID owner subject is required for scope owner schedule auth.");
        }

        var externalSubject = new ExternalSubjectRef
        {
            Platform = ownerSubject.Platform,
            Tenant = ownerSubject.Tenant,
            ExternalUserId = ownerSubject.ExternalUserId,
        };
        var binding = await _bindingQueryPort.ResolveAsync(externalSubject, ct).ConfigureAwait(false);
        return binding == null
            ? ScheduledDispatchCredentialAdmissionResult.MissingBinding(
                "Authenticated NyxID owner binding is required for scope owner schedule auth; complete or refresh NyxID login before creating a scope owner schedule.")
            : ScheduledDispatchCredentialAdmissionResult.Allowed();
    }
}
