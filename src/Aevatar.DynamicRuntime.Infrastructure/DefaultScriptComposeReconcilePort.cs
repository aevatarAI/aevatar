using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptComposeReconcilePort : IScriptComposeReconcilePort
{
    private readonly IDynamicRuntimeReadStore _readStore;

    public DefaultScriptComposeReconcilePort(IDynamicRuntimeReadStore readStore)
    {
        _readStore = readStore;
    }

    public Task<ComposeReconcileResult> ReconcileAsync(string stackId, long desiredGeneration, CancellationToken ct = default)
    {
        return ReconcileCoreAsync(stackId, desiredGeneration, ct);
    }

    private async Task<ComposeReconcileResult> ReconcileCoreAsync(string stackId, long desiredGeneration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(stackId))
            return new ComposeReconcileResult(false, 0, "COMPOSE_STACK_INVALID");

        var stack = await _readStore.GetStackAsync(stackId, ct);
        if (stack == null)
            return new ComposeReconcileResult(false, 0, "COMPOSE_STACK_NOT_FOUND");
        if (stack.DesiredGeneration != desiredGeneration)
            return new ComposeReconcileResult(false, stack.ObservedGeneration, "COMPOSE_GENERATION_CONFLICT");

        var services = await _readStore.GetComposeServicesAsync(stackId, ct);
        if (services.Count == 0)
            return new ComposeReconcileResult(stack.ObservedGeneration >= desiredGeneration, stack.ObservedGeneration, stack.ObservedGeneration >= desiredGeneration ? null : "COMPOSE_RECONCILE_PENDING");

        var minObservedGeneration = services.Min(item => item.Generation);
        var allGenerationConverged = services.All(item => item.Generation >= desiredGeneration);
        var allReplicaConverged = services.All(item => item.ReplicasReady >= item.ReplicasDesired);
        var noBlockedRollout = services.All(item =>
            !string.Equals(item.RolloutStatus, "Blocked", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.RolloutStatus, "Error", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.RolloutStatus, "Failed", StringComparison.OrdinalIgnoreCase));

        if (!allGenerationConverged || !allReplicaConverged || !noBlockedRollout)
            return new ComposeReconcileResult(false, Math.Min(stack.ObservedGeneration, minObservedGeneration), "COMPOSE_RECONCILE_PENDING");

        return new ComposeReconcileResult(true, desiredGeneration);
    }
}
