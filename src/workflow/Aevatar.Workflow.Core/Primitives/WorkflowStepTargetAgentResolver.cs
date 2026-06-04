using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Primitives;
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
