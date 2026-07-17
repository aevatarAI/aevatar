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
/// Retired-actor declaration for the user-agent catalog and generated workflow-agent actors.
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
    private const string RetiredScheduledSkillRunnerKind = "channel-runtime.skill-runner";
    private const string RetiredScheduledSkillRunnerType = "skill_runner";
    private const string RetiredWorkflowAgentKind = "channel-runtime.workflow-agent";
    private const string RetiredWorkflowAgentType = "workflow_agent";
    private const int ReadModelPageSize = 500;

    public override string SpecId => "scheduled";

    // Retire only the legacy catalog actor body left by the deleted
    // Aevatar.GAgents.ChannelRuntime assembly. The durable materialization scopes MUST NOT
    // be retired targets: a scope's runtime kind is derived from the context's simple type
    // name, so the legacy and current UserAgentCatalog/AgentRegistry materialization contexts
    // collapse to the same "projection.materialization-scope.*" kinds. Retiring them would
    // destroy the live projection scope on every startup cleanup pass and leave the
    // user-agent-catalog / agent-registry read models un-materialized (#1763 regression).
    public override IReadOnlyList<RetiredActorTarget> Targets { get; } =
    [
        new(
            UserAgentCatalogGAgent.WellKnownId,
            [
                "channel-runtime.user-agent-catalog",
                "channel-runtime.agent-registry",
            ],
            CleanupReadModels: true),
    ];

    public override async IAsyncEnumerable<RetiredActorTarget> DiscoverDynamicTargetsAsync(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Refactor (iter22/cluster-003):
        //   Old pattern: dynamic cleanup replayed catalog event streams and inferred actor type from actorId text.
        //   New principle: cleanup enumerates typed catalog read-model rows and treats actorId as an opaque address.
        var kindProbe = services.GetRequiredService<IActorKindProbe>();
        var logger = services.GetService<ILogger<ScheduledRetiredActorSpec>>()
                     ?? NullLogger<ScheduledRetiredActorSpec>.Instance;

        if (!await ShouldDiscoverFromCatalogAsync(kindProbe, ct).ConfigureAwait(false))
            yield break;

        foreach (var actorId in await DiscoverFromReadModelBestEffortAsync(services, logger, ct).ConfigureAwait(false))
        {
            yield return new RetiredActorTarget(
                actorId,
                [RetiredScheduledSkillRunnerKind, RetiredWorkflowAgentKind],
                ResetWhenRuntimeKindUnavailable: true);
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
        IActorKindProbe kindProbe,
        CancellationToken ct)
    {
        var catalogTarget = Targets.First(static target =>
            target.ActorId == UserAgentCatalogGAgent.WellKnownId);

        var runtimeKind = await kindProbe
            .GetRuntimeAgentKindAsync(UserAgentCatalogGAgent.WellKnownId, ct)
            .ConfigureAwait(false);

        if (catalogTarget.MatchesRuntimeKind(runtimeKind))
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

        if (string.Equals(agentType, RetiredScheduledSkillRunnerType, StringComparison.Ordinal) ||
            string.Equals(agentType, RetiredWorkflowAgentType, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
