using System.Collections.Concurrent;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.App.Application.Projection.Orchestration;

public sealed class AppProjectionManager : IAppProjectionManager
{
    private readonly IProjectionLifecycleService<AppProjectionContext, object?> _lifecycle;
    private readonly IAppProjectionContextFactory _contextFactory;
    private readonly ILogger<AppProjectionManager>? _logger;
    private readonly ConcurrentDictionary<string, AppProjectionContext> _contexts = new(StringComparer.Ordinal);

    public AppProjectionManager(
        IProjectionLifecycleService<AppProjectionContext, object?> lifecycle,
        IAppProjectionContextFactory contextFactory,
        ILogger<AppProjectionManager>? logger = null)
    {
        _lifecycle = lifecycle;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task EnsureSubscribedAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        if (_contexts.ContainsKey(actorId))
            return;

        var context = _contextFactory.Create(actorId);
        if (!_contexts.TryAdd(actorId, context))
            return;

        try
        {
            await _lifecycle.StartAsync(context, ct);
            _logger?.LogDebug("App projection subscribed for actor {ActorId}.", actorId);
        }
        catch
        {
            _contexts.TryRemove(actorId, out _);
            throw;
        }
    }

    public async Task UnsubscribeAsync(string actorId, CancellationToken ct = default)
    {
        if (!_contexts.TryRemove(actorId, out var context))
            return;

        await _lifecycle.StopAsync(context, ct);
        _logger?.LogDebug("App projection unsubscribed for actor {ActorId}.", actorId);
    }
}
