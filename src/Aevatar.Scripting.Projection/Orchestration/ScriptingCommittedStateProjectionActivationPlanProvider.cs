using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
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
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Published.StateEvent?.EventData == null)
            yield break;

        if (context.ActorType == typeof(ScriptDefinitionGAgent) &&
            context.Published.StateEvent.EventData.Is(ScriptDefinitionUpsertedEvent.Descriptor))
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
}
