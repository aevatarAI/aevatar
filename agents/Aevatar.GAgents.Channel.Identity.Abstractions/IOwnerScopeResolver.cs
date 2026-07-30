using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Resolves an external entry subject to the canonical business-data owner
/// scope using the identity binding read model. Callers must keep
/// <see cref="ExternalSubjectRef"/> for routing/audit and use the returned
/// <see cref="OwnerScopeId"/> only as the shared user data key.
/// </summary>
public interface IOwnerScopeResolver
{
    /// <summary>
    /// Returns the canonical owner scope for an active binding, or
    /// <c>null</c> when the subject has no materialized binding.
    /// </summary>
    Task<OwnerScopeId?> ResolveAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default);
}
