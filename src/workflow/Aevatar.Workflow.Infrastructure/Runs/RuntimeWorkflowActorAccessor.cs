using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal sealed class RuntimeWorkflowActorAccessor
{
    private readonly IActorRuntime _runtime;

    public RuntimeWorkflowActorAccessor(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<IActor?> GetAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return _runtime.GetAsync(actorId);
    }
}
