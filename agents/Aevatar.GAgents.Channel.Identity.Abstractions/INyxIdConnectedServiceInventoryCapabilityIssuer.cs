using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Issues a request-local NyxID user capability for reading the connected-service
/// inventory of one known external-identity binding. Unlike
/// <see cref="INyxIdCapabilityBroker"/>, this narrow port does not assert that the
/// binding grants every Aevatar runtime service; callers must use it only for the
/// read-only account inventory surface.
/// </summary>
public interface INyxIdConnectedServiceInventoryCapabilityIssuer
{
    /// <summary>
    /// Exchanges a known binding for a short-lived user token that can read the
    /// caller's NyxID connected-service inventory.
    /// </summary>
    Task<CapabilityHandle> IssueByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CancellationToken ct = default);
}
