namespace Aevatar.Interop.A2A.Abstractions;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: task ids indexed a process-local ledger.
//   New principle: task id deterministically addresses the task-scoped GAgent owner.
public static class A2ATaskActorId
{
    private const string Prefix = "a2a.task:";

    public static string Build(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return Prefix + Uri.EscapeDataString(taskId.Trim());
    }
}
