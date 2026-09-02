namespace Aevatar.GAgents.StatusDashboard.Executors;

/// <summary>
/// Mints a short-lived bearer for the host's own authenticated endpoints (scope-scoped reads,
/// observatory) so a probe can assert real success (200) instead of an auth-boundary 401 — per
/// the status-dashboard canon, a 401 is not a health signal.
///
/// Implemented host-side because it depends on the scope service token issuer; the agent module
/// only depends on this narrow abstraction (DIP). Returns <c>null</c> when it cannot mint a token
/// (e.g. scope service tokens are disabled), which the resolver surfaces as
/// <see cref="ProbeCredentialUnavailableException"/> so the probe degrades to <c>unknown</c>
/// rather than a false <c>down</c>.
/// </summary>
public interface IProbeServiceTokenProvider
{
    Task<string?> GetScopeBearerAsync(string scopeId, CancellationToken ct);
}

/// <summary>
/// Raised when a probe requires a credential that is not configured/available. The HTTP executor
/// maps it to an <c>unknown</c> outcome — never a false <c>down</c>, never an unauthenticated
/// request that would 401.
/// </summary>
public sealed class ProbeCredentialUnavailableException : Exception
{
    public ProbeCredentialUnavailableException(string message)
        : base(message)
    {
    }
}
