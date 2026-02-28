using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptComposeReconcilePort : IScriptComposeReconcilePort
{
    public Task<ComposeReconcileResult> ReconcileAsync(string stackId, long desiredGeneration, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = stackId;
        return Task.FromResult(new ComposeReconcileResult(true, desiredGeneration));
    }
}
