using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// One-call workflow provisioning facade. Composes the existing member-first
/// services so a caller (e.g. a Claude Code session reaching aevatar through the
/// nyxid proxy) provisions a runnable, scope-owned workflow in a single request:
/// create a member (kind = workflow) → bind the inline workflow YAML → poll the
/// binding run until it succeeds → optionally invoke it once to produce a run
/// stamped with the caller scope → return the run id plus the Observatory and
/// Studio links.
///
/// This service introduces no new runtime, no new workflow mechanism and no
/// MCP server; it only orchestrates <see cref="IStudioMemberService"/> and the
/// platform invocation port. <paramref name="scopeId"/> is always an input
/// parameter — the service never reads an ambient HttpContext.
/// </summary>
public interface IStudioWorkflowProvisioningService
{
    Task<ProvisionWorkflowResponse> ProvisionAsync(
        string scopeId,
        ProvisionWorkflowRequest request,
        CancellationToken ct = default);
}
