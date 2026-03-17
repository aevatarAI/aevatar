using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class UserWorkflowEndpoints
{
    public static IEndpointRouteBuilder MapUserWorkflowCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("UserWorkflows");
        group.MapPut("/{userId}/workflows/{workflowId}", HandleUpsertWorkflowAsync)
            .Produces<UserWorkflowUpsertResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{userId}/workflows", HandleListWorkflowsAsync)
            .Produces<IReadOnlyList<UserWorkflowSummary>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/{userId}/workflow-runs:stream", HandleRunWorkflowStreamAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        return app;
    }

    internal static async Task<IResult> HandleUpsertWorkflowAsync(
        string userId,
        string workflowId,
        UpsertUserWorkflowHttpRequest request,
        [FromServices] IUserWorkflowCommandPort workflowCommandPort,
        CancellationToken ct)
    {
        try
        {
            var result = await workflowCommandPort.UpsertAsync(new UserWorkflowUpsertRequest(
                userId,
                workflowId,
                request.WorkflowYaml,
                request.WorkflowName,
                request.DisplayName,
                request.InlineWorkflowYamls,
                request.RevisionId), ct);
            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
    }

    internal static async Task<IResult> HandleListWorkflowsAsync(
        string userId,
        [FromServices] IUserWorkflowQueryPort workflowQueryPort,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await workflowQueryPort.ListAsync(userId, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
    }

    internal static async Task HandleRunWorkflowStreamAsync(
        HttpContext http,
        string userId,
        RunUserWorkflowStreamHttpRequest request,
        [FromServices] IUserWorkflowQueryPort workflowQueryPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        try
        {
            var workflow = await workflowQueryPort.GetByActorIdAsync(userId, request.ActorId, ct);
            if (workflow == null)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    "USER_WORKFLOW_NOT_FOUND",
                    "Workflow actor was not found for the specified user.",
                    ct);
                return;
            }

            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                new ChatInput
                {
                    Prompt = request.Prompt,
                    AgentId = workflow.ActorId,
                    SessionId = request.SessionId,
                    Metadata = request.Headers == null
                        ? null
                        : new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
                },
                chatRunService,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_USER_WORKFLOW_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(new { code, message }, cancellationToken: ct);
    }

    public sealed record UpsertUserWorkflowHttpRequest(
        string WorkflowYaml,
        string? WorkflowName = null,
        string? DisplayName = null,
        Dictionary<string, string>? InlineWorkflowYamls = null,
        string? RevisionId = null);

    public sealed record RunUserWorkflowStreamHttpRequest(
        string ActorId,
        string Prompt,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null);
}
