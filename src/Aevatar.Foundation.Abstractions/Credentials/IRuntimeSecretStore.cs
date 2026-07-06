namespace Aevatar.Foundation.Abstractions.Credentials;

public interface IRuntimeSecretStore
{
    Task<StoreRuntimeSecretResult> PutAsync(StoreRuntimeSecretRequest request, CancellationToken ct = default);

    Task<ResolveRuntimeSecretResult> ResolveAsync(ResolveRuntimeSecretRequest request, CancellationToken ct = default);

    Task<ConsumeRuntimeSecretResult> ConsumeAsync(ConsumeRuntimeSecretRequest request, CancellationToken ct = default);

    Task<RevokeRuntimeSecretResult> RevokeAsync(RevokeRuntimeSecretRequest request, CancellationToken ct = default);
}

public sealed record StoreRuntimeSecretRequest(
    string Purpose,
    string OwnerRunId,
    string OwnerStepId,
    string Secret,
    TimeSpan TimeToLive,
    bool ConsumeOnce,
    string AuditReason);

public sealed record StoreRuntimeSecretResult(RuntimeSecretReference Reference);

public sealed record ResolveRuntimeSecretRequest(
    string Ref,
    string Purpose,
    string OwnerRunId,
    string OwnerStepId,
    string AuditReason);

public sealed record ResolveRuntimeSecretResult(
    RuntimeSecretReference? Reference,
    string? Secret)
{
    public bool Resolved => Secret is not null;
}

public sealed record ConsumeRuntimeSecretRequest(
    string Ref,
    string Purpose,
    string OwnerRunId,
    string OwnerStepId,
    string AuditReason);

public sealed record ConsumeRuntimeSecretResult(bool Consumed);

public sealed record RevokeRuntimeSecretRequest(
    string Ref,
    string Purpose,
    string OwnerRunId,
    string OwnerStepId,
    string AuditReason);

public sealed record RevokeRuntimeSecretResult(bool Revoked);
