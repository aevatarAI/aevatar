using System.Diagnostics.CodeAnalysis;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Presentation.AGUI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal static partial class ResponsesApiEndpoints
{
    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Mainnet Minimal API handlers (ResponsesEndpoints / MessagesEndpoints) inject long lists of application/runtime collaborators and perform caller resolution / route / session / LLM orchestration inline.
    //   New principle: Host handlers parse/authenticate HTTP only + delegate to typed Application command/query facade that owns Normalize -> Resolve Target -> Build Context -> Dispatch/Observe lifecycle. SSE rendering stays at the boundary.
    //   Refactor helper, no behavior change.
    private sealed class ResponsesCommandFacade(
        ILLMProviderFactory providerFactory,
        IResponsesCallerScopeResolver callerScopeResolver,
        IChatRoutePolicyQueryPort chatRoutePolicyQueryPort,
        ChatRouteResolver chatRouteResolver,
        IResponsesRouteResolver routeResolver,
        ILlmSessionRegistrationPort responseSessionRegistrationPort,
        ILlmSessionQueryPort responseSessionQueryPort,
        IResponsesCompletionApplicationService completionService,
        IEnumerable<IResponsesToolProvider> toolProviders,
        ITeamEntryMemberResolver teamEntryMemberResolver,
        IMemberPublishedServiceResolver memberPublishedServiceResolver,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        ILoggerFactory loggerFactory)
    {
        public Task<IResult> CreateAsync(
            HttpContext http,
            ResponsesCreateRequest request,
            string bearerToken,
            CancellationToken ct) =>
            ExecuteCreateResponseAsync(
                http,
                request,
                bearerToken,
                providerFactory,
                callerScopeResolver,
                chatRoutePolicyQueryPort,
                chatRouteResolver,
                routeResolver,
                responseSessionRegistrationPort,
                responseSessionQueryPort,
                completionService,
                toolProviders,
                teamEntryMemberResolver,
                memberPublishedServiceResolver,
                staticGAgentStreamInvocationPort,
                loggerFactory,
                ct);

        public Task<IResult> CancelAsync(
            string responseId,
            string bearerToken,
            HttpContext http,
            CancellationToken ct) =>
            ExecuteCancelResponseAsync(
                http,
                responseId,
                bearerToken,
                callerScopeResolver,
                responseSessionRegistrationPort,
                responseSessionQueryPort,
                ct);

        [SuppressMessage(
        "Maintainability",
        "CA1506:Avoid excessive class coupling",
        Justification = "Refactor facade owns Responses command orchestration while SSE shaping remains in this boundary.")]
        private static async Task<IResult> ExecuteCreateResponseAsync(
        HttpContext http,
        ResponsesCreateRequest request,
        string bearerToken,
        ILLMProviderFactory providerFactory,
        IResponsesCallerScopeResolver callerScopeResolver,
        IChatRoutePolicyQueryPort chatRoutePolicyQueryPort,
        ChatRouteResolver chatRouteResolver,
        IResponsesRouteResolver routeResolver,
        ILlmSessionRegistrationPort responseSessionRegistrationPort,
        ILlmSessionQueryPort responseSessionQueryPort,
        IResponsesCompletionApplicationService completionService,
        IEnumerable<IResponsesToolProvider> toolProviders,
        ITeamEntryMemberResolver teamEntryMemberResolver,
        IMemberPublishedServiceResolver memberPublishedServiceResolver,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(callerScopeResolver);
        ArgumentNullException.ThrowIfNull(chatRoutePolicyQueryPort);
        ArgumentNullException.ThrowIfNull(chatRouteResolver);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(responseSessionRegistrationPort);
        ArgumentNullException.ThrowIfNull(responseSessionQueryPort);
        ArgumentNullException.ThrowIfNull(completionService);
        ArgumentNullException.ThrowIfNull(toolProviders);
        ArgumentNullException.ThrowIfNull(teamEntryMemberResolver);
        ArgumentNullException.ThrowIfNull(memberPublishedServiceResolver);
        ArgumentNullException.ThrowIfNull(staticGAgentStreamInvocationPort);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(request);
        var logger = loggerFactory.CreateLogger("Aevatar.Mainnet.Host.Api.Responses");

        var normalizedResult = ResponsesRequestNormalizer.Normalize(request);
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
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", ex.Message);
        }

        // Implement (issue #694):
        //   Behavior: /v1/responses applies chat-route model overrides before LLM dispatch.
        //   Why this shape: the endpoint keeps its existing session/tool flow and consumes only the transient target action.
        var routedModel = normalized.Model;
        var routeDecision = await ResolveResponsesChatRouteAsync(
            chatRoutePolicyQueryPort,
            chatRouteResolver,
            callerScope,
            normalized.Model,
            ResolveToolMode(normalized.DeclaredTools.Count, normalized.ToolResults.Count),
            BuildContentHint(normalized.Prompt),
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
        else if (routeDecision.Action.ForwardToTeam is not null)
        {
            // Bypass the LLM session/provider path entirely: ForwardToTeam runs a
            // Studio team entry-member as an ephemeral GAgent via
            // IStaticGAgentStreamInvocationPort, then maps AGUI events back to
            // OpenAI Responses SSE / JSON. The caller still sees Responses-shaped
            // results so /v1/responses stays protocol-neutral as far as routing target.
            return await HandleForwardToTeamAsync(
                http,
                normalized,
                callerScope,
                routeDecision.Action.ForwardToTeam,
                teamEntryMemberResolver,
                staticGAgentStreamInvocationPort,
                logger,
                ct);
        }
        else if (routeDecision.Action.ForwardToGagent is not null)
        {
            // Mirrors ForwardToTeam: bypass LLM session/provider entirely and run a
            // single Studio member as an ephemeral GAgent via
            // IStaticGAgentStreamInvocationPort, mapping AGUI back to OpenAI
            // Responses SSE / JSON. The proto field is named `actor_id` for
            // historical reasons (Voice / NyxIdChat-relay treat it as a raw Orleans
            // grain key); on the LLM facade — which has no raw-actor binding — the
            // field is interpreted as a Studio memberId resolved via
            // IMemberPublishedServiceResolver. This asymmetry is documented in
            // ADR-0024 D5 and matches issue #588's invariant that every invoke
            // resolves to a member identity.
            return await HandleForwardToGAgentAsync(
                http,
                normalized,
                callerScope,
                routeDecision.Action.ForwardToGagent,
                memberPublishedServiceResolver,
                staticGAgentStreamInvocationPort,
                logger,
                ct);
        }

        LlmSessionSnapshot? previousSnapshot = null;
        if (normalized.PreviousResponseId is not null)
        {
            previousSnapshot = await responseSessionQueryPort.GetByResponseIdAsync(normalized.PreviousResponseId, ct);
            var previousError = ValidatePreviousResponse(previousSnapshot, callerScope);
            if (previousError is not null)
                return previousError;
        }

        if (normalized.ToolResults.Count > 0 && previousSnapshot is null)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "previous_response_required",
                "function_call_output requires previous_response_id.");
        }

        if (previousSnapshot is not null &&
            TryBuildAlreadyResolvedToolResultResponse(normalized, previousSnapshot, out var alreadyResolvedResult))
        {
            return alreadyResolvedResult;
        }

        if (previousSnapshot is not null)
        {
            var toolResultError = await PersistIncomingToolResultsAsync(
                responseSessionRegistrationPort,
                previousSnapshot,
                normalized,
                ct);
            if (toolResultError is not null)
                return toolResultError;
        }

        var createdAt = DateTimeOffset.UtcNow;
        LlmSessionRegistrationResult responseSession;
        try
        {
            responseSession = await responseSessionRegistrationPort.RegisterAsync(
                BuildResponseSessionRecord(normalized, callerScope, createdAt),
                ct);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var correlation = LogAndCorrelate(logger, ex, "session_registration", normalized.ResponseId);
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "session_registration_failed",
                $"Failed to register response session. Correlation: {correlation}");
        }

        var toolProviderContext = BuildToolProviderContext(callerScope, normalized.ResponseId, bearerToken);
        var toolClassification = await ResponsesToolClassifier.ClassifyAsync(
            normalized.DeclaredTools.Select(ToApplicationToolDeclaration).ToArray(),
            toolProviders,
            toolProviderContext,
            logger,
            ct);
        // Refactor (iter26/cluster-026-responses-route-user-catalog-cache):
        //   Old pattern: Responses/Messages routes resolve `vendor/model` by reading a singleton per-bearer in-process cache of NyxID user LLM service catalog facts.
        //   New principle: Resolve model route from the current catalog read in the request flow; do not store user route facts in singleton process memory.
        // OpenRouter-style vendor prefix: the catalog advertises every model as
        // `{slug}/{model}` regardless of route shape (gateway provider, user
        // service, proxy service). When the slug resolves to a known catalog
        // entry, pin its RouteValue (full path — e.g. `/api/v1/llm/anthropic/v1`
        // for gateway providers, `/api/v1/proxy/s/<slug>` for proxy services)
        // as the per-request route preference so NyxIdLLMProvider routes to
        // the right plane. An unknown slug (catalog miss, or a model name that
        // just happens to contain `/`) falls through to default gateway routing
        // with the model string preserved verbatim — NyxID's gateway picks the
        // backend by model name.
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

        // LLMRequest.Metadata flows into the LLM provider, where its values may be
        // serialized into logs, traces, or third-party SDKs. Keep only safe-to-log
        // tracing/config values here. Business-control identity and per-request
        // credentials live on the typed CallerContext below; the LLM provider
        // (e.g. NyxIdLLMProvider) reads the bearer from Credentials, not Metadata.
        var llmMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.ResponseId,
            [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
        };
        if (resolvedRouteValue is not null)
            llmMetadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = resolvedRouteValue;
        var toolContextMetadata = toolProviderContext.ToolContextMetadata;

        var llmRequest = new LLMRequest
        {
            Messages = BuildLlmMessages(normalized, previousSnapshot),
            RequestId = normalized.ResponseId,
            Metadata = llmMetadata,
            CallerContext = new LLMRequestCallerContext(
                callerScope.ScopeId,
                callerScope.OwnerSubject,
                normalized.ResponseId,
                new LLMRequestCallerCredentials(bearerToken)),
            Tools = toolClassification.EffectiveTools,
            // LLM provider receives the bare model name (vendor prefix already
            // consumed into NyxIdRoutePreference above). Response-snapshot
            // echoes still use normalized.Model so the client sees back what it sent.
            Model = effectiveModel,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxOutputTokens,
        };

        if (normalized.Stream)
        {
            await WriteStreamResponseAsync(
                http.Response,
                providerFactory,
                completionService,
                responseSessionRegistrationPort,
                logger,
                responseSession,
                llmRequest,
                toolContextMetadata,
                normalized,
                previousSnapshot,
                toolClassification,
                createdAt,
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
            var forwardedToolCalls = completion.ForwardedToolCalls;
            await PersistForwardedToolCallsAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                toolClassification,
                forwardedToolCalls,
                DateTimeOffset.UtcNow,
                ct);
            await TryResolveIncomingToolResultsAsync(
                responseSessionRegistrationPort,
                logger,
                previousSnapshot,
                normalized,
                ct);
            var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Completed,
                ct);
            var completed = BuildCompletedResponse(
                normalized,
                createdAt.ToUnixTimeSeconds(),
                completedAt,
                completion.Text,
                forwardedToolCalls,
                completion.Usage is null ? null : MapUsage(completion.Usage));
            return Results.Json(completed, statusCode: StatusCodes.Status200OK);
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            // Authentication failure messages from NyxID are intentionally surfaced
            // — they describe why the caller's own token was rejected and don't
            // contain server-side internals.
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", ex.Message);
        }
        catch (NyxIdUpstreamException ex)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            var statusCode = ex.Status switch
            {
                401 or 403 => StatusCodes.Status401Unauthorized,
                429 => StatusCodes.Status429TooManyRequests,
                503 => StatusCodes.Status503ServiceUnavailable,
                >= 500 => StatusCodes.Status502BadGateway,
                400 or 404 or 409 or 422 => ex.Status.Value,
                _ => StatusCodes.Status502BadGateway,
            };

            var correlation = LogAndCorrelate(logger, ex, "nyxid_upstream", normalized.ResponseId);
            return ToErrorResult(
                statusCode,
                ex.Kind.ToString().ToLowerInvariant(),
                $"Upstream provider error. Correlation: {correlation}");
        }
        catch (OperationCanceledException)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Cancelled,
                CancellationToken.None);
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TryUpdateSessionStatusAsync(
                responseSessionRegistrationPort,
                logger,
                responseSession,
                LlmSessionStatus.Failed,
                CancellationToken.None);
            var correlation = LogAndCorrelate(logger, ex, "execution", normalized.ResponseId);
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "execution_failed",
                $"Execution failed. Correlation: {correlation}");
        }
    }

    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Mainnet Minimal API handlers (ResponsesEndpoints / MessagesEndpoints) inject long lists of application/runtime collaborators and perform caller resolution / route / session / LLM orchestration inline.
    //   New principle: Host handlers parse/authenticate HTTP only + delegate to typed Application command/query facade that owns Normalize -> Resolve Target -> Build Context -> Dispatch/Observe lifecycle. SSE rendering stays at the boundary.
        private static async Task<IResult> ExecuteCancelResponseAsync(
        HttpContext http,
        string responseId,
        string bearerToken,
        IResponsesCallerScopeResolver callerScopeResolver,
        ILlmSessionRegistrationPort responseSessionRegistrationPort,
        ILlmSessionQueryPort responseSessionQueryPort,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(callerScopeResolver);
        ArgumentNullException.ThrowIfNull(responseSessionRegistrationPort);
        ArgumentNullException.ThrowIfNull(responseSessionQueryPort);

        ResponsesCallerScope callerScope;
        try
        {
            callerScope = await callerScopeResolver.ResolveAsync(bearerToken, http, ct);
        }
        catch (ResponsesCallerScopeUnavailableException ex)
        {
            return ToErrorResult(StatusCodes.Status401Unauthorized, "authentication_required", ex.Message);
        }

        var snapshot = await responseSessionQueryPort.GetByResponseIdAsync(responseId, ct);
        var visibilityError = ValidateResponseVisibility(
            snapshot,
            callerScope,
            "response_not_found",
            "response id does not refer to a visible response session.");
        if (visibilityError is not null)
            return visibilityError;

        var visibleSnapshot = snapshot!;
        if (visibleSnapshot.Status == LlmSessionStatus.Expired)
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "response_expired",
                "response id refers to an expired response session.");
        }

        var cancelledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (visibleSnapshot.Status != LlmSessionStatus.Cancelled)
        {
            try
            {
                await responseSessionRegistrationPort.UpdateStatusAsync(
                    visibleSnapshot.ActorId,
                    visibleSnapshot.ResponseId,
                    LlmSessionStatus.Cancelled,
                    ct);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(StatusCodes.Status408RequestTimeout);
            }
            catch (InvalidOperationException ex)
            {
                // InvalidOperationException here originates from the actor's
                // own validation messages (e.g. terminal-state guard). They're
                // safe to surface — they describe the protocol violation, not
                // server internals.
                return ToErrorResult(
                    StatusCodes.Status400BadRequest,
                    "response_cancel_rejected",
                    ex.Message);
            }
        }

        return Results.Json(new
        {
            id = visibleSnapshot.ResponseId,
            @object = "response",
            status = "cancelled",
            cancelled_at = cancelledAt,
        }, JsonOptions, statusCode: StatusCodes.Status200OK);
    }
    }
}
