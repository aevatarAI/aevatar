using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class OrleansStreamProviderAdapter : Aevatar.Foundation.Abstractions.IStreamProvider
{
    private readonly IClusterClient _clusterClient;
    private readonly AevatarOrleansRuntimeOptions _options;
    private readonly IStreamForwardingRegistry _forwardingRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, OrleansActorStream> _streams = new(StringComparer.Ordinal);

    public OrleansStreamProviderAdapter(
        IClusterClient clusterClient,
        AevatarOrleansRuntimeOptions options,
        IStreamForwardingRegistry? forwardingRegistry = null,
        ILoggerFactory? loggerFactory = null)
    {
        _clusterClient = clusterClient;
        _options = options;
        _forwardingRegistry = forwardingRegistry ?? NoOpForwardingRegistry.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IStream GetStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return _streams.GetOrAdd(actorId, id =>
        {
            var provider = _clusterClient.GetStreamProvider(_options.StreamProviderName);
            return new OrleansActorStream(
                id,
                _options.ActorEventNamespace,
                provider,
                _forwardingRegistry,
                _loggerFactory.CreateLogger<OrleansActorStream>());
        });
    }

    public void RemoveStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _streams.TryRemove(actorId, out _);
    }

    private sealed class NoOpForwardingRegistry : IStreamForwardingRegistry
    {
        public static NoOpForwardingRegistry Instance { get; } = new();

        public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            _ = binding;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            _ = sourceStreamId;
            _ = targetStreamId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(string sourceStreamId, CancellationToken ct = default)
        {
            _ = sourceStreamId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }
    }
}
