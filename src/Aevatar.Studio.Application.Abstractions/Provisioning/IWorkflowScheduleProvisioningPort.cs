namespace Aevatar.Studio.Application.Provisioning;

/// <summary>
/// Narrow, tool-facing port for provisioning a runnable workflow whose recurring
/// runs surface in the Observatory (never a channel/bot). It is the abstraction an
/// agent tool can depend on without referencing the Studio application impl
/// assembly (which drags in the Scripting/Domain/Configuration graph): the tool
/// provider references only this Abstractions project, and a thin adapter in
/// <c>Aevatar.Studio.Application</c> forwards to the existing
/// <c>IStudioWorkflowProvisioningService</c> (member create → bind YAML →
/// <c>ScheduleKind=Workflow</c> scheduled-dispatch). The port surface deliberately
/// carries NO channel / Lark / owner / credential plumbing beyond the caller
/// identity it needs to provision under the right scope — it is the channel-free
/// analogue of the Lark <c>scheduled_agent_creator</c>.
///
/// <paramref name="request"/> always supplies the scope and caller identity as
/// input; like the underlying service, the port and its adapter never read an
/// ambient HttpContext or AsyncLocal — the caller (the tool) reads those from the
/// tool execution context and passes them in.
/// </summary>
public interface IWorkflowScheduleProvisioningPort
{
    Task<WorkflowScheduleProvisioningResult> ProvisionAsync(
        WorkflowScheduleProvisioningRequest request,
        CancellationToken ct = default);
}
