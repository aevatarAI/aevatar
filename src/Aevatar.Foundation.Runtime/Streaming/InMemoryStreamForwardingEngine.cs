using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Streaming;

internal sealed class InMemoryStreamForwardingEngine
{
    private readonly IStreamForwardingBindingSource _registry;
    private readonly Func<string, IStream> _resolveStream;
    private readonly ILogger _logger;

    public InMemoryStreamForwardingEngine(
        IStreamForwardingBindingSource registry,
        Func<string, IStream> resolveStream,
        ILogger logger)
    {
        _registry = registry;
        _resolveStream = resolveStream;
        _logger = logger;
    }

    public async Task ForwardAsync(string sourceStreamId, EventEnvelope envelope)
    {
        List<Task>? forwardingTasks = null;
        var bindings = _registry.GetBindings(sourceStreamId);
        foreach (var binding in bindings)
        {
            if (!StreamForwardingRules.TryBuildForwardedEnvelope(
                    sourceStreamId,
                    binding,
                    envelope,
                    out var forwarded) ||
                forwarded == null)
            {
                continue;
            }

            forwardingTasks ??= new List<Task>();
            forwardingTasks.Add(ForwardToTargetAsync(sourceStreamId, binding.TargetStreamId, forwarded));
        }

        if (forwardingTasks is { Count: > 0 })
        {
            await Task.WhenAll(forwardingTasks);
        }
    }

    private async Task ForwardToTargetAsync(string sourceStreamId, string targetStreamId, EventEnvelope forwarded)
    {
        try
        {
            await _resolveStream(targetStreamId).ProduceAsync(forwarded, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Stream forwarding failed. source={SourceStreamId}, target={TargetStreamId}",
                sourceStreamId,
                targetStreamId);
        }
    }
}
