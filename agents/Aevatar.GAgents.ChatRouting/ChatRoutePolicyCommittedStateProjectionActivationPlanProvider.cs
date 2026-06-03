using Aevatar.ChatRouting.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// Maps chat-route-policy committed state events to the durable current-state projection scope.
/// </summary>
// Refactor (iter32/cluster-034-chat-route-policy-request-path-projection-activation):
//   Old pattern: Chat route policy admin endpoints + voice demo bootstrap 在 request path 调 EnsureProjectionForActorAsync 同步 priming projection,违反 query-time priming forbidden + 命令骨架内聚
//   New principle: 加 ChatRoutePolicyCommittedStateProjectionActivationPlanProvider(committed-state hook 触发);删 ChatRoutePolicyProjectionPort + request-path activation;DI 注册 dispatcher + hook + provider;query_projection_priming_guard 加 chat route policy endpoint 扫描
public sealed class ChatRoutePolicyCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    // Refactor (iter32/cluster-034-chat-route-policy-request-path-projection-activation):
    //   Old pattern: Chat route policy admin endpoints + voice demo bootstrap 在 request path 调 EnsureProjectionForActorAsync 同步 priming projection,违反 query-time priming forbidden + 命令骨架内聚
    //   New principle: 加 ChatRoutePolicyCommittedStateProjectionActivationPlanProvider(committed-state hook 触发);删 ChatRoutePolicyProjectionPort + request-path activation;DI 注册 dispatcher + hook + provider;query_projection_priming_guard 加 chat route policy endpoint 扫描
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ActorType != typeof(ChatRoutePolicyGAgent) ||
            context.Published.StateEvent?.EventData == null ||
            !context.Published.StateEvent.EventData.Is(ChatRoutePolicyUpdated.Descriptor))
        {
            yield break;
        }

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(ChatRoutePolicyMaterializationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = ChatRoutePolicyGAgent.ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
    }
}
