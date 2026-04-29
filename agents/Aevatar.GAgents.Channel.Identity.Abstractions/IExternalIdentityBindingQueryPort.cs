using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Read-side port for resolving an external subject to its active binding.
/// Reads the projection (current-state readmodel) only — no event-store replay,
/// no actor state mirror, no query-time priming (CLAUDE.md).
/// See ADR-0017 §Projection Readiness.
/// </summary>
public interface IExternalIdentityBindingQueryPort
{
    /// <summary>
    /// Returns the active <see cref="BindingId"/> for the given external subject,
    /// or <c>null</c> when no active binding is materialized in the readmodel.
    /// A miss MUST drive the caller to prompt the sender to <c>/init</c>;
    /// callers MUST NOT fall back to bot-owner credentials or any cached token.
    /// </summary>
    Task<BindingId?> ResolveAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default);
}
