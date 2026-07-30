namespace Aevatar.AI.Application.CodexExecution;

public interface IManagedCodexCredentialMutationLeaseHandle : IAsyncDisposable;

public interface IManagedCodexCredentialMutationLease
{
    ValueTask<IManagedCodexCredentialMutationLeaseHandle?> TryAcquireAsync(
        string ownerKey,
        CancellationToken ct = default);
}
