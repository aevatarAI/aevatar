namespace Aevatar.Foundation.Abstractions.Credentials;

public interface ISecretVault
{
    Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default);

    Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default);

    Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default);

    Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default);
}

public sealed record StoreSecretRequest(
    string Purpose,
    string OwnerScopeKey,
    string SubjectId,
    string Secret,
    string AuditReason,
    DateTimeOffset? ExpiresAt = null);

public sealed record StoreSecretResult(SecretReference Reference);

public sealed record ResolveSecretRequest(
    string Ref,
    string Purpose,
    string OwnerScopeKey,
    string SubjectId,
    string AuditReason);

public sealed record ResolveSecretResult(
    SecretReference? Reference,
    string? Secret)
{
    public bool Resolved => Secret is not null;
}

public sealed record RotateSecretRequest(
    string Ref,
    string Purpose,
    string OwnerScopeKey,
    string SubjectId,
    string Secret,
    string AuditReason);

public sealed record RotateSecretResult(SecretReference Reference);

public sealed record RevokeSecretRequest(
    string Ref,
    string Purpose,
    string OwnerScopeKey,
    string SubjectId,
    string AuditReason);

public sealed record RevokeSecretResult(bool Revoked);
