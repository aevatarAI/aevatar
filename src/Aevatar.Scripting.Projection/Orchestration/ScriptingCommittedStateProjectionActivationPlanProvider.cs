using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core;

namespace Aevatar.Scripting.Projection.Orchestration;

/// <summary>
/// Maps scripting authority committed state events to the durable authority readmodel projection scope.
/// </summary>
// Refactor (iter49/issue-882-script-command-readmodel-activation):
//   Old pattern: ScopeScriptCommandApplicationService.UpsertAsync explicitly activated definition/catalog readmodels via ActivateAsync before write commands.
//   New principle: Command service dispatches accepted-only write commands; readmodel activation is owned by scripting committed-state projection activation plan provider.
public sealed class ScriptingCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    // Refactor (iter149/cluster-1133): Old pattern: scripting execution projection activation only recognized domain fact commits.  New principle: actor-owned script run outcome commits use the same projection-session observation path.
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Published.StateEvent?.EventData == null)
            yield break;

        if (context.ActorType == typeof(ScriptBehaviorGAgent) &&
            IsScriptExecutionMutation(context.Published.StateEvent.EventData))
        {
            yield return DurableExecutionPlan(context.ActorId);
            yield break;
        }

        if (context.ActorType == typeof(ScriptEvolutionSessionGAgent) &&
            IsEvolutionSessionMutation(context.Published.StateEvent.EventData))
        {
            yield return DurableEvolutionPlan(context.ActorId);
            yield break;
        }

        if (context.ActorType == typeof(ScriptDefinitionGAgent) &&
            IsDefinitionAuthorityMutation(context.Published.StateEvent.EventData))
        {
            yield return DurableAuthorityPlan(context.ActorId);
            yield break;
        }

        if (context.ActorType != typeof(ScriptCatalogGAgent) ||
            !IsCatalogAuthorityMutation(context.Published.StateEvent.EventData))
        {
            yield break;
        }

        yield return DurableAuthorityPlan(context.ActorId);
    }

    private static ProjectionActivationPlan DurableExecutionPlan(string actorId) =>
        new()
        {
            LeaseType = typeof(ScriptExecutionMaterializationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ScriptProjectionKinds.ExecutionMaterialization,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };

    private static bool IsScriptExecutionMutation(Google.Protobuf.WellKnownTypes.Any eventData)
    {
        // Refactor (iter149/cluster-1133): Old pattern: execution mutation meant only ScriptDomainFactCommitted.  New principle: ScriptRunOutcomeRecordedEvent is a first-class committed execution fact for observers.
        if (eventData.Is(ScriptDomainFactCommitted.Descriptor))
            return true;

        if (eventData.Is(ScriptRunOutcomeRecordedEvent.Descriptor))
            return true;

        return false;
    }

    private static ProjectionActivationPlan DurableEvolutionPlan(string actorId) =>
        new()
        {
            LeaseType = typeof(ScriptEvolutionMaterializationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ScriptProjectionKinds.EvolutionMaterialization,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };

    private static ProjectionActivationPlan DurableAuthorityPlan(string actorId) =>
        new()
        {
            LeaseType = typeof(ScriptAuthorityRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ScriptProjectionKinds.AuthorityMaterialization,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };

    private static bool IsDefinitionAuthorityMutation(Google.Protobuf.WellKnownTypes.Any eventData)
    {
        if (eventData.Is(ScriptDefinitionUpsertedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptReadModelSchemaDeclaredEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptReadModelSchemaValidatedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptReadModelSchemaActivationFailedEvent.Descriptor))
            return true;

        return false;
    }

    private static bool IsCatalogAuthorityMutation(Google.Protobuf.WellKnownTypes.Any eventData)
    {
        if (eventData.Is(ScriptCatalogRevisionPromotedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptCatalogRollbackRequestedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptCatalogRolledBackEvent.Descriptor))
            return true;

        return false;
    }

    private static bool IsEvolutionSessionMutation(Google.Protobuf.WellKnownTypes.Any eventData)
    {
        if (eventData.Is(ScriptEvolutionSessionStartedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptEvolutionProposedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptEvolutionBuildRequestedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptEvolutionValidatedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptEvolutionRejectedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptEvolutionPromotedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptEvolutionRollbackRequestedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptEvolutionRolledBackEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptEvolutionSessionCompletedEvent.Descriptor))
            return true;

        return false;
    }
}
