using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.GAgents.WorkOrder;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Workspace;

namespace Aevatar.Studio.Projection.Orchestration;

/// <summary>
/// Maps Studio actor committed state events to durable Studio readmodel materialization scopes.
/// </summary>
public sealed class StudioCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    private static readonly IReadOnlyDictionary<Type, string> ProjectionKinds =
        new Dictionary<Type, string>
        {
            [typeof(UserConfigGAgent)] = UserConfigGAgent.ProjectionKind,
            [typeof(GAgentRegistryGAgent)] = GAgentRegistryGAgent.ProjectionKind,
            [typeof(ConnectorCatalogGAgent)] = ConnectorCatalogGAgent.ProjectionKind,
            [typeof(RoleCatalogGAgent)] = RoleCatalogGAgent.ProjectionKind,
            [typeof(UserMemoryGAgent)] = UserMemoryGAgent.ProjectionKind,
            [typeof(ChatConversationGAgent)] = ChatConversationGAgent.ProjectionKind,
            [typeof(ChatTurnHistoryDeliveryGAgent)] = ChatTurnHistoryDeliveryGAgent.ProjectionKind,
            [typeof(NyxIdChatConversationGAgent)] = NyxIdChatConversationGAgent.ProjectionKind,
            [typeof(StudioMemberGAgent)] = StudioMemberGAgent.ProjectionKind,
            [typeof(StudioMemberBindingRunGAgent)] = StudioMemberBindingRunGAgent.ProjectionKind,
            [typeof(StudioTeamGAgent)] = StudioTeamGAgent.ProjectionKind,
            [typeof(ContentArtifactGAgent)] = ContentArtifactGAgent.ProjectionKind,
            [typeof(WorkOrderGAgent)] = WorkOrderGAgent.ProjectionKind,
            [typeof(StudioWorkspaceGAgent)] = StudioWorkspaceGAgent.ProjectionKind,
        };

    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Published.StateEvent?.EventData == null)
            yield break;

        if (!ProjectionKinds.TryGetValue(context.ActorType, out var projectionKind))
            yield break;

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(StudioMaterializationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
    }
}
