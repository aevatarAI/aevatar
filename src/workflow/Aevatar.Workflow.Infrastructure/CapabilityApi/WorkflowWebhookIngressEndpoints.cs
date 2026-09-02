using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class WorkflowWebhookIngressEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapPost("/workflow-webhooks/{routeKey}", HandleAsync)
            .WithName("PostWorkflowWebhook")
            .WithEndpointAudit(
                "workflow.webhook.ingress",
                AuditSensitivityLevel.Confidential,
                "workflow_run",
                // Static target: {routeKey} is an opaque webhook key, never recorded.
                EndpointAuditTargetResolvers.Static("workflow_run", "webhook-ingress"),
                captureUnauthenticated: true);
    }

    internal static async Task<IResult> HandleAsync(
        HttpContext http,
        string routeKey,
        WorkflowWebhookIngressRequestBuilder requestBuilder,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> chatRunService,
        IOptions<WorkflowWebhookIngressOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        var logger = loggerFactory.CreateLogger("Aevatar.Workflow.Host.Api.Webhook");
        if (!options.Value.Enabled)
        {
            scope.MarkResult(StatusCodes.Status404NotFound);
            return Results.Json(
                new { code = "WEBHOOK_INGRESS_DISABLED", message = "Workflow webhook ingress is disabled." },
                statusCode: StatusCodes.Status404NotFound);
        }

        var replayStore = http.RequestServices.GetService<IWorkflowWebhookReplayStore>();
        if (replayStore == null)
        {
            scope.MarkResult(StatusCodes.Status503ServiceUnavailable);
            return Results.Json(
                new { code = "WEBHOOK_REPLAY_STORE_UNAVAILABLE", message = "Workflow webhook replay store is unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        byte[] rawBody;
        try
        {
            rawBody = await ReadBodyAsync(http.Request, ct);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }

        var receivedAt = DateTimeOffset.UtcNow;
        var build = requestBuilder.Build(http.Request, routeKey, rawBody, receivedAt);
        if (!build.Succeeded)
        {
            scope.MarkResult(build.StatusCode);
            return Results.Json(
                new { code = build.ErrorCode, message = build.ErrorMessage },
                statusCode: build.StatusCode);
        }

        var admission = await replayStore.AdmitAsync(build.Admission!, ct);
        switch (admission.Status)
        {
            case WorkflowWebhookReplayAdmissionStatus.Admitted:
                return await DispatchAsync(http, build.Request!, build.Admission!, replayStore, chatRunService, logger, scope, ct);
            case WorkflowWebhookReplayAdmissionStatus.DuplicateCompleted:
            case WorkflowWebhookReplayAdmissionStatus.DuplicateInProgress:
                scope.MarkResult(StatusCodes.Status202Accepted);
                return Results.Accepted(
                    value: new
                    {
                        deliveryId = build.Admission!.DeliveryId,
                        duplicate = true,
                        status = admission.Status.ToString(),
                        commandId = admission.ExistingCommandId,
                        correlationId = admission.ExistingCorrelationId,
                    });
            case WorkflowWebhookReplayAdmissionStatus.PayloadConflict:
                scope.MarkResult(StatusCodes.Status409Conflict);
                return Results.Json(
                    new { code = "WEBHOOK_DELIVERY_PAYLOAD_CONFLICT", message = "Webhook delivery id was reused with a different payload." },
                    statusCode: StatusCodes.Status409Conflict);
            case WorkflowWebhookReplayAdmissionStatus.ExpiredRejected:
                scope.MarkResult(StatusCodes.Status410Gone);
                return Results.Json(
                    new { code = "WEBHOOK_DELIVERY_EXPIRED", message = "Webhook delivery is outside the replay retention window." },
                    statusCode: StatusCodes.Status410Gone);
            default:
                scope.MarkResult(StatusCodes.Status503ServiceUnavailable);
                return Results.Json(
                    new { code = "WEBHOOK_REPLAY_ADMISSION_FAILED", message = "Webhook replay admission failed." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> DispatchAsync(
        HttpContext http,
        WorkflowChatRunRequest command,
        WorkflowWebhookReplayAdmissionRequest admission,
        IWorkflowWebhookReplayStore replayStore,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> chatRunService,
        ILogger logger,
        ApiRequestScope scope,
        CancellationToken ct)
    {
        try
        {
            var dispatch = await chatRunService.DispatchAsync(command, ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                await ReleaseAdmissionAsync(admission, replayStore, logger);
                var mappedError = ChatRunStartErrorMapper.ToCommandError(dispatch.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(dispatch.Error);
                scope.MarkResult(statusCode);
                return Results.Json(
                    new { code = mappedError.Code, message = mappedError.Message },
                    statusCode: statusCode);
            }

            CapabilityTraceContext.ApplyCorrelationHeader(http.Response, dispatch.Receipt.CorrelationId);
            var statusUrl = BuildWorkflowRunStatusUrl(dispatch.Receipt.ActorId);
            return Results.Accepted(
                statusUrl,
                new
                {
                    commandId = dispatch.Receipt.CommandId,
                    correlationId = dispatch.Receipt.CorrelationId,
                    actorId = dispatch.Receipt.ActorId,
                    deliveryId = command.ExternalIngress?.DeliveryId ?? string.Empty,
                    statusUrl,
                });
        }
        catch (OperationCanceledException)
        {
            await ReleaseAdmissionAsync(admission, replayStore, logger);
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            await ReleaseAdmissionAsync(admission, replayStore, logger);
            scope.MarkError();
            logger.LogError(ex, "Workflow webhook command dispatch failed.");
            return Results.Json(
                new { code = "WEBHOOK_DISPATCH_FAILED", message = "Workflow webhook dispatch failed." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task ReleaseAdmissionAsync(
        WorkflowWebhookReplayAdmissionRequest admission,
        IWorkflowWebhookReplayStore replayStore,
        ILogger logger)
    {
        try
        {
            await replayStore.ReleaseAsync(admission, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow webhook replay admission release failed.");
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, ct);
        return memory.ToArray();
    }

    private static string BuildWorkflowRunStatusUrl(string actorId) =>
        $"/api/workflow-actors/{Uri.EscapeDataString(actorId)}/current-state";
}
