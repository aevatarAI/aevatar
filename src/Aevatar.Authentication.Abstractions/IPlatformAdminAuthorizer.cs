namespace Aevatar.Authentication.Abstractions;

// 06-20-observatory-admin-cross-scope (G1/G3): provider-agnostic platform-admin authorization seam.
//   Consumers (e.g. the workflow observatory endpoints) depend on this abstraction, not on any NyxID type, to
//   decide whether a caller may view runs across scopes. The only authoritative source of "is this caller a
//   platform admin" is the identity provider, reached server-side with the caller's own bearer; the contract is
//   fail-closed: a caller is elevated ONLY when the provider positively confirms an eligible platform role.
public interface IPlatformAdminAuthorizer
{
    /// <summary>
    /// Resolves the caller's platform identity from the bearer token. MUST fail closed:
    /// returns a non-elevated <see cref="PlatformCaller"/> on a missing/blank token, any provider error,
    /// a malformed response, or a missing/ineligible role. Only <see cref="OperationCanceledException"/>
    /// (the caller aborted) propagates.
    /// </summary>
    Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default);
}

// Result of resolving a caller's platform identity. IsElevated == the role grants cross-scope viewing.
public sealed record PlatformCaller(bool IsElevated, string Role, string Email, string UserId)
{
    public static PlatformCaller NotElevated { get; } = new(false, string.Empty, string.Empty, string.Empty);
}
