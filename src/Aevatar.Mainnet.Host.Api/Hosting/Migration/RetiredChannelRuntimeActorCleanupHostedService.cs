using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Compatibility;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Scheduled;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Hosting.Migration;

/// <summary>
/// Startup-time cleanup for runtime actors persisted by the retired
/// Aevatar.GAgents.ChannelRuntime assembly.
/// </summary>
public sealed class RetiredChannelRuntimeActorCleanupHostedService : IHostedService
{
    private const string MarkerStreamId = "__maintenance:retired-channelruntime-cleanup:v1";
    private const int MarkerInProgress = 1;
    private const int MarkerCompleted = 2;
    private const string RetiredSkillRunnerType = "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent";
    private const string RetiredWorkflowAgentType = "Aevatar.GAgents.ChannelRuntime.WorkflowAgentGAgent";

    private static readonly RetiredActorTarget[] Targets =
    [
        new(
            "channel-bot-registration-store",
            [
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent",
            ],
            CleanupReadModels: true),
        new(
            "device-registration-store",
            [
                "Aevatar.GAgents.ChannelRuntime.DeviceRegistrationGAgent",
            ],
            CleanupReadModels: true),
        new(
            "agent-registry-store",
            [
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent",
                "Aevatar.GAgents.ChannelRuntime.AgentRegistryGAgent",
            ],
            CleanupReadModels: true),
        new(
            "projection.durable.scope:channel-bot-registration:channel-bot-registration-store",
            [
                "Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationMaterializationContext",
            ],
            SourceStreamId: "channel-bot-registration-store"),
        new(
            "projection.durable.scope:device-registration:device-registration-store",
            [
                "Aevatar.GAgents.ChannelRuntime.DeviceRegistrationMaterializationContext",
            ],
            SourceStreamId: "device-registration-store"),
        new(
            "projection.durable.scope:agent-registry:agent-registry-store",
            [
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogMaterializationContext",
                "Aevatar.GAgents.ChannelRuntime.AgentRegistryMaterializationContext",
            ],
            SourceStreamId: "agent-registry-store"),
    ];

    private readonly IActorTypeProbe _typeProbe;
    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamProvider _streamProvider;
    private readonly IEventStore _eventStore;
    private readonly IEventStoreMaintenance _eventStoreMaintenance;
    private readonly IServiceProvider _services;
    private readonly RetiredChannelRuntimeActorCleanupOptions _options;
    private readonly ILogger<RetiredChannelRuntimeActorCleanupHostedService> _logger;

    public RetiredChannelRuntimeActorCleanupHostedService(
        IActorTypeProbe typeProbe,
        IActorRuntime actorRuntime,
        IStreamProvider streamProvider,
        IEventStore eventStore,
        IEventStoreMaintenance eventStoreMaintenance,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<RetiredChannelRuntimeActorCleanupHostedService> logger)
    {
        _typeProbe = typeProbe ?? throw new ArgumentNullException(nameof(typeProbe));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _eventStoreMaintenance = eventStoreMaintenance ?? throw new ArgumentNullException(nameof(eventStoreMaintenance));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = RetiredChannelRuntimeActorCleanupOptions.FromConfiguration(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Retired ChannelRuntime actor cleanup is disabled.");
            return;
        }

        var lease = await TryAcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        if (lease == null)
            return;

        try
        {
            var userAgentTargets = await DiscoverRetiredCatalogUserAgentTargetsAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var target in userAgentTargets)
            {
                if (!await IsLeaseOwnerAsync(lease, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Retired ChannelRuntime actor cleanup lease lost before processing {ActorId}.",
                        target.ActorId);
                    return;
                }

                await CleanupTargetAsync(target, cancellationToken).ConfigureAwait(false);
            }

            foreach (var target in Targets)
            {
                if (!await IsLeaseOwnerAsync(lease, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Retired ChannelRuntime actor cleanup lease lost before processing {ActorId}.",
                        target.ActorId);
                    return;
                }

                await CleanupTargetAsync(target, cancellationToken).ConfigureAwait(false);
            }

            await CompleteLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Retired ChannelRuntime actor cleanup completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private async Task CleanupTargetAsync(RetiredActorTarget target, CancellationToken ct)
    {
        var runtimeTypeName = await _typeProbe
            .GetRuntimeAgentTypeNameAsync(target.ActorId, ct)
            .ConfigureAwait(false);
        var matchesRetiredRuntimeType = MatchesRetiredType(target, runtimeTypeName);
        var shouldContinueReset = false;
        if (!matchesRetiredRuntimeType)
        {
            if (!string.IsNullOrWhiteSpace(runtimeTypeName))
                return;

            shouldContinueReset = target.ResetWhenRuntimeTypeUnavailable &&
                                  await HasEventStreamAsync(target.ActorId, ct).ConfigureAwait(false);
            if (!shouldContinueReset)
                return;
        }

        if (shouldContinueReset)
        {
            _logger.LogInformation(
                "Retired ChannelRuntime actor stream cleanup continuing after actor state was already cleared. actorId={ActorId}",
                target.ActorId);
        }

        if (!string.IsNullOrWhiteSpace(target.SourceStreamId))
        {
            await _streamProvider
                .GetStream(target.SourceStreamId)
                .RemoveRelayAsync(target.ActorId, ct)
                .ConfigureAwait(false);
        }

        if (target.CleanupReadModels && _options.CleanupReadModels)
            await CleanupReadModelsBestEffortAsync(target.ActorId, ct).ConfigureAwait(false);

        await _actorRuntime.DestroyAsync(target.ActorId, ct).ConfigureAwait(false);
        if (_options.ResetEventStreams)
            await _eventStoreMaintenance.ResetStreamAsync(target.ActorId, ct).ConfigureAwait(false);

        if (!matchesRetiredRuntimeType)
        {
            _logger.LogInformation(
                "Retired ChannelRuntime actor stream cleaned. actorId={ActorId}",
                target.ActorId);
            return;
        }

        _logger.LogInformation(
            "Retired ChannelRuntime actor cleaned. actorId={ActorId} runtimeType={RuntimeType}",
            target.ActorId,
            runtimeTypeName);
    }

    private async Task<IReadOnlyList<RetiredActorTarget>> DiscoverRetiredCatalogUserAgentTargetsAsync(CancellationToken ct)
    {
        var catalogTarget = Targets.Single(static target => target.ActorId == UserAgentCatalogGAgent.WellKnownId);
        var catalogRuntimeTypeName = await _typeProbe
            .GetRuntimeAgentTypeNameAsync(UserAgentCatalogGAgent.WellKnownId, ct)
            .ConfigureAwait(false);
        var catalogIsRetired = MatchesRetiredType(catalogTarget, catalogRuntimeTypeName);
        if (!catalogIsRetired &&
            !(string.IsNullOrWhiteSpace(catalogRuntimeTypeName) &&
              await HasEventStreamAsync(UserAgentCatalogGAgent.WellKnownId, ct).ConfigureAwait(false)))
        {
            return [];
        }

        var agentIds = await DiscoverCatalogUserAgentIdsAsync(ct).ConfigureAwait(false);
        if (agentIds.Count == 0)
            return [];

        return agentIds
            .Select(static actorId => new RetiredActorTarget(
                actorId,
                [RetiredSkillRunnerType, RetiredWorkflowAgentType],
                ResetWhenRuntimeTypeUnavailable: true))
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> DiscoverCatalogUserAgentIdsAsync(CancellationToken ct)
    {
        var events = await _eventStore
            .GetEventsAsync(UserAgentCatalogGAgent.WellKnownId, ct: ct)
            .ConfigureAwait(false);
        if (events.Count == 0)
            return [];

        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in events)
        {
            if (ProtobufContractCompatibility.TryUnpack<UserAgentCatalogUpsertedEvent>(evt.EventData, out var upserted))
                AddCatalogAgentId(agentIds, upserted!.Entry);
            else if (ProtobufContractCompatibility.TryUnpack<UserAgentCatalogTombstonedEvent>(evt.EventData, out var tombstoned))
                AddCatalogAgentId(agentIds, tombstoned!.AgentId);
            else if (ProtobufContractCompatibility.TryUnpack<UserAgentCatalogExecutionUpdatedEvent>(
                         evt.EventData,
                         out var executionUpdated))
                AddCatalogAgentId(agentIds, executionUpdated!.AgentId);
            else if (ProtobufContractCompatibility.TryUnpack<UserAgentCatalogTombstonesCompactedEvent>(
                         evt.EventData,
                         out var compacted))
            {
                foreach (var agentId in compacted!.AgentIds)
                    AddCatalogAgentId(agentIds, agentId);
            }
        }

        return agentIds.ToArray();
    }

    private static void AddCatalogAgentId(HashSet<string> agentIds, UserAgentCatalogEntry? entry)
    {
        if (entry == null)
            return;

        if (IsGeneratedUserAgent(entry.AgentId, entry.AgentType))
            agentIds.Add(entry.AgentId.Trim());
    }

    private static void AddCatalogAgentId(HashSet<string> agentIds, string? agentId)
    {
        if (!IsGeneratedUserAgent(agentId, agentType: null))
            return;

        agentIds.Add(agentId!.Trim());
    }

    private static bool IsGeneratedUserAgent(string? agentId, string? agentType)
    {
        var normalizedId = agentId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedId))
            return false;

        if (string.Equals(agentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal) ||
            string.Equals(agentType, WorkflowAgentDefaults.AgentType, StringComparison.Ordinal))
        {
            return true;
        }

        return normalizedId.StartsWith($"{SkillRunnerDefaults.ActorIdPrefix}-", StringComparison.Ordinal) ||
               normalizedId.StartsWith($"{WorkflowAgentDefaults.ActorIdPrefix}-", StringComparison.Ordinal);
    }

    private async Task<bool> HasEventStreamAsync(string actorId, CancellationToken ct) =>
        await _eventStore.GetVersionAsync(actorId, ct).ConfigureAwait(false) > 0;

    private async Task CleanupReadModelsBestEffortAsync(string actorId, CancellationToken ct)
    {
        try
        {
            await CleanupReadModelsAsync(actorId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Retired ChannelRuntime read-model cleanup failed and will be skipped. actorId={ActorId}",
                actorId);
        }
    }

    private async Task CleanupReadModelsAsync(string actorId, CancellationToken ct)
    {
        await DeleteDocumentsForActorAsync<ChannelBotRegistrationDocument>(actorId, ct).ConfigureAwait(false);
        await DeleteDocumentsForActorAsync<DeviceRegistrationDocument>(actorId, ct).ConfigureAwait(false);
        await DeleteDocumentsForActorAsync<UserAgentCatalogDocument>(actorId, ct).ConfigureAwait(false);
        await DeleteDocumentsForActorAsync<UserAgentCatalogNyxCredentialDocument>(actorId, ct).ConfigureAwait(false);
    }

    private async Task DeleteDocumentsForActorAsync<TReadModel>(string actorId, CancellationToken ct)
        where TReadModel : class, IProjectionReadModel
    {
        var reader = _services.GetService<IProjectionDocumentReader<TReadModel, string>>();
        var writer = _services.GetService<IProjectionWriteDispatcher<TReadModel>>();
        if (reader == null || writer == null)
            return;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var result = await reader.QueryAsync(
                new ProjectionDocumentQuery
                {
                    Cursor = cursor,
                    Take = _options.ReadModelCleanupPageSize,
                    Filters =
                    [
                        new ProjectionDocumentFilter
                        {
                            FieldPath = nameof(IProjectionReadModel.ActorId),
                            Operator = ProjectionDocumentFilterOperator.Eq,
                            Value = ProjectionDocumentValue.FromString(actorId),
                        },
                    ],
                },
                ct).ConfigureAwait(false);

            foreach (var item in result.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Id))
                    ids.Add(item.Id);
            }

            cursor = result.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        foreach (var id in ids)
            await writer.DeleteAsync(id, ct).ConfigureAwait(false);
    }

    private async Task<CleanupLease?> TryAcquireLeaseAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_options.InProgressTimeoutSeconds);
        var pollDelay = TimeSpan.FromMilliseconds(_options.WaitPollMilliseconds);

        while (true)
        {
            var marker = await ReadMarkerAsync(ct).ConfigureAwait(false);
            if (marker.Latest?.Status == MarkerCompleted)
            {
                _logger.LogInformation("Retired ChannelRuntime actor cleanup already completed.");
                return null;
            }

            if (marker.Latest == null || IsStale(marker.Latest, timeout))
            {
                var lease = new CleanupLease(Guid.NewGuid().ToString("N"));
                try
                {
                    await AppendMarkerAsync(
                        MarkerInProgress,
                        lease.Token,
                        marker.CurrentVersion,
                        ct).ConfigureAwait(false);
                    return lease;
                }
                catch (EventStoreOptimisticConcurrencyException)
                {
                    continue;
                }
            }

            await Task.Delay(pollDelay, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsLeaseOwnerAsync(CleanupLease lease, CancellationToken ct)
    {
        var marker = await ReadMarkerAsync(ct).ConfigureAwait(false);
        return marker.Latest is { Status: MarkerInProgress } latest &&
               string.Equals(latest.Token, lease.Token, StringComparison.Ordinal);
    }

    private async Task CompleteLeaseAsync(CleanupLease lease, CancellationToken ct)
    {
        var marker = await ReadMarkerAsync(ct).ConfigureAwait(false);
        if (marker.Latest?.Status == MarkerCompleted)
            return;

        if (marker.Latest is not { Status: MarkerInProgress } latest ||
            !string.Equals(latest.Token, lease.Token, StringComparison.Ordinal))
        {
            _logger.LogWarning("Retired ChannelRuntime actor cleanup lease was lost before completion.");
            return;
        }

        await AppendMarkerAsync(
            MarkerCompleted,
            lease.Token,
            marker.CurrentVersion,
            ct).ConfigureAwait(false);
    }

    private async Task<MarkerSnapshot> ReadMarkerAsync(CancellationToken ct)
    {
        var events = await _eventStore.GetEventsAsync(MarkerStreamId, ct: ct).ConfigureAwait(false);
        MarkerRecord? latest = null;
        foreach (var evt in events)
        {
            var parsed = TryParseMarker(evt);
            if (parsed != null)
                latest = parsed;
        }

        var version = events.Count > 0 ? events[^1].Version : 0;
        return new MarkerSnapshot(version, latest);
    }

    private async Task AppendMarkerAsync(
        int status,
        string token,
        long expectedVersion,
        CancellationToken ct)
    {
        await _eventStore.AppendAsync(
            MarkerStreamId,
            [
                new StateEvent
                {
                    AgentId = MarkerStreamId,
                    EventId = token,
                    EventType = Int32Value.Descriptor.FullName,
                    EventData = Any.Pack(new Int32Value { Value = status }),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = expectedVersion + 1,
                },
            ],
            expectedVersion,
            ct).ConfigureAwait(false);
    }

    private static MarkerRecord? TryParseMarker(StateEvent evt)
    {
        if (evt.EventData == null || !evt.EventData.TryUnpack<Int32Value>(out var status))
            return null;

        return new MarkerRecord(
            status.Value,
            evt.EventId ?? string.Empty,
            evt.Timestamp?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            evt.Version);
    }

    private static bool IsStale(MarkerRecord marker, TimeSpan timeout)
    {
        if (marker.Status != MarkerInProgress)
            return true;

        return DateTimeOffset.UtcNow - marker.Timestamp > timeout;
    }

    private static bool MatchesRetiredType(RetiredActorTarget target, string? runtimeTypeName)
    {
        if (string.IsNullOrWhiteSpace(runtimeTypeName))
            return false;

        return target.RetiredTypeTokens.Any(token =>
            ContainsTypeNameToken(runtimeTypeName, token));
    }

    private static bool ContainsTypeNameToken(string runtimeTypeName, string token)
    {
        var startIndex = 0;
        while (startIndex < runtimeTypeName.Length)
        {
            var index = runtimeTypeName.IndexOf(token, startIndex, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var beforeOk = index == 0 || IsTypeNameBoundary(runtimeTypeName[index - 1]);
            var afterIndex = index + token.Length;
            var afterOk = afterIndex == runtimeTypeName.Length || IsTypeNameBoundary(runtimeTypeName[afterIndex]);
            if (beforeOk && afterOk)
                return true;

            startIndex = index + token.Length;
        }

        return false;
    }

    private static bool IsTypeNameBoundary(char value) =>
        value is '[' or ']' or ',' or ' ';

    private sealed record RetiredActorTarget(
        string ActorId,
        IReadOnlyList<string> RetiredTypeTokens,
        string? SourceStreamId = null,
        bool CleanupReadModels = false,
        bool ResetWhenRuntimeTypeUnavailable = true);

    private sealed record CleanupLease(string Token);

    private sealed record MarkerRecord(
        int Status,
        string Token,
        DateTimeOffset Timestamp,
        long Version);

    private sealed record MarkerSnapshot(long CurrentVersion, MarkerRecord? Latest);
}
