using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// One-call workflow provisioning facade. Composes the existing member-first
/// services so a caller (e.g. a Claude Code session reaching aevatar through the
/// nyxid proxy) provisions a runnable, scope-owned workflow in a single request:
/// create a member (kind = workflow) → bind the inline workflow YAML → durably
/// accept actor-owned schedule provisioning → return the provisioning identity
/// plus the Observatory and Studio links. The schedule id is returned only when
/// it is already visible.
///
/// The flow is NON-BLOCKING: the bind is asynchronous (it can take minutes), so
/// the service does not poll it to completion. The member actor observes the
/// exact binding revision before creating the scheduled-dispatch, which (because
/// its schedule kind is <c>Workflow</c>)
/// projects the caller's re-minted NyxID token onto the run so its LLM calls
/// authenticate — the one mechanism a direct invoke could not provide.
///
/// This service introduces no new runtime, no new workflow mechanism and no
/// MCP server; it only orchestrates <see cref="IStudioMemberService"/>, binding,
/// and the actor command port. <paramref name="scopeId"/> and
/// <paramref name="callerCredential"/> are always input parameters — the service
/// never reads an ambient HttpContext.
/// </summary>
public interface IStudioWorkflowProvisioningService
{
    /// <summary>
    /// Resolves and validates the stable workflow capability admission plan
    /// without creating a member, binding a workflow, or accepting a schedule.
    /// </summary>
    Task<ProvisionWorkflowPreparation> PrepareAsync(
        string scopeId,
        ProvisionWorkflowCallerCredential callerCredential,
        ProvisionWorkflowRequest request,
        CancellationToken ct = default);

    Task<ProvisionWorkflowResponse> ProvisionAsync(
        string scopeId,
        ProvisionWorkflowCallerCredential callerCredential,
        ProvisionWorkflowRequest request,
        CancellationToken ct = default);
}
