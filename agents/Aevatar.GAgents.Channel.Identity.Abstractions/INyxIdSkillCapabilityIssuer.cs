using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Issues a request-local NyxID user capability for reading remote skills on
/// behalf of one known external-identity binding. This narrow port does not
/// assert that the binding grants every Aevatar runtime service.
/// </summary>
public interface INyxIdSkillCapabilityIssuer
{
    /// <summary>
    /// Exchanges a known binding for a short-lived user token that can read
    /// remote skills available to the caller.
    /// </summary>
    Task<CapabilityHandle> IssueByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CancellationToken ct = default);
}
