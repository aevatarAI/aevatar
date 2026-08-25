using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AppWorkflowCallerNyxIdAuthority =
    Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerNyxIdAuthority;
using AppWorkflowCallerCredential =
    Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class WorkflowWebhookIngressEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapPost("/workflow-webhooks/{routeKey}", HandleAsync)
            .WithName("PostWorkflowWebhook")
            // External senders (e.g. the NyxID trigger delivery) carry no
            // bearer identity; authentication for this route is the per-binding
            // HMAC signature verified inside the handler. Without this
            // exemption the host's fallback authorization policy rejects every
            // delivery with an empty 401 before the handler runs.
            .AllowAnonymous()
            .WithEndpointAudit(
                "workflow.webhook.ingress",
                AuditSensitivityLevel.Confidential,
                "workflow_run",
                // Static target: {routeKey} is an opaque webhook key, never recorded.
                EndpointAuditTargetResolvers.Static("workflow_run", "webhook-ingress"),
                captureUnauthenticated: true);

        WorkflowWebhookBindingEndpoints.Map(group);
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

        var normalizedRoute = WorkflowWebhookRoute.Normalize(routeKey);
        if (normalizedRoute == null)
        {
            scope.MarkResult(StatusCodes.Status404NotFound);
            return Results.Json(
                new { code = "WEBHOOK_ROUTE_NOT_FOUND", message = "Webhook route was not found." },
                statusCode: StatusCodes.Status404NotFound);
        }

        // Scope-registered bindings are data, always live. The Enabled flag
        // gates only the static appsettings binding list.
        var bindingStore = http.RequestServices.GetService<IWorkflowWebhookBindingStore>();
        var dynamicRecord = bindingStore is null
            ? null
            : await bindingStore.GetAsync(normalizedRoute, ct);
        var staticBindings = options.Value.Bindings
            .Where(binding => string.Equals(
                WorkflowWebhookRoute.Normalize(binding.RouteKey),
                normalizedRoute,
                StringComparison.Ordinal))
            .ToArray();

        if (dynamicRecord != null && staticBindings.Length > 0)
        {
            scope.MarkResult(StatusCodes.Status409Conflict);
            return Results.Json(
                new
                {
                    code = "WEBHOOK_ROUTE_CONFIGURATION_CONFLICT",
                    message = "Webhook route has both dynamic and host-configured bindings.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        if (staticBindings.Length > 1)
        {
            scope.MarkResult(StatusCodes.Status500InternalServerError);
            return Results.Json(
                new
                {
                    code = "WEBHOOK_ROUTE_CONFIGURATION_CONFLICT",
                    message = "Webhook route is configured more than once.",
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var resolvedBinding = dynamicRecord?.ToBindingOptions()
            ?? (options.Value.Enabled ? staticBindings.SingleOrDefault() : null);
        if (resolvedBinding is null)
        {
            scope.MarkResult(StatusCodes.Status404NotFound);
            return Results.Json(
                new { code = "WEBHOOK_ROUTE_NOT_FOUND", message = "Webhook route was not found." },
                statusCode: StatusCodes.Status404NotFound);
        }

        if (dynamicRecord != null && string.IsNullOrWhiteSpace(resolvedBinding.DefinitionActorId))
        {
            scope.MarkResult(StatusCodes.Status409Conflict);
            return Results.Json(
                new
                {
                    code = "WEBHOOK_EXACT_TARGET_REQUIRED",
                    message = "Dynamic webhook binding is not pinned to a workflow definition actor.",
                },
                statusCode: StatusCodes.Status409Conflict);
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
            rawBody = await ReadBodyAsync(
                http.Request,
                WorkflowWebhookIngressLimits.MaxBodyBytes,
                ct);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (WebhookBodyTooLargeException)
        {
            scope.MarkResult(StatusCodes.Status413PayloadTooLarge);
            return Results.Json(
                new
                {
                    code = "WEBHOOK_BODY_TOO_LARGE",
                    message = "Webhook request body exceeds the supported size.",
                },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var receivedAt = DateTimeOffset.UtcNow;
        var build = requestBuilder.Build(http.Request, normalizedRoute, rawBody, receivedAt, resolvedBinding);
        if (!build.Succeeded)
        {
            scope.MarkResult(build.StatusCode);
            return Results.Json(
                new { code = build.ErrorCode, message = build.ErrorMessage },
                statusCode: build.StatusCode);
        }

        // Authenticate and validate the body before touching the definition
        // projection. Anonymous callers must not be able to probe target
        // existence or revision drift through this public route.
        var exactTarget = await WorkflowWebhookExactTargetResolver.ResolveAsync(
            http.RequestServices.GetService<IWorkflowActorBindingReader>(),
            resolvedBinding,
            ct);
        if (!exactTarget.Succeeded)
        {
            scope.MarkResult(exactTarget.StatusCode);
            return Results.Json(
                new { code = exactTarget.ErrorCode, message = exactTarget.ErrorMessage },
                statusCode: exactTarget.StatusCode);
        }

        var runRequest = exactTarget.Definition == null
            ? build.Request!
            : build.Request! with
            {
                ExpectedExecutionMode = exactTarget.Definition.ExpectedExecutionMode,
                ScopeId = exactTarget.Definition.ScopeId,
                ResolvedDefinitionBinding = exactTarget.Definition,
            };
        if (dynamicRecord != null)
        {
            var attached = TryAttachUnattendedCaller(
                dynamicRecord,
                normalizedRoute,
                exactTarget.Definition,
                runRequest,
                out runRequest,
                out var attachError);
            if (!attached)
            {
                scope.MarkResult(attachError!.StatusCode);
                return Results.Json(
                    new { code = attachError.Code, message = attachError.Message },
                    statusCode: attachError.StatusCode);
            }
        }

        var admission = await replayStore.AdmitAsync(build.Admission!, ct);
        switch (admission.Status)
        {
            case WorkflowWebhookReplayAdmissionStatus.Admitted:
                return await DispatchAsync(http, runRequest, build.Admission!, replayStore, chatRunService, logger, scope, ct);
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

    private static bool TryAttachUnattendedCaller(
        WorkflowWebhookBindingRecord record,
        string routeKey,
        WorkflowDefinitionBinding? definition,
        WorkflowChatRunRequest source,
        out WorkflowChatRunRequest result,
        out WebhookIngressError? error)
    {
        result = source;
        error = null;
        var authority = record.CallerAuthority;
        var authorization = record.UnattendedEffectAuthorization;
        if (authority is null && authorization is null)
        {
            if (record.CallerDurableCredential != null)
            {
                error = InvalidDurableCallerCredentialError;
                return false;
            }
            return true;
        }
        if (authority is null || authorization is null || definition is null ||
            definition.ExpectedExecutionMode != ExternalCapabilityExecutionMode.Durable ||
            definition.CapabilityAdmissionPlan is null ||
            string.IsNullOrWhiteSpace(authority.BindingId))
        {
            error = UnattendedAuthorizationDriftError;
            return false;
        }

        try
        {
            WorkflowUnattendedEffectAuthorizationIntegrity.ValidateForDefinition(
                authorization,
                authority,
                routeKey,
                definition.DefinitionActorId,
                definition.ScopeId,
                definition.WorkflowId,
                definition.RevisionId,
                definition.DefinitionVersion,
                definition.CapabilityAdmissionPlan);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            error = UnattendedAuthorizationDriftError;
            return false;
        }

        var durableCredential = record.CallerDurableCredential;
        // Historical unattended bindings carry only authority and authorization.
        // AgentKey is reserved for bindings with an exact, valid durable handle.
        var credentialKind = NyxIdCallerCredentialKind.ProxyDelegation;
        if (durableCredential != null)
        {
            if (!IsValidWebhookDurableAgentKey(record, authority, durableCredential))
            {
                error = InvalidDurableCallerCredentialError;
                return false;
            }
            credentialKind = NyxIdCallerCredentialKind.AgentKey;
        }

        result = source with
        {
            CallerCredential = new AppWorkflowCallerCredential(
                BearerToken: null,
                NyxIdAuthority: new AppWorkflowCallerNyxIdAuthority(
                    authority.Platform,
                    authority.Tenant,
                    authority.ExternalUserId,
                    authority.Scope,
                    authority.BindingId),
                Kind: credentialKind,
                SourceReadableUserBearerToken: null,
                UnattendedEffectAuthorization: authorization.Clone(),
                DurableCallerCredential: durableCredential?.Clone()),
        };
        return true;
    }

    private static bool IsValidWebhookDurableAgentKey(
        WorkflowWebhookBindingRecord record,
        Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority authority,
        DurableCallerCredentialRef credential)
    {
        var descriptor = credential.SecretReference;
        return credential.SourceKind == DurableCallerCredentialSourceKind.WebhookBinding &&
               DurableCallerAgentKeyContract.Matches(credential) &&
               !string.IsNullOrWhiteSpace(credential.Ref) &&
               !string.IsNullOrWhiteSpace(credential.OwnerScopeKey) &&
               !string.IsNullOrWhiteSpace(credential.SubjectId) &&
               string.Equals(credential.OwnerScopeKey, record.ScopeId, StringComparison.Ordinal) &&
               string.Equals(credential.SubjectId, authority.ExternalUserId, StringComparison.Ordinal) &&
               descriptor is not null &&
               !string.IsNullOrWhiteSpace(descriptor.Ref) &&
               string.Equals(descriptor.Ref, credential.Ref, StringComparison.Ordinal) &&
               string.Equals(descriptor.Purpose, credential.Purpose, StringComparison.Ordinal) &&
               string.Equals(descriptor.OwnerScopeKey, credential.OwnerScopeKey, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(descriptor.Fingerprint) &&
               descriptor.Version > 0 &&
               descriptor.CreatedAtUnixMs > 0;
    }

    private static readonly WebhookIngressError UnattendedAuthorizationDriftError = new(
        StatusCodes.Status409Conflict,
        "WEBHOOK_UNATTENDED_AUTHORIZATION_DRIFT",
        "Webhook unattended authorization no longer matches the pinned workflow definition.");

    private static readonly WebhookIngressError InvalidDurableCallerCredentialError = new(
        StatusCodes.Status409Conflict,
        "WEBHOOK_DURABLE_CALLER_CREDENTIAL_INVALID",
        "Webhook durable caller credential is invalid for this binding.");

    private sealed record WebhookIngressError(int StatusCode, string Code, string Message);

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

    private static async Task<byte[]> ReadBodyAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken ct)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maxBytes)
            throw new WebhookBodyTooLargeException();

        using var memory = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;
            if (memory.Length + read > maxBytes)
                throw new WebhookBodyTooLargeException();
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return memory.ToArray();
    }

    private static string BuildWorkflowRunStatusUrl(string actorId) =>
        $"/api/workflow-actors/{Uri.EscapeDataString(actorId)}/current-state";

    private sealed class WebhookBodyTooLargeException : Exception;

}
