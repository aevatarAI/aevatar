using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// One-call workflow provisioning facade. Composes the existing member-first
/// services so a caller (e.g. a Claude Code session reaching aevatar through the
/// nyxid proxy) provisions a runnable, scope-owned workflow in a single request:
/// create a member (kind = workflow) → bind the inline workflow YAML → create a
/// scheduled-dispatch that produces the run under the caller scope → return the
/// schedule id plus the Observatory and Studio links.
///
/// The flow is NON-BLOCKING: the bind is asynchronous (it can take minutes), so
/// the service does not poll it to completion. The run is produced by the
/// scheduled-dispatch, which (because its schedule kind is <c>Workflow</c>)
/// projects the caller's re-minted NyxID token onto the run so its LLM calls
/// authenticate — the one mechanism a direct invoke could not provide.
///
/// This service introduces no new runtime, no new workflow mechanism and no
/// MCP server; it only orchestrates <see cref="IStudioMemberService"/> and the
/// scheduled-dispatch application service. <paramref name="scopeId"/> and
/// <paramref name="callerCredential"/> are always input parameters — the service
/// never reads an ambient HttpContext.
/// </summary>
public interface IStudioWorkflowProvisioningService
{
    Task<ProvisionWorkflowResponse> ProvisionAsync(
        string scopeId,
        ProvisionWorkflowCallerCredential callerCredential,
        ProvisionWorkflowRequest request,
        CancellationToken ct = default);
}
