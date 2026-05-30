using Aevatar.Hosting;
using Aevatar.Studio.Projection.Repair;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

// Refactor (iter1357/cluster-explicit-scope-draft-member-repair):
//   Old pattern: there was no explicit scoped HTTP entry point for repairing
//   historical workflow-draft member authority gaps.
//   New principle: Host only guards scope access and composes the one-shot
//   repair service; repair orchestration stays in the projection service.
internal static class StudioWorkflowDraftMemberRepairEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(
                "/api/scopes/{scopeId}/workflow-drafts:repair-members",
                HandleRepairScopeAsync)
            .WithTags("StudioWorkflowDraftRepair");
    }

    internal static async Task<IResult> HandleRepairScopeAsync(
        HttpContext http,
        string scopeId,
        [FromServices] StudioWorkflowDraftMemberRepairService repairService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Accepted(
                $"/api/scopes/{Uri.EscapeDataString(scopeId)}/workflow-drafts:repair-members",
                await repairService.RepairScopeAsync(scopeId, ct).ConfigureAwait(false));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_STUDIO_WORKFLOW_DRAFT_MEMBER_REPAIR_REQUEST",
                message = ex.Message,
            });
        }
    }
}
