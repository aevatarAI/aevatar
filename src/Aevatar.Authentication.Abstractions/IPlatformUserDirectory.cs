namespace Aevatar.Authentication.Abstractions;

// 06-20-observatory-admin-cross-scope (G6): provider-agnostic directory used only to RESOLVE an email to a
//   candidate scope id (= the identity provider's user id). This is "resolve the user id and use it as the
//   scope id", NOT "all runs by this user" — a run's scope_id is not guaranteed to equal the creator's user id.
public interface IPlatformUserDirectory
{
    /// <summary>
    /// Resolves candidate users for an email via the identity provider's admin search. Fail-closed:
    /// returns an empty list on a missing/blank token or email, any provider error, or a malformed response.
    /// Only <see cref="OperationCanceledException"/> propagates. The caller must already be authorized (the
    /// underlying provider endpoint is itself admin-gated).
    /// </summary>
    Task<IReadOnlyList<PlatformUserMatch>> SearchByEmailAsync(string bearerToken, string email, CancellationToken ct = default);
}

// A candidate scope resolved from an email. ScopeId == the identity provider user id (the convention used for
// nyxid-native runs).
public sealed record PlatformUserMatch(string ScopeId, string Email, string Role);
