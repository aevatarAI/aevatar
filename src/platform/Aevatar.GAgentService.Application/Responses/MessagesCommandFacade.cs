using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Anthropic Messages Host handler normalized, resolved chat route, registered sessions, classified tools, and built LLM requests inline.
//   New principle: Application owns the Messages command lifecycle as a typed facade; Host only maps Anthropic HTTP/SSE/JSON frames.
public sealed class MessagesCommandFacade(
    IResponsesCallerScopeResolver callerScopeResolver,
    IChatRoutePolicyQueryPort chatRoutePolicyQueryPort,
    ChatRouteResolver chatRouteResolver,
    IResponsesRouteResolver routeResolver,
    ILlmSessionRegistrationPort sessionRegistrationPort,
    IResponsesCompletionApplicationService completionService,
    ILLMProviderFactory providerFactory,
    ILogger<MessagesCommandFacade> logger) : IMessagesCommandFacade
{
    private const string RegistrationScopeMetadataKey = "scope_id";

    public async Task<MessagesCreateCommandResult> CreateAsync(
        MessagesCommandRequest request,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedResult = MessagesRequestNormalizer.Normalize(request);
        if (!normalizedResult.Succeeded)
        {
            return MessagesCreateCommandResult.FromError(
                400,
                normalizedResult.ErrorCode ?? "invalid_request_error",
                normalizedResult.ErrorMessage ?? "Invalid request.");
        }

        var normalized = normalizedResult.Request!;
        var callerScopeResult = await ResolveCallerScopeAsync(bearerToken, ct);
        if (callerScopeResult.Error is not null)
            return MessagesCreateCommandResult.FromError(
                callerScopeResult.Error.StatusCode,
                "authentication_error",
                callerScopeResult.Error.Message);

        var routedModelResult = await ResolveRouteTargetAsync(normalized, callerScopeResult.Scope!, ct);
        if (routedModelResult.Error is not null)
            return MessagesCreateCommandResult.FromError(
                routedModelResult.Error.StatusCode,
                routedModelResult.Error.Code,
                routedModelResult.Error.Message);

        var sessionResult = await RegisterSessionAsync(normalized, callerScopeResult.Scope!, DateTimeOffset.UtcNow, ct);
        if (sessionResult.Error is not null)
            return MessagesCreateCommandResult.FromError(
                sessionResult.Error.StatusCode,
                sessionResult.Error.Code,
                sessionResult.Error.Message);

        var plan = await BuildExecutionPlanAsync(
            normalized,
            callerScopeResult.Scope!,
            routedModelResult.Model!,
            bearerToken,
            sessionResult.Session!,
            ct);

        return normalized.Stream
            ? MessagesCreateCommandResult.FromStreamPlan(plan)
            : await ExecuteNonStreamingAsync(plan, ct);
    }

    public async Task<ResponsesStreamCommandResult> StreamAsync(
        MessagesCreateCommandPlan plan,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(onTextDelta);

        try
        {
            var provider = providerFactory.GetDefault();
            var completion = await completionService.StreamAsync(
                provider,
                plan.LlmRequest,
                plan.ToolContextMetadata,
                plan.ToolClassification,
                onTextDelta,
                ct);
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Completed, ct);
            return ResponsesStreamCommandResult.FromCompleted(
                completion.Text,
                completion.ForwardedToolCalls,
                completion.Usage);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(401, "authentication_error", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(ex.Status ?? 502, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(499, "client_closed_request", "Client closed request.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Streaming /v1/messages {MessageId} failed", plan.Normalized.MessageId);
            return ResponsesStreamCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    private async Task<CallerScopeResult> ResolveCallerScopeAsync(string bearerToken, CancellationToken ct)
    {
        try
        {
            var callerScope = await callerScopeResolver.ResolveAsync(bearerToken, ct);
            return new CallerScopeResult(callerScope, null);
        }
        catch (ResponsesCallerScopeUnavailableException ex)
        {
            return new CallerScopeResult(null, new ResponsesCommandError(401, "authentication_error", ex.Message));
        }
    }

    private async Task<RouteTargetResult> ResolveRouteTargetAsync(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        CancellationToken ct)
    {
        var routeDecision = await ResolveResponsesChatRouteAsync(
            callerScope,
            normalized.Model,
            ResolveToolMode(normalized.DeclaredTools.Count, inlineToolResultCount: 0),
            BuildRouteContentHint(normalized),
            ct);

        if (routeDecision.Action.Reject is not null)
        {
            return RouteTargetResult.FromError(
                403,
                "chat_route_rejected",
                string.IsNullOrWhiteSpace(routeDecision.Action.Reject.Reason)
                    ? "The chat route policy rejected this request."
                    : routeDecision.Action.Reject.Reason);
        }

        if (!string.IsNullOrWhiteSpace(routeDecision.Action.ForwardToModel?.ModelName))
            return RouteTargetResult.FromModel(routeDecision.Action.ForwardToModel.ModelName.Trim());

        if (routeDecision.Action.ForwardToGagent is not null)
        {
            return RouteTargetResult.FromError(
                501,
                "chat_route_action_not_supported",
                "ForwardToGAgent is not supported by /v1/messages in v1.");
        }

        return RouteTargetResult.FromModel(normalized.Model);
    }

    private async Task<SessionRegistrationResult> RegisterSessionAsync(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        try
        {
            var session = await sessionRegistrationPort.RegisterAsync(
                BuildSessionRecord(normalized, callerScope, createdAt),
                ct);
            return new SessionRegistrationResult(session, null);
        }
        catch (OperationCanceledException)
        {
            return new SessionRegistrationResult(null, new ResponsesCommandError(408, "request_timeout", "Request timed out."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to register llm session for message {MessageId}", normalized.MessageId);
            return new SessionRegistrationResult(null, new ResponsesCommandError(500, "api_error", "Failed to register session."));
        }
    }

    private async Task<MessagesCreateCommandPlan> BuildExecutionPlanAsync(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        string routedModel,
        string bearerToken,
        LlmSessionRegistrationResult session,
        CancellationToken ct)
    {
        var toolProviderContext = BuildToolProviderContext(callerScope, normalized.MessageId, bearerToken);
        var toolClassification = await ResponsesToolClassifier.ClassifyAsync(
            normalized.DeclaredTools,
            Array.Empty<IResponsesToolProvider>(),
            toolProviderContext,
            logger,
            ct);
        var (effectiveModel, resolvedRouteValue) = await ResolveModelRouteAsync(routedModel, bearerToken, ct);
        var llmRequest = BuildLlmRequest(
            normalized,
            callerScope,
            bearerToken,
            effectiveModel,
            resolvedRouteValue,
            toolClassification);
        if (normalized.DroppedImageContent)
        {
            logger.LogWarning(
                "Image content blocks dropped from Messages request {MessageId}; Path B is text-only in v1.",
                normalized.MessageId);
        }

        return new MessagesCreateCommandPlan(
            normalized,
            session,
            llmRequest,
            toolProviderContext.ToolContextMetadata,
            toolClassification);
    }

    private async Task<MessagesCreateCommandResult> ExecuteNonStreamingAsync(
        MessagesCreateCommandPlan plan,
        CancellationToken ct)
    {
        try
        {
            var provider = providerFactory.GetDefault();
            var completion = await completionService.CollectAsync(
                provider,
                plan.LlmRequest,
                plan.ToolContextMetadata,
                plan.ToolClassification,
                ct);
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Completed, ct);
            return MessagesCreateCommandResult.FromCompleted(new MessagesCreateCompletedCommandResult(plan.Normalized, completion));
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return MessagesCreateCommandResult.FromError(401, "authentication_error", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            var statusCode = ex.Status switch
            {
                400 => 400,
                401 => 401,
                403 => 403,
                404 => 404,
                429 => 429,
                >= 500 => 502,
                _ => 502,
            };
            return MessagesCreateCommandResult.FromError(statusCode, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return MessagesCreateCommandResult.FromError(499, "client_closed_request", "Client closed request.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Unexpected error processing /v1/messages {MessageId}", plan.Normalized.MessageId);
            return MessagesCreateCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    private async Task<(string EffectiveModel, string? ResolvedRouteValue)> ResolveModelRouteAsync(
        string routedModel,
        string bearerToken,
        CancellationToken ct)
    {
        var anthropicPrefixed = false;
        if (!routedModel.Contains('/', StringComparison.Ordinal))
        {
            routedModel = $"anthropic/{routedModel}";
            anthropicPrefixed = true;
        }

        var modelRoute = ResponsesModelRouteParser.Parse(routedModel);
        var effectiveModel = routedModel;
        string? resolvedRouteValue = null;
        if (modelRoute.RouteSlug is not null)
        {
            resolvedRouteValue = await routeResolver
                .ResolveRouteValueAsync(modelRoute.RouteSlug, bearerToken, ct)
                .ConfigureAwait(false);
            if (resolvedRouteValue is not null)
                effectiveModel = modelRoute.Model;
            else if (anthropicPrefixed)
                effectiveModel = modelRoute.Model;
        }

        return (effectiveModel, resolvedRouteValue);
    }

    private static LLMRequest BuildLlmRequest(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        string bearerToken,
        string effectiveModel,
        string? resolvedRouteValue,
        ResponsesToolClassification toolClassification)
    {
        var llmMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.MessageId,
            [RegistrationScopeMetadataKey] = callerScope.ScopeId,
        };
        if (resolvedRouteValue is not null)
            llmMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = resolvedRouteValue;

        return new LLMRequest
        {
            Messages = [.. normalized.ChatMessages],
            RequestId = normalized.MessageId,
            Metadata = llmMetadata,
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.MessageId,
                new LLMRequestCallerCredentials(bearerToken)),
            Tools = toolClassification.EffectiveTools,
            Model = effectiveModel,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxTokens,
        };
    }

    private static ResponsesToolProviderContext BuildToolProviderContext(
        ResponsesCallerScope callerScope,
        string responseId,
        string bearerToken)
    {
        return new ResponsesToolProviderContext(
            new ResponsesToolProviderCallerScope(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                callerScope.OriginKind.ToString()),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = responseId,
                [LLMRequestMetadataKeys.ResponseId] = responseId,
                [LLMRequestMetadataKeys.ScopeId] = callerScope.ScopeId,
                [LLMRequestMetadataKeys.OwnerSubject] = callerScope.OwnerSubject,
                [RegistrationScopeMetadataKey] = callerScope.ScopeId,
                [LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken,
            });
    }

    private async Task<ChatRouteDecision> ResolveResponsesChatRouteAsync(
        ResponsesCallerScope callerScope,
        string model,
        ToolMode toolMode,
        string contentHint,
        CancellationToken ct)
    {
        var ownerScope = OwnerScope.ForNyxIdNative(callerScope.ScopeId);
        var snapshot = await chatRoutePolicyQueryPort.LookupForCallerAsync(ownerScope, ct);
        return chatRouteResolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.NyxResponses,
            CallerScope = new ChatRouteCallerScope
            {
                NyxUserId = ownerScope.NyxUserId,
                Platform = ownerScope.Platform,
                RegistrationScopeId = ownerScope.RegistrationScopeId,
                SenderId = ownerScope.SenderId,
            },
            Channel = string.Empty,
            CommandName = string.Empty,
            ContentHint = contentHint,
            ToolMode = toolMode,
            Model = model,
        });
    }

    private static ToolMode ResolveToolMode(int declaredToolCount, int inlineToolResultCount)
    {
        if (inlineToolResultCount > 0)
            return ToolMode.Inline;
        return declaredToolCount > 0 ? ToolMode.Declared : ToolMode.None;
    }

    private static string BuildRouteContentHint(NormalizedMessagesRequest normalized) =>
        normalized.ChatMessages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content
        ?? normalized.ChatMessages.LastOrDefault()?.Content
        ?? string.Empty;

    private static LlmSessionRecord BuildSessionRecord(
        NormalizedMessagesRequest normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt)
    {
        return new LlmSessionRecord
        {
            ResponseId = normalized.MessageId,
            ScopeId = callerScope.ScopeId,
            OwnerSubject = callerScope.OwnerSubject,
            OriginKind = callerScope.OriginKind,
            PreviousResponseId = string.Empty,
            Status = LlmSessionStatus.Accepted,
            CreatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            UpdatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };
    }

    private async Task TryUpdateSessionStatusAsync(
        LlmSessionRegistrationResult session,
        LlmSessionStatus status,
        CancellationToken ct)
    {
        try
        {
            await sessionRegistrationPort.UpdateStatusAsync(session.ActorId, session.ResponseId, status, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update llm session {ResponseId} to {Status}", session.ResponseId, status);
        }
    }

    private sealed record CallerScopeResult(
        ResponsesCallerScope? Scope,
        ResponsesCommandError? Error);

    private sealed record RouteTargetResult(
        string? Model,
        ResponsesCommandError? Error)
    {
        public static RouteTargetResult FromModel(string model) => new(model, null);

        public static RouteTargetResult FromError(int statusCode, string code, string message) =>
            new(null, new ResponsesCommandError(statusCode, code, message));
    }

    private sealed record SessionRegistrationResult(
        LlmSessionRegistrationResult? Session,
        ResponsesCommandError? Error);
}
