using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

internal sealed class OrleansMassTransitQueueAdapterFactory : IQueueAdapterFactory
{
    private readonly string _providerName;
    private readonly IQueueAdapterCache _cache;
    private readonly IStreamQueueMapper _queueMapper;
    private readonly OrleansMassTransitQueueAdapter _adapter;
    private static readonly IStreamFailureHandler NoOpFailureHandler = new NoOpStreamDeliveryFailureHandler();

    [ActivatorUtilitiesConstructor]
    public OrleansMassTransitQueueAdapterFactory(
        AevatarOrleansRuntimeOptions runtimeOptions,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
        : this(
            runtimeOptions,
            () => serviceProvider.GetRequiredService<IMassTransitEnvelopeTransport>(),
            loggerFactory)
    {
    }

    private OrleansMassTransitQueueAdapterFactory(
        AevatarOrleansRuntimeOptions runtimeOptions,
        Func<IMassTransitEnvelopeTransport> resolveTransport,
        ILoggerFactory loggerFactory)
    {
        _providerName = runtimeOptions.StreamProviderName;

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
        _adapter = new OrleansMassTransitQueueAdapter(
            _providerName,
            resolveTransport,
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
