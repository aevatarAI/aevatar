using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

public sealed class KafkaPartitionAwareQueueAdapterFactory : IQueueAdapterFactory
{
    private readonly string _providerName;
    private readonly IQueueAdapterCache _cache;
    private readonly StrictQueuePartitionMapper _mapper;
    private readonly KafkaPartitionAwareQueueAdapter _adapter;
    private static readonly IStreamFailureHandler NoOpFailureHandler = new NoOpStreamDeliveryFailureHandler();

    [ActivatorUtilitiesConstructor]
    public KafkaPartitionAwareQueueAdapterFactory(
        AevatarOrleansRuntimeOptions runtimeOptions,
        IServiceProvider serviceProvider)
    {
        _providerName = runtimeOptions.StreamProviderName;
        _mapper = new StrictQueuePartitionMapper(
            _providerName,
            Math.Max(1, runtimeOptions.QueueCount));

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

        var cacheOptions = new SimpleQueueCacheOptions
        {
            CacheSize = Math.Max(128, runtimeOptions.QueueCacheSize),
        };
        _cache = new SimpleQueueAdapterCache(cacheOptions, _providerName, loggerFactory);

        var actorEventNamespace = string.IsNullOrWhiteSpace(runtimeOptions.ActorEventNamespace)
            ? OrleansRuntimeConstants.ActorEventStreamNamespace
            : runtimeOptions.ActorEventNamespace;

        _adapter = new KafkaPartitionAwareQueueAdapter(
            _providerName,
            () => serviceProvider.GetRequiredService<IKafkaPartitionAwareEnvelopeTransport>(),
            () => serviceProvider.GetRequiredService<LocalPartitionRecordRouter>(),
            _mapper,
            actorEventNamespace);
    }

    public Task<IQueueAdapter> CreateAdapter() =>
        Task.FromResult<IQueueAdapter>(_adapter);

    public IQueueAdapterCache GetQueueAdapterCache() =>
        _cache;

    public IStreamQueueMapper GetStreamQueueMapper() =>
        _mapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
    {
        _ = queueId;
        return Task.FromResult(NoOpFailureHandler);
    }
}
