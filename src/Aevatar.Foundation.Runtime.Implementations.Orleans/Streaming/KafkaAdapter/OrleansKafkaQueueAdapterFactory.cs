using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.KafkaAdapter;

internal sealed class OrleansKafkaQueueAdapterFactory : IQueueAdapterFactory
{
    private readonly string _providerName;
    private readonly MassTransitKafkaTransportOptions _transportOptions;
    private readonly IKafkaEnvelopeTransport _transport;
    private readonly IQueueAdapterCache _cache;
    private readonly IStreamQueueMapper _queueMapper;
    private readonly OrleansKafkaQueueAdapter _adapter;
    private static readonly IStreamFailureHandler NoOpFailureHandler = new NoOpStreamDeliveryFailureHandler();

    public OrleansKafkaQueueAdapterFactory(
        AevatarOrleansRuntimeOptions runtimeOptions,
        MassTransitKafkaTransportOptions transportOptions,
        IKafkaEnvelopeTransport transport,
        ILoggerFactory loggerFactory)
    {
        _providerName = runtimeOptions.StreamProviderName;
        _transportOptions = transportOptions;
        _transport = transport;

        var mapperOptions = new HashRingStreamQueueMapperOptions
        {
            TotalQueueCount = Math.Max(1, runtimeOptions.QueueCount),
        };
        _queueMapper = new HashRingBasedStreamQueueMapper(mapperOptions, _providerName);

        var cacheOptions = new SimpleQueueCacheOptions
        {
            CacheSize = Math.Max(128, runtimeOptions.QueueCacheSize),
        };
        _cache = new SimpleQueueAdapterCache(cacheOptions, _providerName, loggerFactory);
        var actorEventNamespace = string.IsNullOrWhiteSpace(runtimeOptions.ActorEventNamespace)
            ? OrleansRuntimeConstants.ActorEventStreamNamespace
            : runtimeOptions.ActorEventNamespace;
        _adapter = new OrleansKafkaQueueAdapter(
            _providerName,
            _transportOptions.TopicName,
            _transport,
            _queueMapper,
            actorEventNamespace);
    }

    public Task<IQueueAdapter> CreateAdapter() =>
        Task.FromResult<IQueueAdapter>(_adapter);

    public IQueueAdapterCache GetQueueAdapterCache() =>
        _cache;

    public IStreamQueueMapper GetStreamQueueMapper() =>
        _queueMapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
    {
        _ = queueId;
        return Task.FromResult(NoOpFailureHandler);
    }
}
