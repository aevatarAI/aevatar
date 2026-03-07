using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunChildCompletionRouter
{
    private readonly WorkflowRunCapabilityRegistry _registry;
    private readonly WorkflowRunReadContext _read;
    private readonly WorkflowRunWriteContext _write;
    private readonly WorkflowRunEffectPorts _effects;

    public WorkflowRunChildCompletionRouter(
        WorkflowRunCapabilityRegistry registry,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    public async Task<bool> TryHandleAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        CancellationToken ct)
    {
        var handlers = _registry.Capabilities
            .Where(x => x.CanHandleChildRunCompletion(evt, publisherActorId, _read))
            .ToArray();
        if (handlers.Length == 0)
            return false;
        if (handlers.Length > 1)
            throw new InvalidOperationException(
                $"Multiple workflow run capabilities matched child completion '{evt.RunId}'.");

        await handlers[0].HandleChildRunCompletionAsync(evt, publisherActorId, _read, _write, _effects, ct);
        return true;
    }
}
