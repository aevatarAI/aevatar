using System.Diagnostics.CodeAnalysis;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Messages;

internal static partial class MessagesApiEndpoints
{
    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Mainnet Minimal API handlers (ResponsesEndpoints / MessagesEndpoints) inject long lists of application/runtime collaborators and perform caller resolution / route / session / LLM orchestration inline.
    //   New principle: Host handlers parse/authenticate HTTP only + delegate to typed Application command/query facade that owns Normalize -> Resolve Target -> Build Context -> Dispatch/Observe lifecycle. SSE rendering stays at the boundary.
    //   Refactor helper, no behavior change.
    private sealed class MessagesCommandFacade(
        ILLMProviderFactory providerFactory,
        IResponsesCallerScopeResolver callerScopeResolver,
        IChatRoutePolicyQueryPort chatRoutePolicyQueryPort,
        ChatRouteResolver chatRouteResolver,
        IResponsesRouteResolver routeResolver,
        ILlmSessionRegistrationPort sessionRegistrationPort,
        IResponsesCompletionApplicationService completionService,
        ILoggerFactory loggerFactory)
    {
        public Task<IResult> CreateAsync(
            HttpContext http,
            MessagesCreateRequest request,
            string bearerToken,
            CancellationToken ct) =>
            ExecuteCreateMessageAsync(
                http,
                request,
                bearerToken,
                providerFactory,
                callerScopeResolver,
                chatRoutePolicyQueryPort,
                chatRouteResolver,
                routeResolver,
                sessionRegistrationPort,
                completionService,
                loggerFactory,
                ct);

        [SuppressMessage(
        "Maintainability",
        "CA1506:Avoid excessive class coupling",
        Justification = "Refactor facade owns Messages command orchestration while SSE shaping remains in this boundary.")]
        private static async Task<IResult> ExecuteCreateMessageAsync(
        HttpContext http,
        MessagesCreateRequest request,
        string bearerToken,
        ILLMProviderFactory providerFactory,
        IResponsesCallerScopeResolver callerScopeResolver,
        IChatRoutePolicyQueryPort chatRoutePolicyQueryPort,
        ChatRouteResolver chatRouteResolver,
        IResponsesRouteResolver routeResolver,
        ILlmSessionRegistrationPort sessionRegistrationPort,
        IResponsesCompletionApplicationService completionService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(callerScopeResolver);
        ArgumentNullException.ThrowIfNull(chatRoutePolicyQueryPort);
        ArgumentNullException.ThrowIfNull(chatRouteResolver);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(sessionRegistrationPort);
        ArgumentNullException.ThrowIfNull(completionService);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(request);
        var logger = loggerFactory.CreateLogger("Aevatar.Mainnet.Host.Api.Messages");

        var normalizedResult = MessagesRequestNormalizer.Normalize(request);
        if (!normalizedResult.Succeeded)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                normalizedResult.ErrorCode ?? "invalid_request_error",
                normalizedResult.ErrorMessage ?? "Invalid request.");
        }

        var normalized = normalizedResult.Request!;
        ResponsesCallerScope callerScope;
        try
        {
            callerScope = await callerScopeResolver.ResolveAsync(bearerToken, http, ct);
        }
        catch (ResponsesCallerScopeUnavailableException ex)
        {
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_error", ex.Message);
        }

        // Implement (issue #694):
        //   Behavior: Anthropic Messages facade applies the same chat-route model override as Responses.
        //   Why this shape: Messages shares the LlmSession/LLMRequest path, so routing stays protocol-neutral.
        var routedModel = normalized.Model;
        var routeDecision = await ResponsesApiEndpoints.ResolveResponsesChatRouteAsync(
            chatRoutePolicyQueryPort,
            chatRouteResolver,
            callerScope,
            normalized.Model,
            ResponsesApiEndpoints.ResolveToolMode(normalized.DeclaredTools.Count, inlineToolResultCount: 0),
            ResponsesApiEndpoints.BuildContentHint(BuildRouteContentHint(normalized)),
            ct);
        if (routeDecision.Action.Reject is not null)
            return ToErrorResult(
                StatusCodes.Status403Forbidden,
                "chat_route_rejected",
                string.IsNullOrWhiteSpace(routeDecision.Action.Reject.Reason)
                    ? "The chat route policy rejected this request."
                    : routeDecision.Action.Reject.Reason);
        if (!string.IsNullOrWhiteSpace(routeDecision.Action.ForwardToModel?.ModelName))
        {
            routedModel = routeDecision.Action.ForwardToModel.ModelName.Trim();
        }
        else if (routeDecision.Action.ForwardToGagent is not null)
        {
            return ToErrorResult(
                StatusCodes.Status501NotImplemented,
                "chat_route_action_not_supported",
                "ForwardToGAgent is not supported by /v1/messages in v1.");
        }

        // Path B is stateless: register a new LlmSession per request, no
        // previous_response_id continuation. The session id mirrors the
        // Anthropic message id so projection/audit can correlate.
        var createdAt = DateTimeOffset.UtcNow;
        LlmSessionRegistrationResult session;
        try
        {
            session = await sessionRegistrationPort.RegisterAsync(
                BuildSessionRecord(normalized, callerScope, createdAt),
                ct);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to register llm session for message {MessageId}", normalized.MessageId);
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "api_error",
                "Failed to register session.");
        }

        // Tools come from the inbound declaration only. Substitute/additive
        // tool providers (TodoWrite, Task, WebFetch) are intentionally NOT
        // injected here because Anthropic Messages clients (Claude Code in
        // particular) ship their own tool harness on top of the response;
        // injecting Aevatar's substitutes would shadow client tools.
        var toolProviderContext = ResponsesApiEndpoints.BuildToolProviderContext(
            callerScope,
            normalized.MessageId,
            bearerToken);
        var toolClassification = await ResponsesToolClassifier.ClassifyAsync(
            normalized.DeclaredTools,
            Array.Empty<IResponsesToolProvider>(),
            toolProviderContext,
            logger,
            ct);

        // /v1/messages is the Anthropic Messages facade. Native Anthropic
        // clients (Claude Code, Cursor's Anthropic mode, Anthropic SDK) send
        // raw Anthropic model ids without a provider prefix
        // (e.g. `claude-sonnet-4-5-20250929`). Without normalization those
        // strings have no `/` so the catalog router treats them as
        // gateway-default, and NyxID's gateway then rejects them with HTTP 400
        // because it doesn't know to route a bare `claude-*` to the anthropic
        // backend. Auto-prefix `anthropic/` so the existing OpenRouter-style
        // routing below resolves to `/api/v1/llm/anthropic/v1` for any caller
        // that doesn't hand-prefix the model. If the route resolver doesn't
        // recognize `anthropic` we fall back to the original bare name (which
        // was the pre-fix behavior), so this change is strictly additive.
        var anthropicPrefixed = false;
        if (!routedModel.Contains('/', StringComparison.Ordinal))
        {
            routedModel = $"anthropic/{routedModel}";
            anthropicPrefixed = true;
        }

        // Refactor (iter26/cluster-026-responses-route-user-catalog-cache):
        //   Old pattern: Responses/Messages routes resolve `vendor/model` by reading a singleton per-bearer in-process cache of NyxID user LLM service catalog facts.
        //   New principle: Resolve model route from the current catalog read in the request flow; do not store user route facts in singleton process memory.
        // OpenRouter-style vendor prefix routing (same as Path A). If the
        // model is `vendor/name`, resolve the route value through the catalog;
        // unknown slugs fall through to gateway default.
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
            {
                // Resolver doesn't know the synthesized "anthropic" slug;
                // fall back to the original bare model so downstream behavior
                // matches pre-fix code paths and tests that wire a no-op
                // resolver keep working.
                routedModel = modelRoute.Model;
                effectiveModel = modelRoute.Model;
            }
        }

        var llmMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.MessageId,
            [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
        };
        if (resolvedRouteValue is not null)
            llmMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = resolvedRouteValue;

        var toolContextMetadata = toolProviderContext.ToolContextMetadata;

        var llmRequest = new LLMRequest
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

        if (normalized.DroppedImageContent)
        {
            logger.LogWarning(
                "Image content blocks dropped from Messages request {MessageId}; Path B is text-only in v1.",
                normalized.MessageId);
        }

        if (normalized.Stream)
        {
            await WriteStreamingMessageAsync(
                http.Response,
                providerFactory,
                completionService,
                sessionRegistrationPort,
                logger,
                session,
                llmRequest,
                toolContextMetadata,
                normalized,
                toolClassification,
                ct);
            return Results.Empty;
        }

        try
        {
            var provider = providerFactory.GetDefault();
            var completion = await completionService.CollectAsync(
                provider,
                llmRequest,
                toolContextMetadata,
                toolClassification,
                ct);
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Completed, ct);
            return Results.Json(
                BuildCompletedMessage(normalized, completion),
                JsonOptions,
                statusCode: StatusCodes.Status200OK);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_error", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            var statusCode = ex.Status switch
            {
                400 => StatusCodes.Status400BadRequest,
                401 => StatusCodes.Status401Unauthorized,
                403 => StatusCodes.Status403Forbidden,
                404 => StatusCodes.Status404NotFound,
                429 => StatusCodes.Status429TooManyRequests,
                >= 500 => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status502BadGateway,
            };
            return ToErrorResult(statusCode, ex.Kind.ToString().ToLowerInvariant(), ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Cancelled, CancellationToken.None);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            await TryUpdateSessionStatusAsync(sessionRegistrationPort, logger, session, LlmSessionStatus.Failed, CancellationToken.None);
            logger.LogError(ex, "Unexpected error processing /v1/messages {MessageId}", normalized.MessageId);
            return ToErrorResult(StatusCodes.Status500InternalServerError, "api_error", "Internal server error.");
        }
    }
    }
}
