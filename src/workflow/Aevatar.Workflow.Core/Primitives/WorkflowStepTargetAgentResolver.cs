using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Primitives;

// Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
//   Old pattern: WorkflowStepTargetAgentResolver 用 agent_type/agent_id 通过 Type.GetType + AppDomain scan + IRoleAgentTypeResolver 直接 create/link actors,workflow step parameter 暴露 raw CLR lifecycle
//   New principle: role-level agent_kind 配合 WorkflowRunGAgent runtime lifecycle;step 只用 target_role;删 agent_type/agent_id raw lifecycle 参数 + IWorkflowAgentTypeAliasProvider;Foundation 加 CreateByKindAsync;Bridge 注册 stable kind token
public sealed class WorkflowStepTargetAgentResolver
{
    public Task<WorkflowStepTargetAgentResolution> ResolveAsync(
        StepRequestEvent request,
        IEventContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        var targetRole = request.TargetRole;
        if (!string.IsNullOrWhiteSpace(targetRole))
        {
            var roleActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(ctx.AgentId, targetRole);
            return Task.FromResult(WorkflowStepTargetAgentResolution.Actor(roleActorId, $"target_role:{targetRole}"));
        }

        var implicitTargetRole = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
            workflow: null,
            configuredTargetRole: request.TargetRole,
            stepType: request.StepType);
        if (!string.IsNullOrWhiteSpace(implicitTargetRole))
        {
            var roleActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(ctx.AgentId, implicitTargetRole);
            return Task.FromResult(WorkflowStepTargetAgentResolution.Actor(roleActorId, $"implicit_target_role:{implicitTargetRole}"));
        }

        return Task.FromResult(WorkflowStepTargetAgentResolution.Self(ctx.AgentId));
    }
}

public readonly record struct WorkflowStepTargetAgentResolution(
    bool UseSelf,
    string ActorId,
    string Mode,
    string WorkerId)
{
    public static WorkflowStepTargetAgentResolution Self(string workerId) =>
        new(true, string.Empty, "self", workerId);

    public static WorkflowStepTargetAgentResolution Actor(string actorId, string mode) =>
        new(false, actorId, mode, actorId);
}
