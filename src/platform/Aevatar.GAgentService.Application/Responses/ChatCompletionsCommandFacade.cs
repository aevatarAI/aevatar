using System.Security.Cryptography;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter344/cluster-001):
//   Old pattern: Host handler owns caller resolution, route resolution, session registration, tool planning, direct provider execution, status updates, and protocol rendering in one request stack.
//   New principle: Host maps HTTP/OpenAI frames only; typed Application facade owns Normalize -> Resolve Target -> Build Context -> Build Envelope -> Dispatch -> Receipt/Observe via the same LlmSessionGAgent run path as Responses/Messages.
public sealed class ChatCompletionsCommandFacade(
    IResponsesCallerScopeResolver callerScopeResolver,
    IResponsesChatRouteDecisionPort chatRouteDecisionPort,
    IResponsesRouteResolver routeResolver,
    ILlmSessionRegistrationPort sessionRegistrationPort,
    IActorDispatchPort dispatchPort,
    IResponsesToolClassificationService toolClassificationService,
    IResponsesDirectToolPlanService directToolPlanService,
    ILogger<ChatCompletionsCommandFacade> logger) : IChatCompletionsCommandFacade
{
    private const string RegistrationScopeMetadataKey = "scope_id";

    public async Task<ChatCompletionsCreateCommandResult> CreateAsync(
        ChatCompletionsCommandRequest request,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerScopeContext);

        var normalizedResult = Normalize(request);
        if (!normalizedResult.Succeeded)
        {
            return ChatCompletionsCreateCommandResult.FromError(
                400,
                normalizedResult.ErrorCode ?? "invalid_request_error",
                normalizedResult.ErrorMessage ?? "Invalid request.");
        }

        var normalized = normalizedResult.Request!;
        var callerScopeResult = await ResolveCallerScopeAsync(callerScopeContext, ct);
        if (callerScopeResult.Error is not null)
            return ChatCompletionsCreateCommandResult.FromError(
                callerScopeResult.Error.StatusCode,
                callerScopeResult.Error.Code,
                callerScopeResult.Error.Message);

        var routedModelResult = await ResolveRouteTargetAsync(normalized, callerScopeResult.Scope!, ct);
        if (routedModelResult.Error is not null)
            return ChatCompletionsCreateCommandResult.FromError(
                routedModelResult.Error.StatusCode,
                routedModelResult.Error.Code,
                routedModelResult.Error.Message);

        var createdAt = DateTimeOffset.UtcNow;
        var sessionResult = await RegisterSessionAsync(normalized, callerScopeResult.Scope!, createdAt, ct);
        if (sessionResult.Error is not null)
            return ChatCompletionsCreateCommandResult.FromError(
                sessionResult.Error.StatusCode,
                sessionResult.Error.Code,
                sessionResult.Error.Message);

        var planResult = await BuildExecutionPlanAsync(
            normalized,
            callerScopeResult.Scope!,
            routedModelResult.Model!,
            routedModelResult.Action!,
            callerScopeContext.InboundBearerToken,
            sessionResult.Session!,
            createdAt,
            ct);
        if (planResult.Error is not null)
            return ChatCompletionsCreateCommandResult.FromError(
                planResult.Error.StatusCode,
                planResult.Error.Code,
                planResult.Error.Message);

        return normalized.Stream
            ? ChatCompletionsCreateCommandResult.FromStreamPlan(planResult.Plan!)
            : await ExecuteNonStreamingAsync(planResult.Plan!, ct);
    }

    public async Task<ResponsesStreamCommandResult> StreamAsync(
        ChatCompletionsCreateCommandPlan plan,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(onTextDelta);

        try
        {
            var admission = await DispatchRunAsync(plan, ct);
            return ResponsesStreamCommandResult.FromAccepted(new ResponsesStreamAcceptedCommandResult(admission));
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(401, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(ResolveUpstreamStatusCode(ex), ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ResponsesStreamCommandResult.FromError(499, "client_closed_request", "Client closed request.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Streaming /v1/chat/completions {CompletionId} failed", plan.Normalized.CompletionId);
            return ResponsesStreamCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    private static ChatCompletionsRequestNormalizationResult Normalize(ChatCompletionsCommandRequest request)
    {
        var model = request.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return ChatCompletionsRequestNormalizationResult.Failed("model_required", "model is required.");

        if (request.MaxTokens is <= 0)
            return ChatCompletionsRequestNormalizationResult.Failed("invalid_max_tokens", "max_tokens must be greater than zero when provided.");

        if (request.Temperature is < 0 or > 2)
            return ChatCompletionsRequestNormalizationResult.Failed("invalid_temperature", "temperature must be between 0 and 2.");

        if (request.ChatMessages.Count == 0)
            return ChatCompletionsRequestNormalizationResult.Failed("invalid_messages", "messages must contain at least one entry.");

        return ChatCompletionsRequestNormalizationResult.Success(new NormalizedChatCompletionsCommand(
            "chatcmpl_" + NewOpaqueId(),
            model,
            request.Stream == true,
            request.IncludeUsageInStream,
            request.Temperature,
            request.MaxTokens,
            request.ChatMessages,
            request.DeclaredTools));
    }

    private async Task<CallerScopeResult> ResolveCallerScopeAsync(
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct)
    {
        try
        {
            var callerScope = await callerScopeResolver.ResolveAsync(callerScopeContext, ct);
            return new CallerScopeResult(callerScope, null);
        }
        catch (ResponsesCallerScopeUnavailableException ex)
        {
            return new CallerScopeResult(null, new ResponsesCommandError(401, "authentication_required", ex.Message));
        }
    }

    private async Task<RouteTargetResult> ResolveRouteTargetAsync(
        NormalizedChatCompletionsCommand normalized,
        ResponsesCallerScope callerScope,
        CancellationToken ct)
    {
        var routeDecision = await chatRouteDecisionPort.ResolveAsync(
            callerScope,
            normalized.Model,
            normalized.DeclaredTools.Count > 0 ? ToolMode.Declared : ToolMode.None,
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

        var action = routeDecision.Action.Clone();
        var routedModel = !string.IsNullOrWhiteSpace(action.ForwardToModel?.ModelName)
            ? action.ForwardToModel.ModelName.Trim()
            : normalized.Model;
        if (action.ForwardToModel is null)
            action.ForwardToModel = new ForwardToModel();

        action.ForwardToModel.ModelName = routedModel;
        return RouteTargetResult.FromModel(routedModel, action);
    }

    private async Task<SessionRegistrationResult> RegisterSessionAsync(
        NormalizedChatCompletionsCommand normalized,
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
            logger.LogError(ex, "Failed to register llm session for chat completion {CompletionId}", normalized.CompletionId);
            return new SessionRegistrationResult(null, new ResponsesCommandError(500, "api_error", "Failed to register session."));
        }
    }

    private async Task<ExecutionPlanResult> BuildExecutionPlanAsync(
        NormalizedChatCompletionsCommand normalized,
        ResponsesCallerScope callerScope,
        string routedModel,
        ChatRouteAction routeAction,
        string bearerToken,
        LlmSessionRegistrationResult session,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var toolProviderContext = BuildToolProviderContext(callerScope, normalized.CompletionId, bearerToken);
        var toolPlan = directToolPlanService.Build(routeAction);
        if (toolPlan.Error is not null)
            return ExecutionPlanResult.FromError(toolPlan.Error);

        var toolClassification = await toolClassificationService.ClassifyAsync(
            normalized.DeclaredTools,
            toolProviderContext,
            toolPlan.AdditionalToolProviders,
            ct: ct);
        var (effectiveModel, resolvedRouteValue) = await ResolveModelRouteAsync(routedModel, bearerToken, ct);
        var llmRequest = BuildLlmRequest(
            normalized,
            callerScope,
            bearerToken,
            effectiveModel,
            resolvedRouteValue,
            toolClassification);

        return ExecutionPlanResult.FromPlan(new ChatCompletionsCreateCommandPlan(
            normalized,
            session,
            llmRequest,
            toolProviderContext.ToolContextMetadata,
            toolClassification,
            toolPlan.ToolChoiceHintPlan,
            createdAt));
    }

    private async Task<ChatCompletionsCreateCommandResult> ExecuteNonStreamingAsync(
        ChatCompletionsCreateCommandPlan plan,
        CancellationToken ct)
    {
        try
        {
            var admission = await DispatchRunAsync(plan, ct);
            return ChatCompletionsCreateCommandResult.FromAccepted(new ChatCompletionsCreateAcceptedCommandResult(
                plan.Normalized,
                plan.CreatedAt.ToUnixTimeSeconds(),
                plan.Session,
                admission));
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ChatCompletionsCreateCommandResult.FromError(401, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            return ChatCompletionsCreateCommandResult.FromError(ResolveUpstreamStatusCode(ex), ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return ChatCompletionsCreateCommandResult.FromError(499, "client_closed_request", "Client closed request.");
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(plan.Session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Unexpected error processing /v1/chat/completions {CompletionId}", plan.Normalized.CompletionId);
            return ChatCompletionsCreateCommandResult.FromError(500, "api_error", "Internal server error.");
        }
    }

    private async Task<(string EffectiveModel, string? ResolvedRouteValue)> ResolveModelRouteAsync(
        string routedModel,
        string bearerToken,
        CancellationToken ct)
    {
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
        }

        return (effectiveModel, resolvedRouteValue);
    }

    private static LLMRequest BuildLlmRequest(
        NormalizedChatCompletionsCommand normalized,
        ResponsesCallerScope callerScope,
        string bearerToken,
        string effectiveModel,
        string? resolvedRouteValue,
        ResponsesToolClassification toolClassification)
    {
        var llmMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.CompletionId,
            [RegistrationScopeMetadataKey] = callerScope.ScopeId,
        };
        return new LLMRequest
        {
            Messages = [.. normalized.ChatMessages],
            RequestId = normalized.CompletionId,
            Metadata = llmMetadata,
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.CompletionId,
                new LLMRequestCallerCredentials(bearerToken)),
            Tools = toolClassification.EffectiveTools,
            LlmControl = new LLMControlContext(
                NyxIdAccessToken: null,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                ModelOverride: null,
                NyxIdRoutePreference: resolvedRouteValue,
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null),
            Model = effectiveModel,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxTokens,
        };
    }

    private static ResponsesToolProviderContext BuildToolProviderContext(
        ResponsesCallerScope callerScope,
        string responseId,
        string bearerToken) =>
        new(
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

    private static string BuildRouteContentHint(NormalizedChatCompletionsCommand normalized) =>
        normalized.ChatMessages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content
        ?? normalized.ChatMessages.LastOrDefault()?.Content
        ?? string.Empty;

    private static LlmSessionRecord BuildSessionRecord(
        NormalizedChatCompletionsCommand normalized,
        ResponsesCallerScope callerScope,
        DateTimeOffset createdAt) =>
        new()
        {
            ResponseId = normalized.CompletionId,
            ScopeId = callerScope.ScopeId,
            OwnerSubject = callerScope.OwnerSubject,
            OriginKind = callerScope.OriginKind,
            PreviousResponseId = string.Empty,
            Status = LlmSessionStatus.Accepted,
            CreatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            UpdatedAt = Timestamp.FromDateTime(createdAt.UtcDateTime),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };

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

    private Task<DispatchAdmission> DispatchRunAsync(
        ChatCompletionsCreateCommandPlan plan,
        CancellationToken ct)
    {
        var command = BuildRunRequested(
            plan.Session.ResponseId,
            plan.LlmRequest,
            plan.ToolClassification,
            plan.ToolChoiceHintPlan,
            plan.CreatedAt);
        var envelope = ServiceCommandEnvelopeFactory.Create(
            plan.Session.ActorId,
            command,
            command.RunId);
        return dispatchPort.DispatchAsync(plan.Session.ActorId, envelope, ct);
    }

    private static LlmRunRequested BuildRunRequested(
        string responseId,
        LLMRequest request,
        ResponsesToolClassification toolClassification,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan,
        DateTimeOffset requestedAt)
    {
        var command = new LlmRunRequested
        {
            ResponseId = responseId,
            RunId = $"{responseId}:llm-run",
            Model = request.Model ?? string.Empty,
            RoutePreference = request.LlmControl?.NyxIdRoutePreference ?? string.Empty,
            ScopeId = request.CallerContext?.ScopeId ?? string.Empty,
            OwnerSubject = request.CallerContext?.OwnerSubject ?? string.Empty,
            BearerToken = request.CallerContext?.Credentials?.NyxIdBearer ?? string.Empty,
            RequestedAt = Timestamp.FromDateTimeOffset(requestedAt),
        };
        if (request.Temperature is not null)
            command.Temperature = request.Temperature.Value;
        if (request.MaxTokens is not null)
            command.MaxTokens = request.MaxTokens.Value;
        command.Messages.AddRange(request.Messages.Select(ToRuntimeMessage));
        command.ToolSelection = ToToolSelection(toolClassification, toolChoiceHintPlan);
        return command;
    }

    private static LlmSessionRuntimeChatMessage ToRuntimeMessage(ChatMessage message)
    {
        var result = new LlmSessionRuntimeChatMessage
        {
            Role = message.Role,
            Content = message.Content ?? string.Empty,
            ReasoningContent = message.ReasoningContent ?? string.Empty,
            ToolCallId = message.ToolCallId ?? string.Empty,
        };
        if (message.ToolCalls is { Count: > 0 })
            result.ToolCalls.AddRange(message.ToolCalls.Select(static call => new LlmSessionRuntimeToolCall
            {
                CallId = call.Id,
                ToolName = call.Name,
                ArgumentsJson = call.ArgumentsJson,
            }));
        return result;
    }

    private static LlmSessionRuntimeToolSelection ToToolSelection(
        ResponsesToolClassification classification,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan)
    {
        var selection = new LlmSessionRuntimeToolSelection
        {
            SubstitutedToolNames = { classification.SubstitutedToolNames },
            AdditiveToolNames = { classification.AdditiveToolNames },
        };
        if (!toolChoiceHintPlan.IsEmpty)
        {
            selection.ToolChoiceHintName = toolChoiceHintPlan.ToolName;
            selection.ToolChoiceHintArgumentsJson = toolChoiceHintPlan.PrefilledArgumentsJson();
        }

        selection.ForwardedTools.AddRange(classification.ForwardedTools.Select(static tool =>
            new LlmSessionRuntimeToolDeclaration
            {
                ToolName = tool.Name,
                Description = tool.Description,
                ParametersJson = tool.ParametersJson,
                SchemaHash = tool.SchemaHash,
            }));
        return selection;
    }

    private static int ResolveUpstreamStatusCode(NyxIdUpstreamException ex) =>
        ex.Status switch
        {
            400 => 400,
            401 => 401,
            403 => 403,
            404 => 404,
            429 => 429,
            >= 500 => 502,
            _ => 502,
        };

    private static string NewOpaqueId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed record CallerScopeResult(
        ResponsesCallerScope? Scope,
        ResponsesCommandError? Error);

    private sealed record RouteTargetResult(
        string? Model,
        ChatRouteAction? Action,
        ResponsesCommandError? Error)
    {
        public static RouteTargetResult FromModel(string model, ChatRouteAction action) => new(model, action, null);

        public static RouteTargetResult FromError(int statusCode, string code, string message) =>
            new(null, null, new ResponsesCommandError(statusCode, code, message));
    }

    private sealed record SessionRegistrationResult(
        LlmSessionRegistrationResult? Session,
        ResponsesCommandError? Error);

    private sealed record ExecutionPlanResult(
        ChatCompletionsCreateCommandPlan? Plan,
        ResponsesCommandError? Error)
    {
        public static ExecutionPlanResult FromPlan(ChatCompletionsCreateCommandPlan plan) => new(plan, null);

        public static ExecutionPlanResult FromError(ResponsesCommandError error) => new(null, error);
    }
}
