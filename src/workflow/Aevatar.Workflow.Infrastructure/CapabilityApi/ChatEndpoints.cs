using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCapabilityEndpoints
{
    private const string WorkflowRuntimeDefaultsSectionName = "WorkflowRuntimeDefaults";
    private static readonly WorkflowMultipartChatRequestParseError ChatPostUnsupportedMediaType = new(
        StatusCodes.Status415UnsupportedMediaType,
        "UNSUPPORTED_MEDIA_TYPE",
        "Content-Type must be application/json or multipart/form-data.");

    public static IEndpointRouteBuilder MapWorkflowCapabilityEndpoints(
        this IEndpointRouteBuilder app,
        bool mapChatPost = true)
    {
        var group = app.MapGroup("/api").WithTags("Chat");
        if (mapChatPost)
        {
            group.MapPost("/chat", HandleChatPost)
                .WithName("StartWorkflowChat");
        }
        group.MapGet(
                "/ws/chat",
                async (
                    HttpContext http,
                    [FromServices] IWorkflowChatRunInteractionPort chatRunService,
                    [FromServices] ILoggerFactory loggerFactory,
                    CancellationToken ct) =>
                    await HandleChatWebSocket(http, chatRunService, loggerFactory, ct))
            .WithName("StartWorkflowChatWebSocket");
        ChatQueryEndpoints.Map(group);
        group.MapPost("/workflow/runs/fork", HandleForkRun)
            .WithName("ForkWorkflowRun");
        WorkflowWebhookIngressEndpoints.Map(group);
        WorkflowExternalApprovalCallbackEndpoints.Map(group);
        // 06-19-workflow-run-observatory (C2): read-only, scope-gated run viewer. Maps its own absolute
        // routes (admin frame /admin/workflow-observatory + data /api/workflow/observatory/*) on the root app.
        app.MapWorkflowRunObservatory();
        // Workflow studio: conversational orchestration surface, gated behind the same OIDC login as the
        // observatory. Mount + login only this increment (page /workflow/studio + /workflow/studio/callback).
        app.MapWorkflowStudio();

        return app;
    }

    public static Task HandleChatPostAsync(HttpContext http, CancellationToken ct = default) =>
        HandleChatPost(
            http,
            http.RequestServices.GetRequiredService<IWorkflowChatRunInteractionPort>(),
            http.RequestServices.GetRequiredService<WorkflowMultipartChatRequestParser>(),
            ct);

    public static async Task HandleHttpChat(
        HttpContext http,
        HttpChatInput input,
        IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct = default,
        WorkflowDefinitionBinding? resolvedDefinitionBinding = null)
    {
        var scopeResolution = ResolvePostTrustedScope(http);
        if (!scopeResolution.Succeeded)
        {
            await WriteJsonErrorResponseAsync(
                http,
                scopeResolution.StatusCode,
                scopeResolution.Code,
                scopeResolution.Message,
                ct);
            return;
        }

        await HandleHttpChat(
                http,
                input,
                scopeResolution.ScopeId!,
                chatRunService,
                ct,
                allowEmptyInputForResolvedWorkflowService: resolvedDefinitionBinding != null,
                resolvedDefinitionBinding: resolvedDefinitionBinding)
            .ConfigureAwait(false);
    }

    public static ValueTask<ChatRunRequestNormalizationResult> NormalizeChatInputAsync(
        ChatInput input,
        IFileArtifactIngressPort? fileIngressPort,
        IReadOnlyDictionary<string, string>? defaultMetadata = null,
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? trustedCallerCredential = null,
        CancellationToken cancellationToken = default,
        string? trustedScopeId = null,
        bool allowEmptyInputForResolvedWorkflowService = false,
        Aevatar.Workflow.Abstractions.NyxIdCallerCredentialSelection? trustedNyxIdCredentialSelection = null) =>
        ChatRunRequestNormalizer.NormalizeAsync(
            input,
            fileIngressPort,
            defaultMetadata,
            trustedCallerCredential,
            cancellationToken,
            trustedScopeId,
            allowEmptyInputForResolvedWorkflowService,
            trustedNyxIdCredentialSelection);

    internal static async Task HandleChatPost(
        HttpContext http,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        [FromServices] WorkflowMultipartChatRequestParser multipartParser,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(chatRunService);
        ArgumentNullException.ThrowIfNull(multipartParser);

        var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
        if (!callerCredential.Succeeded)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(callerCredential.Error);
            var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(callerCredential.Error);
            await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
            return;
        }

        HttpChatInput input;
        string trustedScopeId;
        if (IsMultipartForm(http.Request.ContentType))
        {
            var scopeResolution = ResolvePostTrustedScope(http);
            if (!scopeResolution.Succeeded)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    scopeResolution.StatusCode,
                    scopeResolution.Code,
                    scopeResolution.Message,
                    ct);
                return;
            }

            trustedScopeId = scopeResolution.ScopeId!;
            var parsed = await multipartParser.ParseAsync(http, trustedScopeId, ct);
            if (!parsed.Succeeded)
            {
                await WriteJsonErrorResponseAsync(http, parsed.StatusCode, parsed.Code, parsed.Message, ct);
                return;
            }

            input = parsed.Input!;
        }
        else
        {
            if (!IsJson(http.Request.ContentType))
            {
                var error = ChatPostUnsupportedMediaType;
                await WriteJsonErrorResponseAsync(http, error.StatusCode, error.Code, error.Message, ct);
                return;
            }

            var parsed = await ParseJsonHttpChatInputAsync(http.Request, ct);
            if (!parsed.Succeeded)
            {
                if (parsed.Failure == JsonHttpChatInputParseFailure.InvalidConversationInput)
                {
                    var (code, message) = ChatRunStartErrorMapper.ToCommandError(
                        WorkflowChatRunStartError.InvalidConversationInput);
                    await WriteJsonErrorResponseAsync(
                        http,
                        StatusCodes.Status400BadRequest,
                        code,
                        message,
                        ct);
                }
                else
                {
                    await WriteJsonErrorResponseAsync(
                        http,
                        StatusCodes.Status400BadRequest,
                        "INVALID_CHAT_INPUT",
                        "Chat request body is invalid.",
                        ct);
                }

                return;
            }

            input = parsed.Input!;
            var scopeResolution = ResolvePostTrustedScope(http);
            if (!scopeResolution.Succeeded)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    scopeResolution.StatusCode,
                    scopeResolution.Code,
                    scopeResolution.Message,
                    ct);
                return;
            }

            trustedScopeId = scopeResolution.ScopeId!;
        }

        await HandleHttpChat(http, input, trustedScopeId, chatRunService, ct);
    }

    public static IEndpointRouteBuilder MapWorkflowChatInteractionEndpoints(this IEndpointRouteBuilder app)
    {
        return app;
    }

    public static async Task HandleChat(
        HttpContext http,
        ChatInput input,
        IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct = default,
        Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedHook = null,
        IFileArtifactIngressPort? fileIngressPort = null,
        bool allowEmptyInputForResolvedWorkflowService = false,
        WorkflowDefinitionBinding? resolvedDefinitionBinding = null)
    {
        using var scope = ApiRequestScope.BeginHttp();
        var serviceProvider = http.Features.Get<IServiceProvidersFeature>()?.RequestServices;
        var loggerFactory = serviceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        var logger = loggerFactory?.CreateLogger("Aevatar.Workflow.Host.Api.Chat");
        await using var writer = new ChatSseResponseWriter(http.Response, logger: logger);

        try
        {
            var defaultMetadata = TryResolveRuntimeDefaultMetadata(serviceProvider, logger);
            var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
            if (!callerCredential.Succeeded)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(callerCredential.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(callerCredential.Error);
                scope.MarkResult(statusCode);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
                return;
            }

            fileIngressPort ??= serviceProvider?.GetService<IFileArtifactIngressPort>();
            // 06-20-observatory-run-state-feed (R2d): derive the run scope_id from the authenticated caller
            // claim so the materialized current-state doc is attributed to the caller's observatory (and the
            // body-supplied scopeId cannot spoof another scope). When auth is disabled or no single scope
            // claim is present, the run is simply not claim-attributed (body scopeId is used as before).
            var trustedScopeId = Aevatar.Capabilities.AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId)
                ? callerScopeId
                : null;
            var firstInputFileRef = FirstInputFileRef(input.InputParts);
            logger?.LogWarning(
                "Workflow chat input file refs received. path={Path} sourceKind={SourceKind} sessionId={SessionId} scopeId={ScopeId} trustedScopeId={TrustedScopeId} requestInputPartCount={RequestInputPartCount} inputFileRefCount={InputFileRefCount} firstFileId={FirstFileId} firstArtifactId={FirstArtifactId} firstMediaType={FirstMediaType}",
                http.Request.Path.Value ?? string.Empty,
                input.Source?.Kind ?? string.Empty,
                input.SessionId ?? string.Empty,
                input.ScopeId ?? string.Empty,
                trustedScopeId ?? string.Empty,
                input.InputParts?.Count ?? 0,
                CountInputFileRefs(input.InputParts),
                firstInputFileRef?.FileId ?? string.Empty,
                firstInputFileRef?.ArtifactId ?? firstInputFileRef?.Uri ?? string.Empty,
                firstInputFileRef?.MediaType ?? string.Empty);
            var normalizedRequest = await ChatRunRequestNormalizer.NormalizeAsync(
                input,
                fileIngressPort,
                defaultMetadata,
                trustedCallerCredential: callerCredential.Credential,
                cancellationToken: ct,
                trustedScopeId: trustedScopeId,
                allowEmptyInputForResolvedWorkflowService: allowEmptyInputForResolvedWorkflowService,
                trustedNyxIdCredentialSelection: callerCredential.NyxIdCredentialSelection);
            if (!normalizedRequest.Succeeded)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(normalizedRequest.Error);
                scope.MarkResult(statusCode);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
                return;
            }

            var normalizedInputParts = normalizedRequest.Request!.InputParts;
            var firstNormalizedFileRef = FirstInputFileRef(normalizedInputParts);
            logger?.LogWarning(
                "Workflow chat input file refs normalized. path={Path} sessionId={SessionId} scopeId={ScopeId} normalizedInputPartCount={NormalizedInputPartCount} normalizedInputFileRefCount={NormalizedInputFileRefCount} firstFileId={FirstFileId} firstArtifactId={FirstArtifactId} firstMediaType={FirstMediaType}",
                http.Request.Path.Value ?? string.Empty,
                normalizedRequest.Request.SessionId ?? string.Empty,
                normalizedRequest.Request.ScopeId ?? string.Empty,
                normalizedInputParts?.Count ?? 0,
                CountInputFileRefs(normalizedInputParts),
                firstNormalizedFileRef?.FileId ?? string.Empty,
                firstNormalizedFileRef?.ArtifactId ?? string.Empty,
                firstNormalizedFileRef?.MediaType ?? string.Empty);

            var request = AttachResolvedDefinitionBinding(
                normalizedRequest.Request!,
                resolvedDefinitionBinding);
            var result = await chatRunService.ExecuteAsync(
                request,
                async (frame, token) =>
                {
                    await writer.WriteAsync(frame, token);
                    scope.RecordFirstResponse();
                },
                onAcceptedAsync: async (receipt, token) =>
                {
                    CapabilityTraceContext.ApplyCorrelationHeader(http.Response, receipt.Run.CorrelationId);
                    ExceptionDispatchInfo? acceptedHookFailure = null;
                    if (onAcceptedHook != null)
                    {
                        try
                        {
                            await onAcceptedHook(receipt.Run, token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            acceptedHookFailure = ExceptionDispatchInfo.Capture(ex);
                        }
                    }

                    await writer.StartAsync(token);
                    await writer.WriteAsync(BuildRunContextFrame(receipt.Run), token);
                    scope.RecordFirstResponse();
                    acceptedHookFailure?.Throw();
                },
                ct);

            if (!result.Succeeded)
            {
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(result.Error);
                scope.MarkResult(statusCode);
                if (!writer.Started)
                {
                    await WriteRunStartFailureResponseAsync(http, statusCode, result, ct);
                }
                else
                {
                    await WriteStreamRunStartFailureFrameAsync(writer, result, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            scope.MarkError();
            logger?.LogError(ex, "Workflow chat execution failed.");
            if (!writer.Started)
            {
                var (code, message) = WorkflowExecutionErrorMapper.ToError(ex);
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status500InternalServerError,
                    code,
                    message,
                    CancellationToken.None);
                return;
            }

            await WriteStreamErrorFrameAsync(writer, ex, logger, CancellationToken.None);
        }
    }

    private static WorkflowChatRunRequest AttachResolvedDefinitionBinding(
        WorkflowChatRunRequest request,
        WorkflowDefinitionBinding? resolvedDefinitionBinding) =>
        resolvedDefinitionBinding == null
            ? request
            : request with { ResolvedDefinitionBinding = resolvedDefinitionBinding };

    private static async Task HandleHttpChat(
        HttpContext http,
        HttpChatInput input,
        string trustedScopeId,
        IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct = default,
        IFileArtifactIngressPort? fileIngressPort = null,
        bool allowEmptyInputForResolvedWorkflowService = false,
        WorkflowDefinitionBinding? resolvedDefinitionBinding = null)
    {
        using var scope = ApiRequestScope.BeginHttp();
        var serviceProvider = http.Features.Get<IServiceProvidersFeature>()?.RequestServices;
        var loggerFactory = serviceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        var logger = loggerFactory?.CreateLogger("Aevatar.Workflow.Host.Api.Chat");
        await using var writer = new ChatSseResponseWriter(http.Response, logger: logger);

        try
        {
            var defaultMetadata = TryResolveRuntimeDefaultMetadata(serviceProvider, logger);
            var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
            if (!callerCredential.Succeeded)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(callerCredential.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(callerCredential.Error);
                scope.MarkResult(statusCode);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
                return;
            }

            fileIngressPort ??= serviceProvider?.GetService<IFileArtifactIngressPort>();
            var normalizedRequest = await ChatRunRequestNormalizer.NormalizeAsync(
                input,
                fileIngressPort,
                defaultMetadata,
                trustedCallerCredential: callerCredential.Credential,
                cancellationToken: ct,
                trustedScopeId: trustedScopeId,
                allowEmptyInputForResolvedWorkflowService: allowEmptyInputForResolvedWorkflowService,
                trustedNyxIdCredentialSelection: callerCredential.NyxIdCredentialSelection);
            if (!normalizedRequest.Succeeded)
            {
                var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(normalizedRequest.Error);
                scope.MarkResult(statusCode);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
                return;
            }

            var request = AttachResolvedDefinitionBinding(
                normalizedRequest.Request!,
                resolvedDefinitionBinding);
            var result = await chatRunService.ExecuteAsync(
                request,
                async (frame, token) =>
                {
                    await writer.WriteAsync(frame, token);
                    scope.RecordFirstResponse();
                },
                onAcceptedAsync: async (receipt, token) =>
                {
                    CapabilityTraceContext.ApplyCorrelationHeader(http.Response, receipt.Run.CorrelationId);
                    await writer.StartAsync(token);
                    if (receipt.ChatContext != null)
                        await writer.WriteAsync(BuildChatContextFrame(receipt.ChatContext), token);
                    await writer.WriteAsync(BuildRunContextFrame(receipt.Run), token);
                    scope.RecordFirstResponse();
                },
                ct);

            if (result is { Succeeded: true, Receipt: not null } && !writer.Started)
            {
                CapabilityTraceContext.ApplyCorrelationHeader(http.Response, result.Receipt.Run.CorrelationId);
                await writer.StartAsync(ct);
                if (result.Receipt.ChatContext != null)
                    await writer.WriteAsync(BuildChatContextFrame(result.Receipt.ChatContext), ct);
                await writer.WriteAsync(BuildRunContextFrame(result.Receipt.Run), ct);
                scope.RecordFirstResponse();
            }

            if (!result.Succeeded)
            {
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(result.Error);
                scope.MarkResult(statusCode);
                if (!writer.Started)
                {
                    await WriteRunStartFailureResponseAsync(http, statusCode, result, ct);
                }
                else
                {
                    await WriteStreamRunStartFailureFrameAsync(writer, result, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            scope.MarkError();
            logger?.LogError(ex, "Workflow chat execution failed.");
            if (!writer.Started)
            {
                var (code, message) = WorkflowExecutionErrorMapper.ToError(ex);
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status500InternalServerError,
                    code,
                    message,
                    CancellationToken.None);
                return;
            }

            await WriteStreamErrorFrameAsync(writer, ex, logger, CancellationToken.None);
        }
    }

    internal static async Task<IResult> HandleCommand(
        ChatInput input,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> chatRunService,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? defaultMetadata = null,
        IFileArtifactIngressPort? fileIngressPort = null)
    {
        using var scope = ApiRequestScope.BeginHttp();
        var logger = loggerFactory.CreateLogger("Aevatar.Workflow.Host.Api.Command");

        // 06-20-observatory-run-state-feed (R2d): the /command path has no HttpContext, so the run scope
        // cannot be derived from a caller claim — runs created here are explicitly out of AC3 (no per-caller
        // observatory attribution unless an explicit body scopeId is supplied). Intentional, not silently
        // mis-scoped.
        var normalizedRequest = await ChatRunRequestNormalizer.NormalizeAsync(
            input,
            fileIngressPort,
            defaultMetadata: defaultMetadata,
            cancellationToken: ct);
        if (!normalizedRequest.Succeeded)
        {
            var (code, message) = ChatRunStartErrorMapper.ToCommandError(normalizedRequest.Error);
            var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(normalizedRequest.Error);
            scope.MarkResult(statusCode);
            return Results.Json(new { code, message }, statusCode: statusCode);
        }

        try
        {
            var dispatchResult = await chatRunService.DispatchAsync(
                normalizedRequest.Request!,
                ct);

            if (!dispatchResult.Succeeded || dispatchResult.Receipt == null)
            {
                var mappedError = ChatRunStartErrorMapper.ToCommandError(dispatchResult.Error);
                var statusCode = ChatRunStartErrorMapper.ToHttpStatusCode(dispatchResult.Error);
                scope.MarkResult(statusCode);
                return Results.Json(
                    new { code = mappedError.Code, message = mappedError.Message },
                    statusCode: statusCode);
            }

            return Results.Accepted(
                BuildWorkflowRunStatusUrl(dispatchResult.Receipt.ActorId),
                CapabilityTraceContext.CreateAcceptedPayload(dispatchResult.Receipt));
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            scope.MarkError();
            logger.LogError(ex, "Workflow command execution failed before start signal");
            return Results.Json(
                new { code = "EXECUTION_FAILED", message = "Command execution failed." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public static async Task<IResult> HandleResume(
        WorkflowResumeInput input,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(resumeService);

        try
        {
            var actorId = (input.ActorId ?? string.Empty).Trim();
            var runId = (input.RunId ?? string.Empty).Trim();
            var stepId = (input.StepId ?? string.Empty).Trim();
            var commandId = NormalizeOptional(input.CommandId);
            if (string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(stepId))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new { error = "actorId, runId and stepId are required." });
            }

            if (input.ToolApproval != null &&
                (string.IsNullOrWhiteSpace(input.ToolApproval.ExecutionId) ||
                 string.IsNullOrWhiteSpace(input.ToolApproval.ToolCallId) ||
                 string.IsNullOrWhiteSpace(input.ToolApproval.ApprovalRequestId)))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new
                {
                    code = "INVALID_TOOL_APPROVAL_RESUME_REQUEST",
                    message = "toolApproval.executionId, toolApproval.toolCallId and " +
                              "toolApproval.approvalRequestId are required together when toolApproval is provided.",
                });
            }

            var dispatch = await resumeService.DispatchAsync(
                new WorkflowResumeCommand(
                    actorId,
                    runId,
                    stepId,
                    commandId,
                    input.Approved,
                    input.UserInput,
                    NormalizeMetadata(input.Metadata),
                    input.EditedContent,
                    input.Feedback,
                    ToolApproval: ToToolApprovalResumeCommand(input.ToolApproval)),
                ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                return MapRunControlDispatchFailure(dispatch.Error, scope);
            }

            // Refactor (iter56/cluster-891-endpoint-ack-honesty): old=200-shaped accepted, new=202 + Location
            //   Resume dispatch only proves inbox admission for the workflow actor, not applied continuation state.
            //   The workflow actor current-state read model remains the status resource for observing the run after acceptance.
            var statusUrl = BuildWorkflowRunStatusUrl(dispatch.Receipt);
            return Results.Accepted(statusUrl, new
            {
                accepted = true,
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                stepId,
                acceptedCommandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
                statusUrl,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.MarkError();
            throw;
        }
    }

    public static async Task<IResult> HandleSignal(
        WorkflowSignalInput input,
        [FromServices] ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(signalService);

        try
        {
            var actorId = (input.ActorId ?? string.Empty).Trim();
            var runId = (input.RunId ?? string.Empty).Trim();
            var signalName = (input.SignalName ?? string.Empty).Trim();
            var commandId = NormalizeOptional(input.CommandId);
            var stepId = NormalizeOptional(input.StepId);
            if (string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(signalName))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new { error = "actorId, runId and signalName are required." });
            }

            var dispatch = await signalService.DispatchAsync(
                new WorkflowSignalCommand(
                    actorId,
                    runId,
                    signalName,
                    commandId,
                    input.Payload,
                    stepId),
                ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                return MapRunControlDispatchFailure(dispatch.Error, scope);
            }

            // Refactor (iter56/cluster-891-endpoint-ack-honesty): old=200-shaped accepted, new=202 + Location
            //   Signal dispatch only proves inbox admission for the workflow actor, not applied signal handling.
            //   The workflow actor current-state read model remains the status resource for observing the run after acceptance.
            var statusUrl = BuildWorkflowRunStatusUrl(dispatch.Receipt);
            return Results.Accepted(statusUrl, new
            {
                accepted = true,
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                signalName,
                stepId,
                acceptedCommandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
                statusUrl,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.MarkError();
            throw;
        }
    }

    public static async Task<IResult> HandleStop(
        WorkflowStopInput input,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(stopService);

        try
        {
            var actorId = (input.ActorId ?? string.Empty).Trim();
            var runId = (input.RunId ?? string.Empty).Trim();
            var commandId = NormalizeOptional(input.CommandId);
            var reason = NormalizeOptional(input.Reason);
            if (string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(runId))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new { error = "actorId and runId are required." });
            }

            var dispatch = await stopService.DispatchAsync(
                new WorkflowStopCommand(
                    actorId,
                    runId,
                    commandId,
                    reason),
                ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                return MapRunControlDispatchFailure(dispatch.Error, scope);
            }

            // Refactor (iter56/cluster-891-endpoint-ack-honesty): old=200-shaped accepted, new=202 + Location
            //   Stop dispatch only proves inbox admission for the workflow actor, not terminal run state.
            //   The workflow actor current-state read model remains the status resource for observing the run after acceptance.
            var statusUrl = BuildWorkflowRunStatusUrl(dispatch.Receipt);
            return Results.Accepted(statusUrl, new
            {
                accepted = true,
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                reason,
                acceptedCommandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
                statusUrl,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.MarkError();
            throw;
        }
    }

    public static async Task<IResult> HandleRetryCompensation(
        WorkflowRetryCompensationInput input,
        [FromServices] ICommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> retryService,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(retryService);

        try
        {
            var actorId = (input.ActorId ?? string.Empty).Trim();
            var runId = (input.RunId ?? string.Empty).Trim();
            var failedCompensationStepId = (input.FailedCompensationStepId ?? string.Empty).Trim();
            var commandId = NormalizeOptional(input.CommandId);
            var reason = NormalizeOptional(input.Reason);
            if (string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(failedCompensationStepId))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new { error = "actorId, runId and failedCompensationStepId are required." });
            }

            var dispatch = await retryService.DispatchAsync(
                new WorkflowRetryCompensationCommand(
                    actorId,
                    runId,
                    failedCompensationStepId,
                    commandId,
                    reason),
                ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
                return MapRunControlDispatchFailure(dispatch.Error, scope);

            var statusUrl = BuildWorkflowRunStatusUrl(dispatch.Receipt);
            return Results.Accepted(statusUrl, new
            {
                accepted = true,
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                failedCompensationStepId,
                reason,
                acceptedCommandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
                statusUrl,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.MarkError();
            throw;
        }
    }

    public static async Task<IResult> HandleForkRun(
        WorkflowForkRunInput input,
        [FromServices] ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError> forkDispatchService,
        HttpContext? http = null,
        CancellationToken ct = default)
    {
        using var scope = ApiRequestScope.BeginHttp();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(forkDispatchService);

        try
        {
            var sourceRunId = (input.SourceRunId ?? string.Empty).Trim();
            var startAtStepId = (input.StartAtStepId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sourceRunId) ||
                string.IsNullOrWhiteSpace(startAtStepId))
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.BadRequest(new { error = "sourceRunId and startAtStepId are required." });
            }

            if (http == null ||
                !Aevatar.Capabilities.AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var trustedScopeId))
            {
                scope.MarkResult(StatusCodes.Status401Unauthorized);
                return Results.Unauthorized();
            }

            var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
            if (!callerCredential.Succeeded)
            {
                scope.MarkResult(StatusCodes.Status400BadRequest);
                return Results.Json(
                    new { error = "Caller credential is invalid.", code = "INVALID_CALLER_CREDENTIAL" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var dispatch = await forkDispatchService.DispatchAsync(
                new WorkflowForkRunCommand(
                    sourceRunId,
                    startAtStepId,
                    input.InlineYaml,
                    NormalizeStringMap(input.InlineSubYamls),
                    NormalizeStringMap(input.VariableOverrides),
                    input.Input,
                    NormalizeOptional(input.CommandId),
                    NormalizeOptional(input.CorrelationId),
                    ScopeId: trustedScopeId,
                    CallerCredential: callerCredential.Credential),
                ct);

            if (!dispatch.Succeeded || dispatch.Receipt == null)
                return MapForkRunFailure(dispatch.Error, scope);

            var statusUrl = BuildWorkflowRunStatusUrl(dispatch.Receipt);
            return Results.Accepted(statusUrl, new
            {
                accepted = true,
                sourceRunId = dispatch.Receipt.SourceRunId,
                newRunId = dispatch.Receipt.NewRunId,
                newRunActorId = dispatch.Receipt.NewRunActorId,
                workflowName = dispatch.Receipt.WorkflowName,
                acceptedCommandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
                statusUrl,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.MarkError();
            throw;
        }
    }

    private static string BuildWorkflowRunStatusUrl(WorkflowRunControlAcceptedReceipt receipt) =>
        BuildWorkflowRunStatusUrl(receipt.ActorId);

    // Implement (issue #3251):
    //   Behavior: fork receipts expose a routable run id while preserving the technical actor address.
    //   Why this shape: status links must not treat NewRunActorId as the run identity when NewRunId exists.
    private static string BuildWorkflowRunStatusUrl(WorkflowForkRunAcceptedReceipt receipt) =>
        string.IsNullOrWhiteSpace(receipt.NewRunId)
            ? BuildWorkflowRunStatusUrl(receipt.NewRunActorId)
            : $"/api/workflow/observatory/runs/{Uri.EscapeDataString(receipt.NewRunId)}";

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: accepted status links pointed at /api/actors/{actorId}.
    //   New principle: accepted status links point at the workflow actor current-state readmodel resource.
    private static string BuildWorkflowRunStatusUrl(string actorId) =>
        $"/api/workflow-actors/{Uri.EscapeDataString(actorId)}/current-state";

    private static WorkflowRunEventEnvelope BuildRunContextFrame(WorkflowChatRunAcceptedReceipt receipt) =>
        new()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.run.context",
                Payload = Any.Pack(new WorkflowRunContextPayload
                {
                    ActorId = receipt.ActorId,
                    WorkflowName = receipt.WorkflowName,
                    CommandId = receipt.CommandId,
                }),
            },
        };

    private static WorkflowRunEventEnvelope BuildChatContextFrame(WorkflowChatContext context) =>
        new()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.chat.context",
                Payload = Any.Pack(new WorkflowChatContextPayload
                {
                    ScopeId = context.ScopeId,
                    ConversationId = context.ConversationId,
                    TurnId = context.TurnId,
                    StateVersion = Math.Max(0, context.StateVersion),
                }),
            },
        };

    private static int CountInputFileRefs(IReadOnlyList<ChatInputContentPart>? inputParts) =>
        inputParts?.Count(static part => part.FileRef is not null && HasFileRefIdentity(part.FileRef)) ?? 0;

    private static ChatInputFileRef? FirstInputFileRef(IReadOnlyList<ChatInputContentPart>? inputParts) =>
        inputParts?.FirstOrDefault(static part => part.FileRef is not null && HasFileRefIdentity(part.FileRef))?.FileRef;

    private static bool HasFileRefIdentity(ChatInputFileRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId) ||
        !string.IsNullOrWhiteSpace(fileRef.Uri);

    private static int CountInputFileRefs(IReadOnlyList<WorkflowChatInputPart>? inputParts) =>
        inputParts?.Count(static part => part.FileRef is not null && HasFileRefIdentity(part.FileRef)) ?? 0;

    private static FileArtifactRef? FirstInputFileRef(IReadOnlyList<WorkflowChatInputPart>? inputParts) =>
        inputParts?.FirstOrDefault(static part => part.FileRef is not null && HasFileRefIdentity(part.FileRef))?.FileRef;

    private static bool HasFileRefIdentity(FileArtifactRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

    private static WorkflowToolApprovalResumeCommand? ToToolApprovalResumeCommand(
        WorkflowToolApprovalResumeInput? input)
    {
        if (input == null)
            return null;

        return new WorkflowToolApprovalResumeCommand(
            NormalizeRequired(input.ExecutionId, nameof(input.ExecutionId)),
            NormalizeRequired(input.ToolCallId, nameof(input.ToolCallId)),
            NormalizeRequired(input.ApprovalRequestId, nameof(input.ApprovalRequestId)));
    }

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = NormalizeOptional(value);
        if (normalized == null)
            throw new ArgumentException("Value is required.", name);

        return normalized;
    }

    private static IResult MapRunControlDispatchFailure(
        WorkflowRunControlStartError error,
        ApiRequestScope scope)
    {
        var (statusCode, message) = error.Code switch
        {
            WorkflowRunControlStartErrorCode.InvalidActorId => (
                StatusCodes.Status400BadRequest,
                "actorId is required."),
            WorkflowRunControlStartErrorCode.InvalidRunId => (
                StatusCodes.Status400BadRequest,
                "runId is required."),
            WorkflowRunControlStartErrorCode.InvalidStepId => (
                StatusCodes.Status400BadRequest,
                "stepId is required."),
            WorkflowRunControlStartErrorCode.InvalidSignalName => (
                StatusCodes.Status400BadRequest,
                "signalName is required."),
            WorkflowRunControlStartErrorCode.ActorNotFound => (
                StatusCodes.Status404NotFound,
                $"Actor '{error.ActorId}' not found."),
            WorkflowRunControlStartErrorCode.ActorNotWorkflowRun => (
                StatusCodes.Status400BadRequest,
                $"Actor '{error.ActorId}' is not a workflow run actor."),
            WorkflowRunControlStartErrorCode.RunBindingMissing => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' does not have a bound run id."),
            WorkflowRunControlStartErrorCode.RunBindingMismatch => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' is bound to run '{error.BoundRunId}', not '{error.RequestedRunId}'."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Workflow control dispatch failed."),
        };
        scope.MarkResult(statusCode);
        return Results.Json(new { error = message }, statusCode: statusCode);
    }

    private static IResult MapForkRunFailure(
        WorkflowForkRunStartError error,
        ApiRequestScope scope)
    {
        var (statusCode, message) = error.Code switch
        {
            WorkflowForkRunStartErrorCode.SourceRunNotFound => (
                StatusCodes.Status404NotFound,
                error.Reason),
            WorkflowForkRunStartErrorCode.SourceRunNotTerminal => (
                StatusCodes.Status409Conflict,
                error.Reason),
            WorkflowForkRunStartErrorCode.InvalidWorkflowYaml => (
                StatusCodes.Status400BadRequest,
                error.Reason),
            WorkflowForkRunStartErrorCode.StartStepNotFound => (
                StatusCodes.Status400BadRequest,
                error.Reason),
            WorkflowForkRunStartErrorCode.RunCreationFailed => (
                StatusCodes.Status502BadGateway,
                error.Reason),
            WorkflowForkRunStartErrorCode.DispatchFailed => (
                StatusCodes.Status502BadGateway,
                error.Reason),
            WorkflowForkRunStartErrorCode.InvalidCallerCredential => (
                StatusCodes.Status400BadRequest,
                error.Reason),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Workflow fork dispatch failed."),
        };
        scope.MarkResult(statusCode);
        return Results.Json(new { error = message }, statusCode: statusCode);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }

    private static IReadOnlyDictionary<string, string>? NormalizeMetadata(IDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return null;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var normalizedKey = NormalizeOptional(key);
            var normalizedValue = NormalizeOptional(value);
            if (normalizedKey == null || normalizedValue == null)
                continue;

            normalized[normalizedKey] = normalizedValue;
        }

        return normalized.Count == 0
            ? null
            : normalized;
    }

    private static IReadOnlyDictionary<string, string>? NormalizeStringMap(IDictionary<string, string>? map)
    {
        if (map == null || map.Count == 0)
            return null;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            var normalizedKey = NormalizeOptional(key);
            if (normalizedKey == null)
                continue;

            normalized[normalizedKey] = value ?? string.Empty;
        }

        return normalized.Count == 0
            ? null
            : normalized;
    }

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(
            new
            {
                code,
                message,
            },
            cancellationToken: ct);
    }

    private static async Task WriteRunStartFailureResponseAsync(
        HttpContext http,
        int statusCode,
        WorkflowChatRunInteractionResult result,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(
            result.FailureDetail == null
                ? ChatRunStartErrorMapper.ToErrorBody(result.Error)
                : ChatRunStartErrorMapper.ToErrorBody(result.FailureDetail),
            cancellationToken: ct);
    }

    private static async ValueTask<JsonHttpChatInputParseResult> ParseJsonHttpChatInputAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = await JsonSerializer.DeserializeAsync<HttpChatInput>(
                    request.Body,
                    ChatWebSocketProtocol.JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return input == null
                ? JsonHttpChatInputParseResult.Failed(JsonHttpChatInputParseFailure.InvalidChatInput)
                : JsonHttpChatInputParseResult.Success(input);
        }
        catch (JsonException ex)
        {
            return JsonHttpChatInputParseResult.Failed(
                IsConversationInputJsonException(ex)
                    ? JsonHttpChatInputParseFailure.InvalidConversationInput
                    : JsonHttpChatInputParseFailure.InvalidChatInput);
        }
    }

    private static bool IsConversationInputJsonException(JsonException ex) =>
        string.Equals(ex.Path, "$.conversation", StringComparison.OrdinalIgnoreCase) ||
        ex.Path?.StartsWith("$.conversation.", StringComparison.OrdinalIgnoreCase) == true ||
        ex.Path?.StartsWith("$.conversation[", StringComparison.OrdinalIgnoreCase) == true;

    private enum JsonHttpChatInputParseFailure
    {
        None = 0,
        InvalidChatInput = 1,
        InvalidConversationInput = 2,
    }

    private readonly record struct JsonHttpChatInputParseResult(
        HttpChatInput? Input,
        JsonHttpChatInputParseFailure Failure)
    {
        public bool Succeeded => Failure == JsonHttpChatInputParseFailure.None && Input != null;

        public static JsonHttpChatInputParseResult Success(HttpChatInput input) =>
            new(input, JsonHttpChatInputParseFailure.None);

        public static JsonHttpChatInputParseResult Failed(JsonHttpChatInputParseFailure failure) =>
            new(null, failure);
    }

    private static PostTrustedScopeResolution ResolvePostTrustedScope(HttpContext http)
    {
        if (!Aevatar.Capabilities.AevatarScopeAccessGuard.IsAuthenticationEnabled(http.RequestServices))
            return PostTrustedScopeResolution.ScopeDenied("Trusted scope context is required.");

        if (http.User?.Identity?.IsAuthenticated != true)
            return PostTrustedScopeResolution.AuthenticationRequired();

        var claimedScopeIds = http.User.Claims
            .Where(static claim =>
                string.Equals(claim.Type, "workflow.scope_id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "scope_id", StringComparison.OrdinalIgnoreCase))
            .Select(static claim => claim.Value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return claimedScopeIds.Count switch
        {
            1 => PostTrustedScopeResolution.Success(claimedScopeIds[0]!),
            0 => PostTrustedScopeResolution.ScopeDenied("Authenticated scope is missing."),
            _ => PostTrustedScopeResolution.ScopeDenied("Authenticated scope is ambiguous."),
        };
    }

    private sealed record PostTrustedScopeResolution(
        bool Succeeded,
        string? ScopeId,
        int StatusCode,
        string Code,
        string Message)
    {
        public static PostTrustedScopeResolution Success(string scopeId) =>
            new(true, scopeId, StatusCodes.Status200OK, string.Empty, string.Empty);

        public static PostTrustedScopeResolution AuthenticationRequired() =>
            new(false, null, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "Authentication is required.");

        public static PostTrustedScopeResolution ScopeDenied(string message) =>
            new(false, null, StatusCodes.Status403Forbidden, "SCOPE_ACCESS_DENIED", message);
    }

    private static bool IsMultipartForm(string? contentType) =>
        contentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsJson(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ||
        contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteStreamErrorFrameAsync(
        ChatSseResponseWriter writer,
        Exception ex,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            var (code, message) = WorkflowExecutionErrorMapper.ToError(ex);
            await writer.WriteAsync(
                new WorkflowRunEventEnvelope
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RunError = new WorkflowRunErrorEventPayload
                    {
                        Code = code,
                        Message = message,
                    },
                },
                ct);
        }
        catch (IOException writeEx)
        {
            logger?.LogDebug(writeEx, "Failed to write SSE error frame because the stream is no longer writable.");
        }
        catch (ObjectDisposedException writeEx)
        {
            logger?.LogDebug(writeEx, "Failed to write SSE error frame because the stream is no longer writable.");
        }
        catch (InvalidOperationException writeEx)
        {
            logger?.LogDebug(writeEx, "Failed to write SSE error frame because the stream is no longer writable.");
        }
        catch (OperationCanceledException writeEx)
        {
            logger?.LogDebug(writeEx, "Failed to write SSE error frame because the stream is no longer writable.");
        }
        catch (Exception writeEx)
        {
            logger?.LogWarning(writeEx, "Unexpected failure while writing SSE error frame.");
        }
    }

    private static async Task WriteStreamRunStartFailureFrameAsync(
        ChatSseResponseWriter writer,
        WorkflowChatRunInteractionResult result,
        CancellationToken ct)
    {
        var (code, message) = result.FailureDetail == null
            ? ChatRunStartErrorMapper.ToCommandError(result.Error)
            : ChatRunStartErrorMapper.ToCommandError(result.FailureDetail);
        await writer.WriteAsync(
            new WorkflowRunEventEnvelope
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RunError = new WorkflowRunErrorEventPayload
                {
                    Code = code,
                    Message = message,
                },
            },
            ct);
    }

    public static class WorkflowExecutionErrorMapper
    {
        public const string CompatibilityErrorCode = "WORKFLOW_REVISION_INCOMPATIBLE";
        private const string DescriptorMissingMarker = "Type registry has no descriptor for type name";

        public static (string Code, string Message) ToError(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);

            if (FindException<CommandObservationTimeoutException>(ex) != null)
            {
                return (
                    "RUN_OBSERVATION_TIMEOUT",
                    "Run was accepted but did not become observable before the deadline.");
            }

            return IsCompatibilityFailure(ex)
                ? (
                    CompatibilityErrorCode,
                    "Workflow revision is incompatible with this backend. Re-publish or migrate the workflow/service revision.")
                : (
                    "EXECUTION_FAILED",
                    "Workflow execution failed.");
        }

        public static bool IsCompatibilityFailure(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);

            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is WorkflowExpectedExecutionModeCompatibilityException)
                    return true;
                if (current.Message.Contains(DescriptorMissingMarker, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static TException? FindException<TException>(Exception ex)
            where TException : Exception
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is TException matched)
                    return matched;
            }

            return null;
        }
    }

    internal static async Task HandleChatWebSocket(
        HttpContext http,
        IWorkflowChatRunInteractionPort chatRunService,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default,
        IFileArtifactIngressPort? fileIngressPort = null)
    {
        using var scope = ApiRequestScope.BeginWebSocket();
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Expected websocket request.", ct);
            return;
        }

        // 06-20-observatory-run-state-feed (R2d): derive the run scope_id from the authenticated WS principal
        // (read before accepting the socket, while the HTTP context still carries the claims), mirroring the
        // SSE path. When no single scope claim is present, the run is not claim-attributed (body scopeId is
        // used as before).
        var trustedScopeId = Aevatar.Capabilities.AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId)
            ? callerScopeId
            : null;

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var logger = loggerFactory?.CreateLogger("Aevatar.Workflow.Host.Api.Chat.WebSocket");
        var responseMessageType = WebSocketMessageType.Text;

        try
        {
            var incomingFrame = await ChatWebSocketProtocol.ReceiveAsync(socket, ct);
            responseMessageType = incomingFrame.HasValue
                ? ChatWebSocketProtocol.NormalizeMessageType(incomingFrame.Value.MessageType)
                : WebSocketMessageType.Text;

            if (!ChatWebSocketCommandParser.TryParse(incomingFrame, out var command, out var parseError))
            {
                var parseContext = CapabilityTraceContext.CreateMessageContext(fallbackCorrelationId: parseError.RequestId ?? string.Empty);
                await ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateCommandError(
                        parseError.RequestId,
                        parseError.Code,
                        parseError.Message,
                        parseContext.CorrelationId),
                    ct,
                    parseError.ResponseMessageType);
                scope.RecordFirstResponse();
                return;
            }

            responseMessageType = ChatWebSocketProtocol.NormalizeMessageType(command.ResponseMessageType);
            var defaultMetadata = TryResolveRuntimeDefaultMetadata(http.RequestServices, logger);
            fileIngressPort ??= http.RequestServices.GetService<IFileArtifactIngressPort>();
            await ChatWebSocketRunCoordinator.ExecuteAsync(
                socket,
                command,
                chatRunService,
                scope,
                ct,
                defaultMetadata,
                fileIngressPort,
                trustedScopeId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            scope.MarkError();
            logger?.LogWarning(ex, "Failed to execute websocket chat command");
            if (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var failureContext = CapabilityTraceContext.CreateMessageContext();
                await ChatWebSocketProtocol.SendAsync(
                    socket,
                    ChatWebSocketEnvelopeFactory.CreateCommandError(
                        requestId: null,
                        code: "RUN_EXECUTION_FAILED",
                        message: "Failed to execute run.",
                        correlationId: failureContext.CorrelationId),
                    ct,
                    responseMessageType);
            }
        }
        finally
        {
            await ChatWebSocketProtocol.CloseAsync(socket, ct);
        }
    }

    private static IReadOnlyDictionary<string, string> TryResolveRuntimeDefaultMetadata(
        IServiceProvider? serviceProvider,
        ILogger? logger)
    {
        if (serviceProvider == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var configuration = serviceProvider.GetService(typeof(IConfiguration)) as IConfiguration;
            return ParseRuntimeDefaultMetadata(configuration);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to resolve workflow runtime default metadata from configuration.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseRuntimeDefaultMetadata(IConfiguration? configuration)
    {
        if (configuration == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var section = configuration.GetSection(WorkflowRuntimeDefaultsSectionName);
        if (!section.Exists())
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in section.AsEnumerable(makePathsRelative: true))
        {
            var normalizedKey = NormalizeRuntimeDefaultKey(rawKey);
            var normalizedValue = string.IsNullOrWhiteSpace(rawValue) ? string.Empty : rawValue.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;

            metadata[normalizedKey] = normalizedValue;
        }

        return metadata;
    }

    private static string NormalizeRuntimeDefaultKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return string.Empty;

        return rawKey
            .Trim()
            .Replace(':', '.');
    }

}
