using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans;

/// <summary>
/// Resets stale entries left in the Orleans <c>PubSubRendezvousGrain</c> redis
/// state for an actor's self-stream when the actor itself was already torn down.
///
/// Orleans's normal grain deactivation path tries to unsubscribe from the
/// rendezvous, but ungraceful shutdowns (silo crash, deactivation exceptions
/// the grain swallows as <c>ObjectDisposedException</c>/
/// <c>OrleansMessageRejectionException</c>) and actor type migrations both
/// leave behind state with a non-empty etag. The next silo wave then fails
/// <c>RegisterAsStreamProducer</c> with <c>InconsistentStateException</c>,
/// blocking the projection pipeline that depends on that stream.
///
/// We delete the rendezvous redis key directly so the next producer
/// registration writes from a clean slate.
/// </summary>
internal sealed class OrleansRedisStreamPubSubMaintenance : IStreamPubSubMaintenance
{
    private readonly IConnectionMultiplexer _connection;
    private readonly AevatarOrleansRuntimeOptions _options;
    private readonly string _serviceId;
    private readonly ILogger<OrleansRedisStreamPubSubMaintenance> _logger;

    public OrleansRedisStreamPubSubMaintenance(
        IConnectionMultiplexer connection,
        AevatarOrleansRuntimeOptions options,
        IOptions<ClusterOptions> clusterOptions,
        ILogger<OrleansRedisStreamPubSubMaintenance>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clusterOptions);

        _connection = connection;
        _options = options;
        _serviceId = clusterOptions.Value.ServiceId
            ?? throw new InvalidOperationException(
                "ClusterOptions.ServiceId is required to compute Orleans pub/sub rendezvous redis keys.");
        _logger = logger ?? NullLogger<OrleansRedisStreamPubSubMaintenance>.Instance;
    }

    public async Task<bool> ResetActorStreamPubSubAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        // Orleans Redis grain storage default key format (verified against
        // Microsoft.Orleans.Persistence.Redis 10.0.x DefaultGetStorageKey):
        //   "{grainId}/{serviceId}"
        // PubSubRendezvousGrain id format is:
        //   "pubsubrendezvous/{streamProvider}/{streamNamespace}/{streamKey}"
        // and an actor self-stream uses the actor id as the streamKey.
        var grainId =
            $"pubsubrendezvous/{_options.StreamProviderName}/{_options.ActorEventNamespace}/{actorId}";
        var redisKey = (RedisKey)$"{grainId}/{_serviceId}";

        try
        {
            var deleted = await _connection.GetDatabase().KeyDeleteAsync(redisKey).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation(
                "Orleans pub/sub rendezvous state reset. actorId={ActorId} key={Key} deleted={Deleted}",
                actorId,
                (string)redisKey!,
                deleted);
            return deleted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Orleans pub/sub rendezvous state reset failed. actorId={ActorId} key={Key}",
                actorId,
                (string)redisKey!);
            return false;
        }
    }
}
