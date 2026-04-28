using System.Runtime.CompilerServices;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.Compatibility;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Retired-actor declaration for the user-agent catalog and the generated
/// skill-runner / workflow-agent actors previously hosted by the deleted
/// <c>Aevatar.GAgents.ChannelRuntime</c> assembly. Reads the catalog event
/// stream to discover the dynamic generated-actor ids before the catalog
/// itself is destroyed.
/// </summary>
public sealed class ScheduledRetiredActorSpec : RetiredActorSpec
{
    private const string RetiredSkillRunnerType = "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent";
    private const string RetiredWorkflowAgentType = "Aevatar.GAgents.ChannelRuntime.WorkflowAgentGAgent";

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
        var eventStore = services.GetRequiredService<IEventStore>();
        var agentIds = await DiscoverCatalogUserAgentIdsAsync(eventStore, ct).ConfigureAwait(false);
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

    private static async Task<IReadOnlyList<string>> DiscoverCatalogUserAgentIdsAsync(
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
