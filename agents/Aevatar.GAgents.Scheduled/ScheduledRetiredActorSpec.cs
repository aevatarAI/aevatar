using System.Runtime.CompilerServices;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.TypeSystem;
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
/// Dynamic discovery is gated on the catalog itself matching a retired
/// runtime-type token. On a fully-migrated cluster this gate keeps the catalog
/// read-model query a no-op even though the cleanup runs every startup.
///
/// Refactor (iter22/cluster-003):
///   Old pattern: retired actor discovery replayed catalog event streams and parsed actorId prefixes.
///   New principle: dynamic targets come only from typed catalog read models; actorId stays opaque.
/// </summary>
public sealed class ScheduledRetiredActorSpec : RetiredActorSpec
{
    private const string RetiredSkillRunnerType = "Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent";
    // Retained as a string literal so legacy clusters still clean up workflow_agent
    // event streams persisted before the social_media template was removed (issue #598).
    // Delete once all legacy actors have been retired from production clusters.
    private const string RetiredWorkflowAgentType = "Aevatar.GAgents.ChannelRuntime.WorkflowAgentGAgent";
    // Mirror of the deleted WorkflowAgentDefaults — kept here so retired-actor discovery
    // can still recognize legacy workflow_agent rows persisted in the catalog read model
    // and drive their cleanup. New agents never carry these tokens; delete with the
    // retired workflow_agent constants once all legacy actors are gone.
    private const string LegacyWorkflowAgentType = "workflow_agent";
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
            $"projection.durable.scope:{UserAgentCatalogStorageContracts.LegacyDurableProjectionKind}:{UserAgentCatalogGAgent.WellKnownId}",
            [
                "Aevatar.GAgents.ChannelRuntime.UserAgentCatalogMaterializationContext",
                "Aevatar.GAgents.ChannelRuntime.AgentRegistryMaterializationContext",
            ],
            SourceStreamId: UserAgentCatalogGAgent.WellKnownId),
        // Mid-migration deploys may have created the durable projection scope at
        // the new scope key (DurableProjectionKind) while still bound to the old
        // ChannelRuntime materialization context type, leaving the new
        // Aevatar.GAgents.Scheduled scope unable to recreate. Cover that case
        // explicitly so retired cleanup wipes the actor + its stream pub/sub
        // rendezvous state on next startup.
        new(
            $"projection.durable.scope:{UserAgentCatalogStorageContracts.DurableProjectionKind}:{UserAgentCatalogGAgent.WellKnownId}",
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
        // Refactor (iter22/cluster-003):
        //   Old pattern: dynamic cleanup replayed catalog event streams and inferred actor type from actorId text.
        //   New principle: cleanup enumerates typed catalog read-model rows and treats actorId as an opaque address.
        var typeProbe = services.GetRequiredService<IActorTypeProbe>();
        var logger = services.GetService<ILogger<ScheduledRetiredActorSpec>>()
                     ?? NullLogger<ScheduledRetiredActorSpec>.Instance;

        if (!await ShouldDiscoverFromCatalogAsync(typeProbe, ct).ConfigureAwait(false))
            yield break;

        foreach (var actorId in await DiscoverFromReadModelBestEffortAsync(services, logger, ct).ConfigureAwait(false))
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
            .DeleteByActorAsync<SkillRunnerExecutionDocument>(services, actorId, ct)
            .ConfigureAwait(false);
        await RetiredActorReadModelHelpers
            .DeleteByActorAsync<UserAgentCatalogNyxCredentialDocument>(services, actorId, ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> ShouldDiscoverFromCatalogAsync(
        IActorTypeProbe typeProbe,
        CancellationToken ct)
    {
        var catalogTarget = Targets.First(static target =>
            target.ActorId == UserAgentCatalogGAgent.WellKnownId);

        var runtimeTypeName = await typeProbe
            .GetRuntimeAgentTypeNameAsync(UserAgentCatalogGAgent.WellKnownId, ct)
            .ConfigureAwait(false);

        if (catalogTarget.MatchesRuntimeType(runtimeTypeName))
            return true;

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
            // Read-model discovery is maintenance best effort; failed typed queries
            // skip dynamic generated-agent cleanup rather than side-reading events.
            logger.LogWarning(
                ex,
                "Retired user-agent discovery from {DocumentType} read model failed; skipping dynamic generated-agent targets.",
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

    private static bool IsGeneratedUserAgent(string? agentId, string? agentType)
    {
        var normalizedId = agentId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedId))
            return false;

        if (string.Equals(agentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal) ||
            string.Equals(agentType, LegacyWorkflowAgentType, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
