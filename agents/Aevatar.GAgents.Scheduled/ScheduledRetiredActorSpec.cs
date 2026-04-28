using System.Runtime.CompilerServices;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Compatibility;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Retired-actor declaration for the user-agent catalog and the generated
/// skill-runner / workflow-agent actors previously hosted by the deleted
/// <c>Aevatar.GAgents.ChannelRuntime</c> assembly.
///
/// Dynamic discovery is gated on the catalog itself looking retired
/// (matches a retired runtime-type token, or runtime type unavailable but
/// stream still has events). On a fully-migrated cluster this gate keeps
/// the catalog walk a no-op even though the cleanup runs every startup.
///
/// When the gate fires, generated agent ids are read from the
/// <see cref="UserAgentCatalogDocument"/> read model first (survives event
/// stream snapshot+compaction), and merged with any catalog upsert events
/// not yet projected. Without the read-model path, snapshotted entries
/// would be silently dropped after compaction.
/// </summary>
public sealed class ScheduledRetiredActorSpec : RetiredActorSpec
{
    private const string RetiredSkillRunnerType = "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent";
    private const string RetiredWorkflowAgentType = "Aevatar.GAgents.ChannelRuntime.WorkflowAgentGAgent";
    private const int ReadModelPageSize = 500;

    public override string SpecId => "scheduled";

    public override IReadOnlyList<RetiredActorTarget> Targets { get; } =
    [
        new(
            UserAgentCatalogGAgent.WellKnownId,
            [
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent",
                "Aevatar.GAgents.ChannelRuntime.AgentRegistryGAgent",
            ],
            CleanupReadModels: true),
        new(
            $"projection.durable.scope:agent-registry:{UserAgentCatalogGAgent.WellKnownId}",
            [
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogMaterializationContext",
                "Aevatar.GAgents.ChannelRuntime.AgentRegistryMaterializationContext",
            ],
            SourceStreamId: UserAgentCatalogGAgent.WellKnownId),
    ];

    public override async IAsyncEnumerable<RetiredActorTarget> DiscoverDynamicTargetsAsync(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var typeProbe = services.GetRequiredService<IActorTypeProbe>();
        var eventStore = services.GetRequiredService<IEventStore>();
        var logger = services.GetService<ILogger<ScheduledRetiredActorSpec>>()
                     ?? NullLogger<ScheduledRetiredActorSpec>.Instance;

        if (!await ShouldDiscoverFromCatalogAsync(typeProbe, eventStore, ct).ConfigureAwait(false))
            yield break;

        var agentIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var actorId in await DiscoverFromReadModelBestEffortAsync(services, logger, ct).ConfigureAwait(false))
            agentIds.Add(actorId);

        foreach (var actorId in await DiscoverFromCatalogEventsAsync(eventStore, ct).ConfigureAwait(false))
            agentIds.Add(actorId);

        foreach (var actorId in agentIds)
        {
            yield return new RetiredActorTarget(
                actorId,
                [RetiredSkillRunnerType, RetiredWorkflowAgentType],
                ResetWhenRuntimeTypeUnavailable: true);
        }
    }

    public override async Task DeleteReadModelsForActorAsync(
        IServiceProvider services,
        string actorId,
        CancellationToken ct)
    {
        await RetiredActorReadModelHelpers
            .DeleteByActorAsync<UserAgentCatalogDocument>(services, actorId, ct)
            .ConfigureAwait(false);
        await RetiredActorReadModelHelpers
            .DeleteByActorAsync<UserAgentCatalogNyxCredentialDocument>(services, actorId, ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> ShouldDiscoverFromCatalogAsync(
        IActorTypeProbe typeProbe,
        IEventStore eventStore,
        CancellationToken ct)
    {
        var catalogTarget = Targets.First(static target =>
            target.ActorId == UserAgentCatalogGAgent.WellKnownId);

        var runtimeTypeName = await typeProbe
            .GetRuntimeAgentTypeNameAsync(UserAgentCatalogGAgent.WellKnownId, ct)
            .ConfigureAwait(false);

        if (catalogTarget.MatchesRuntimeType(runtimeTypeName))
            return true;

        if (string.IsNullOrWhiteSpace(runtimeTypeName))
        {
            var version = await eventStore
                .GetVersionAsync(UserAgentCatalogGAgent.WellKnownId, ct)
                .ConfigureAwait(false);
            return version > 0;
        }

        return false;
    }

    private static async Task<IReadOnlyList<string>> DiscoverFromReadModelBestEffortAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct)
    {
        var reader = services.GetService<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        if (reader == null)
            return [];

        try
        {
            return await DiscoverFromReadModelAsync(reader, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Read-model probe is the snapshot+compaction patch — never block startup
            // when the projection store is unavailable. Fall back to event-stream walk;
            // un-compacted clusters still get cleaned, compacted ones merely degrade.
            logger.LogWarning(
                ex,
                "Retired user-agent discovery from {DocumentType} read model failed; falling back to catalog event stream walk.",
                nameof(UserAgentCatalogDocument));
            return [];
        }
    }

    private static async Task<IReadOnlyList<string>> DiscoverFromReadModelAsync(
        IProjectionDocumentReader<UserAgentCatalogDocument, string> reader,
        CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var result = await reader.QueryAsync(
                new ProjectionDocumentQuery
                {
                    Cursor = cursor,
                    Take = ReadModelPageSize,
                    Filters =
                    [
                        new ProjectionDocumentFilter
                        {
                            FieldPath = nameof(IProjectionReadModel.ActorId),
                            Operator = ProjectionDocumentFilterOperator.Eq,
                            Value = ProjectionDocumentValue.FromString(UserAgentCatalogGAgent.WellKnownId),
                        },
                    ],
                },
                ct).ConfigureAwait(false);

            foreach (var doc in result.Items)
            {
                if (IsGeneratedUserAgent(doc.Id, doc.AgentType))
                    ids.Add(doc.Id.Trim());
            }

            cursor = result.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return ids.ToArray();
    }

    private static async Task<IReadOnlyList<string>> DiscoverFromCatalogEventsAsync(
        IEventStore eventStore,
        CancellationToken ct)
    {
        var events = await eventStore
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
}
