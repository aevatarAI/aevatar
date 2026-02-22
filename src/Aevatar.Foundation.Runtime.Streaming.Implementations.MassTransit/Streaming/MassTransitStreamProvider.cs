using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

public sealed class MassTransitStreamProvider : Aevatar.Foundation.Abstractions.IStreamProvider
{
    private readonly IMassTransitEnvelopeTransport _transport;
    private readonly MassTransitStreamOptions _options;
    private readonly IStreamForwardingRegistry _forwardingRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, MassTransitStream> _streams = new(StringComparer.Ordinal);

    public MassTransitStreamProvider(
        IMassTransitEnvelopeTransport transport,
        MassTransitStreamOptions options,
        IStreamForwardingRegistry? forwardingRegistry = null,
        ILoggerFactory? loggerFactory = null)
    {
        _transport = transport;
        _options = options;
        _forwardingRegistry = forwardingRegistry ?? NoOpForwardingRegistry.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IStream GetStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return _streams.GetOrAdd(actorId, id =>
            new MassTransitStream(
                id,
                _options.StreamNamespace,
                _transport,
                _forwardingRegistry,
                _loggerFactory.CreateLogger<MassTransitStream>()));
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
