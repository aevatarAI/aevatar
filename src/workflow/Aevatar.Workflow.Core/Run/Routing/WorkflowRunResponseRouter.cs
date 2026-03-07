using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunResponseRouter
{
    private readonly WorkflowRunCapabilityRegistry _registry;
    private readonly WorkflowRunReadContext _read;
    private readonly WorkflowRunWriteContext _write;
    private readonly WorkflowRunEffectPorts _effects;

    public WorkflowRunResponseRouter(
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
        EventEnvelope envelope,
        string defaultPublisherId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var handlers = _registry.GetResponseCandidates(envelope.Payload)
            .Where(x => x.CanHandleResponse(envelope, defaultPublisherId, _read))
            .ToArray();
        if (handlers.Length == 0)
            return false;
        if (handlers.Length > 1)
            throw new InvalidOperationException(
                $"Multiple workflow run capabilities matched response '{envelope.Payload?.TypeUrl ?? "(none)"}'.");

        await handlers[0].HandleResponseAsync(envelope, defaultPublisherId, _read, _write, _effects, ct);
        return true;
    }
}
