using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

public sealed class KafkaProviderQueueAdapterFactory : IQueueAdapterFactory
{
    private readonly IQueueAdapterCache _cache;
    private readonly KafkaProviderQueueAdapter _adapter;
    private static readonly IStreamFailureHandler NoOpFailureHandler = new NoOpStreamDeliveryFailureHandler();

    [ActivatorUtilitiesConstructor]
    public KafkaProviderQueueAdapterFactory(
        AevatarOrleansRuntimeOptions runtimeOptions,
        KafkaProviderProducer transport,
        KafkaProviderTransportOptions transportOptions,
        ILoggerFactory? loggerFactory = null)
    {
        var providerName = runtimeOptions.StreamProviderName;
        var mapper = new KafkaQueuePartitionMapper(
            providerName,
            Math.Max(1, runtimeOptions.QueueCount));

        var cacheOptions = new SimpleQueueCacheOptions
        {
            CacheSize = Math.Max(128, runtimeOptions.QueueCacheSize),
        };
        _cache = new SimpleQueueAdapterCache(cacheOptions, providerName, loggerFactory ?? NullLoggerFactory.Instance);

        var actorEventNamespace = string.IsNullOrWhiteSpace(runtimeOptions.ActorEventNamespace)
            ? OrleansRuntimeConstants.ActorEventStreamNamespace
            : runtimeOptions.ActorEventNamespace;

        _adapter = new KafkaProviderQueueAdapter(
            providerName,
            transport,
            transportOptions,
            mapper,
            actorEventNamespace);
    }

    public Task<IQueueAdapter> CreateAdapter() =>
        Task.FromResult<IQueueAdapter>(_adapter);

    public IQueueAdapterCache GetQueueAdapterCache() =>
        _cache;

    public IStreamQueueMapper GetStreamQueueMapper() =>
        _adapter.Mapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
    {
        _ = queueId;
        return Task.FromResult(NoOpFailureHandler);
    }
}
