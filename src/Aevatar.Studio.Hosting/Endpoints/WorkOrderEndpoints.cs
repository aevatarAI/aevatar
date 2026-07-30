using System.Security.Claims;
using Aevatar.Capabilities;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

internal static class WorkOrderEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/scopes/{scopeId}/work-orders", HandleCreateAsync)
            .WithTags("WorkOrders");
        app.MapGet("/api/scopes/{scopeId}/work-orders", HandleListAsync)
            .WithTags("WorkOrders");
        app.MapGet("/api/scopes/{scopeId}/work-orders/{workOrderId}", HandleGetAsync)
            .WithTags("WorkOrders");
        app.MapPost("/api/scopes/{scopeId}/work-orders/{workOrderId}:reassign", HandleReassignAsync)
            .WithTags("WorkOrders");
        app.MapPost("/api/scopes/{scopeId}/work-orders/{workOrderId}:dispatch", HandleDispatchAsync)
            .WithTags("WorkOrders");
        app.MapPost("/api/scopes/{scopeId}/work-orders/{workOrderId}:cancel", HandleCancelAsync)
            .WithTags("WorkOrders");
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string scopeId,
        CreateWorkOrderRequest request,
        [FromServices] IWorkOrderService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryResolvePrincipal(http.User, out var requester))
            return Results.Unauthorized();

        try
        {
            var receipt = await service.CreateAsync(scopeId, request, requester, ct);
            return Results.Accepted(BuildLocation(scopeId, receipt.WorkOrderId), receipt);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_WORK_ORDER_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleListAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IWorkOrderService service,
        int? pageSize,
        string? pageToken,
        string? status,
        string? requesterPrincipalId,
        string? teamId,
        string? memberId,
        string? publishedServiceId,
        string? workflowId,
        string? runId,
        DateTimeOffset? createdFromUtc,
        DateTimeOffset? createdToUtc,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await service.ListAsync(
                scopeId,
                new WorkOrderQueryRequest(
                    pageSize,
                    pageToken,
                    status,
                    requesterPrincipalId,
                    teamId,
                    memberId,
                    publishedServiceId,
                    workflowId,
                    runId,
                    createdFromUtc,
                    createdToUtc),
                ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_WORK_ORDER_QUERY", ex.Message);
        }
    }

    internal static async Task<IResult> HandleGetAsync(
        HttpContext http,
        string scopeId,
        string workOrderId,
        [FromServices] IWorkOrderService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        WorkOrderCurrentStateResponse response;
        try
        {
            response = await service.GetAsync(scopeId, workOrderId, ct);
        }
        catch (ArgumentException ex) when (
            string.Equals(ex.ParamName, nameof(workOrderId), StringComparison.Ordinal))
        {
            return BadRequest(
                "INVALID_WORK_ORDER_ID",
                "The WorkOrder identity is malformed.");
        }
        catch (WorkOrderNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_WORK_ORDER_REQUEST", ex.Message);
        }

        return Results.Ok(response);
    }

    internal static Task<IResult> HandleReassignAsync(
        HttpContext http,
        string scopeId,
        string workOrderId,
        ReassignWorkOrderRequest request,
        [FromServices] IWorkOrderService service,
        CancellationToken ct) =>
        HandleRequesterCommandAsync(
            http,
            scopeId,
            workOrderId,
            service,
            (principal, token) => service.ReassignAsync(scopeId, workOrderId, request, principal, token),
            ct);

    internal static Task<IResult> HandleDispatchAsync(
        HttpContext http,
        string scopeId,
        string workOrderId,
        DispatchWorkOrderRequest request,
        [FromServices] IWorkOrderService service,
        CancellationToken ct) =>
        HandleRequesterCommandAsync(
            http,
            scopeId,
            workOrderId,
            service,
            (principal, token) => service.DispatchAsync(scopeId, workOrderId, request, principal, token),
            ct);

    internal static Task<IResult> HandleCancelAsync(
        HttpContext http,
        string scopeId,
        string workOrderId,
        CancelWorkOrderRequest request,
        [FromServices] IWorkOrderService service,
        CancellationToken ct) =>
        HandleRequesterCommandAsync(
            http,
            scopeId,
            workOrderId,
            service,
            (principal, token) => service.CancelAsync(scopeId, workOrderId, request, principal, token),
            ct);

    private static async Task<IResult> HandleRequesterCommandAsync(
        HttpContext http,
        string scopeId,
        string workOrderId,
        IWorkOrderService service,
        Func<WorkOrderPrincipalContract, CancellationToken, Task<WorkOrderAcceptedReceipt>> command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryResolvePrincipal(http.User, out var principal))
            return Results.Unauthorized();

        WorkOrderAcceptedReceipt receipt;
        try
        {
            receipt = await command(principal, ct);
        }
        catch (ArgumentException ex) when (
            string.Equals(ex.ParamName, nameof(workOrderId), StringComparison.Ordinal))
        {
            return BadRequest(
                "INVALID_WORK_ORDER_ID",
                "The WorkOrder identity is malformed.");
        }
        catch (WorkOrderNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_WORK_ORDER_COMMAND", ex.Message);
        }

        return Results.Accepted(BuildLocation(scopeId, workOrderId), receipt);
    }

    private static bool TryResolvePrincipal(
        ClaimsPrincipal user,
        out WorkOrderPrincipalContract principal)
    {
        var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value?.Trim()
                      ?? user.FindFirst("sub")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(subject))
        {
            principal = null!;
            return false;
        }

        principal = new WorkOrderPrincipalContract(subject, "user");
        return true;
    }

    private static string BuildLocation(string scopeId, string workOrderId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/work-orders/{Uri.EscapeDataString(workOrderId)}";

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { code, message });

    private static IResult NotFound(string message) =>
        Results.NotFound(new { code = "WORK_ORDER_NOT_FOUND", message });
}
