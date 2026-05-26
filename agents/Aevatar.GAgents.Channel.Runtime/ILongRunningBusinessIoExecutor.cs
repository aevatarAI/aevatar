namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Creates per-call provider IO lease scopes for actor-owned self-continuations.
/// </summary>
public interface IDisposableProviderIoLeaseFactory
{
    IDisposableProviderIoLease Acquire(string ownerActorId, string operationName, string correlationId);
}

public interface IDisposableProviderIoLease : IDisposable
{
}

// Refactor (iter107/cluster-107-channel-business-io-process-queue):
//   Old pattern: Channel actor records intent, then process-local Channel/Task workers (LongRunningBusinessIoExecutor singleton) own the actual business IO work item and call back later.
//   New principle: Delete the singleton business IO queue; existing owner actor (ConversationGAgent / AgentRunGAgent) uses typed self-continuations + disposable provider IO leases - actor-owned operation state, no process-local fact source.
public sealed class DisposableProviderIoLeaseFactory : IDisposableProviderIoLeaseFactory
{
    public IDisposableProviderIoLease Acquire(string ownerActorId, string operationName, string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return DisposableProviderIoLease.Instance;
    }

    private sealed class DisposableProviderIoLease : IDisposableProviderIoLease
    {
        public static DisposableProviderIoLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
