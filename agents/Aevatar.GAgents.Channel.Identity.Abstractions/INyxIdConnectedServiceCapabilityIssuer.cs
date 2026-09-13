using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Issues a request-local NyxID capability for connected-service discovery and
/// execution under one verified external-identity binding. Unlike
/// <see cref="INyxIdCapabilityBroker"/>, this narrow port does not assert that the
/// binding grants every Aevatar runtime service. Each operation remains subject
/// to its exact service contract, runtime selection, and NyxID authorization.
/// </summary>
public interface INyxIdConnectedServiceCapabilityIssuer
{
    /// <summary>
    /// Exchanges a known binding for a short-lived user token that can read the
    /// caller's NyxID connected-service inventory and execute admitted operations.
    /// </summary>
    Task<CapabilityHandle> IssueByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CancellationToken ct = default);
}
