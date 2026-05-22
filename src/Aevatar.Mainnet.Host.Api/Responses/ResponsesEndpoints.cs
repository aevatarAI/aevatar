using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Presentation.AGUI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal static partial class ResponsesApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapResponsesApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1").WithTags("Responses");
        // Auth is endpoint-internal: each handler manually extracts the inbound
        // bearer and resolves the caller via NyxID `/me`. Opt out of the host's
        // FallbackPolicy.RequireAuthenticatedUser() so opaque NyxID API keys
        // (non-JWT) reach the handler instead of being 401'd by JwtBearer.
        group.MapPost("/responses", HandleCreateResponseAsync).AllowAnonymous();
        group.MapPost("/responses/{id}/cancel", HandleCancelResponseAsync).AllowAnonymous();
        group.MapGet("/models", HandleListModelsAsync).AllowAnonymous();
        return app;
    }

    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Mainnet Minimal API handlers (ResponsesEndpoints / MessagesEndpoints) inject long lists of application/runtime collaborators and perform caller resolution / route / session / LLM orchestration inline.
    //   New principle: Host handlers parse/authenticate HTTP only + delegate to typed Application command/query facade that owns Normalize -> Resolve Target -> Build Context -> Dispatch/Observe lifecycle. SSE rendering stays at the boundary.
    internal static async Task<IResult> HandleCreateResponseAsync(
        HttpContext http,
        ResponsesCreateRequest request,
        [FromServices] IResponsesCommandFacade commandFacade,
        [FromServices] ITeamEntryMemberResolver teamEntryMemberResolver,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandFacade);
        ArgumentNullException.ThrowIfNull(teamEntryMemberResolver);
        ArgumentNullException.ThrowIfNull(memberPublishedServiceResolver);
        ArgumentNullException.ThrowIfNull(staticGAgentStreamInvocationPort);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authorization bearer token is required.");

        var result = await commandFacade.CreateAsync(ToCommandRequest(request), bearerToken, ct);
        if (result.Error is not null)
            return ToErrorResult(result.Error.StatusCode, result.Error.Code, result.Error.Message);

        var logger = loggerFactory.CreateLogger("Aevatar.Mainnet.Host.Api.Responses");
        if (result.Forward?.Action.ForwardToTeam is not null)
        {
            return await HandleForwardToTeamAsync(
                http,
                result.Forward.Normalized,
                result.Forward.CallerScope,
                result.Forward.Action.ForwardToTeam,
                teamEntryMemberResolver,
                staticGAgentStreamInvocationPort,
                logger,
                ct);
        }

        if (result.Forward?.Action.ForwardToGagent is not null)
        {
            return await HandleForwardToGAgentAsync(
                http,
                result.Forward.Normalized,
                result.Forward.CallerScope,
                result.Forward.Action.ForwardToGagent,
                memberPublishedServiceResolver,
                staticGAgentStreamInvocationPort,
                logger,
                ct);
        }

        if (result.StreamPlan is not null)
        {
            await WriteStreamResponseAsync(
                http.Response,
                commandFacade,
                result.StreamPlan,
                ct);
            return Results.Empty;
        }

        if (result.Completed is not null)
        {
            return Results.Json(
                BuildCompletedResponse(
                    result.Completed.Normalized,
                    result.Completed.CreatedAt,
                    result.Completed.CompletedAt,
                    result.Completed.OutputText,
                    result.Completed.ForwardedToolCalls,
                    result.Completed.Usage is null ? null : MapUsage(result.Completed.Usage)),
                statusCode: StatusCodes.Status200OK);
        }

        throw new InvalidOperationException("Responses command facade returned no result.");
    }

    internal static async Task<IResult> HandleCancelResponseAsync(
        HttpContext http,
        [FromRoute] string id,
        [FromServices] IResponsesCommandFacade commandFacade,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(commandFacade);

        var responseId = id?.Trim();
        if (string.IsNullOrWhiteSpace(responseId))
        {
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "response_id_required",
                "response id is required.");
        }

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return ToErrorResult(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authorization bearer token is required.");

        var result = await commandFacade.CancelAsync(responseId, bearerToken, ct);
        if (result.Error is not null)
            return ToErrorResult(result.Error.StatusCode, result.Error.Code, result.Error.Message);

        return Results.Json(new
        {
            id = result.ResponseId,
            @object = "response",
            status = "cancelled",
            cancelled_at = result.CancelledAt,
        }, JsonOptions, statusCode: StatusCodes.Status200OK);
    }

    private static ResponsesCommandRequest ToCommandRequest(ResponsesCreateRequest request) =>
        new(
            request.Model,
            request.Input,
            request.Stream,
            request.PreviousResponseId,
            request.Temperature,
            request.MaxOutputTokens,
            request.Tools);

    private static async Task WriteStreamResponseAsync(
        HttpResponse response,
        IResponsesCommandFacade commandFacade,
        ResponsesCreateCommandPlan plan,
        CancellationToken ct)
    {
        var normalized = plan.Normalized;
        var createdAt = plan.CreatedAt.ToUnixTimeSeconds();
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);

        var sequenceNumber = 0;
        var createdResponse = BuildCreatedResponse(normalized, createdAt);
        await WriteSseFrameAsync(
            response,
            "response.created",
            new
            {
                type = "response.created",
                response = createdResponse,
                sequence_number = ++sequenceNumber,
            },
            ct);

        var outputItem = BuildOutputMessage(normalized.MessageItemId, "in_progress", text: null);
        await WriteSseFrameAsync(
            response,
            "response.output_item.added",
            new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = outputItem,
                sequence_number = ++sequenceNumber,
            },
            ct);

        var completion = await commandFacade.StreamAsync(
            plan,
            async (delta, token) =>
            {
                await WriteSseFrameAsync(
                    response,
                    "response.output_text.delta",
                    new
                    {
                        type = "response.output_text.delta",
                        item_id = normalized.MessageItemId,
                        output_index = 0,
                        content_index = 0,
                        delta,
                        sequence_number = ++sequenceNumber,
                    },
                    token);
            },
            ct);

        if (completion.Error is not null)
        {
            await WriteStreamFailureAsync(
                response,
                normalized,
                createdAt,
                ++sequenceNumber,
                completion.Error.Code,
                completion.Error.Message,
                CancellationToken.None);
            return;
        }

        var completedText = completion.OutputText;
        await WriteSseFrameAsync(
            response,
            "response.output_text.done",
            new
            {
                type = "response.output_text.done",
                item_id = normalized.MessageItemId,
                output_index = 0,
                content_index = 0,
                text = completedText,
                sequence_number = ++sequenceNumber,
            },
            ct);

        var completedOutputItem = BuildOutputMessage(normalized.MessageItemId, "completed", completedText);
        await WriteSseFrameAsync(
            response,
            "response.output_item.done",
            new
            {
                type = "response.output_item.done",
                output_index = 0,
                item = completedOutputItem,
                sequence_number = ++sequenceNumber,
            },
            ct);

        var nextOutputIndex = 1;
        foreach (var toolCall in completion.ForwardedToolCalls)
        {
            var functionCallItem = BuildFunctionCallOutputItem(toolCall);
            await WriteSseFrameAsync(
                response,
                "response.output_item.added",
                new
                {
                    type = "response.output_item.added",
                    output_index = nextOutputIndex,
                    item = functionCallItem,
                    sequence_number = ++sequenceNumber,
                },
                ct);
            await WriteSseFrameAsync(
                response,
                "response.output_item.done",
                new
                {
                    type = "response.output_item.done",
                    output_index = nextOutputIndex,
                    item = functionCallItem,
                    sequence_number = ++sequenceNumber,
                },
                ct);
            nextOutputIndex++;
        }

        var completedResponse = BuildCompletedResponse(
            normalized,
            createdAt,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            completedText,
            completion.ForwardedToolCalls,
            completion.Usage is null ? null : MapUsage(completion.Usage));

        await WriteSseFrameAsync(
            response,
            "response.completed",
            new
            {
                type = "response.completed",
                response = completedResponse,
                sequence_number = ++sequenceNumber,
            },
            ct);
    }

    /// <summary>
    /// Handle a <see cref="ChatRouteAction.ForwardToTeam"/> decision: resolve the
    /// team's entry member to a Studio published service, invoke it as an
    /// ephemeral GAgent run via <see cref="IStaticGAgentStreamInvocationPort{TFrame}"/>,
    /// and map the AGUI event stream back to OpenAI Responses (SSE or JSON).
    ///
    /// Bypasses LLM session/provider/llmRequest entirely — the response id
    /// the caller sees is the normalized response id; per-turn run lifecycle
    /// belongs to the team entry member's actor.
    /// </summary>
    private static async Task<IResult> HandleForwardToTeamAsync(
        HttpContext http,
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        ForwardToTeam forwardToTeam,
        ITeamEntryMemberResolver teamEntryMemberResolver,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        ILogger logger,
        CancellationToken ct)
    {
        var teamId = forwardToTeam.TeamId?.Trim() ?? string.Empty;
        var endpointId = forwardToTeam.EndpointId?.Trim() ?? string.Empty;
        if (teamId.Length == 0)
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "chat_route_invalid",
                "ForwardToTeam decision missing team_id.");
        if (endpointId.Length == 0)
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "chat_route_invalid",
                "ForwardToTeam decision missing endpoint_id.");

        // ForwardToTeam.scope_id is reserved for future cross-scope routing;
        // v1 stamps the caller's ingress scope and ignores conflicting overrides.
        var scopeId = callerScope.ScopeId;

        TeamEntryMemberResolution resolution;
        try
        {
            resolution = await teamEntryMemberResolver.ResolveAsync(scopeId, teamId, ct);
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            return ToErrorResult(
                ResolveTeamEntryHttpStatusCode(ex.Code),
                ex.Code,
                ex.Message);
        }

        var identity = new ServiceIdentity
        {
            TenantId = resolution.ScopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = resolution.PublishedServiceId,
        };
        var input = new StaticGAgentStreamInvocationInput(
            Prompt: normalized.Prompt ?? string.Empty,
            SessionId: normalized.ResponseId,
            Headers: BuildStaticGAgentInvocationHeaders(http, normalized, callerScope));
        var invocationRequest = new StaticGAgentStreamInvocationRequest(identity, endpointId, input);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (normalized.Stream)
        {
            await WriteAGuiBackedResponseStreamAsync(
                http.Response,
                normalized,
                createdAt,
                invocationRequest,
                staticGAgentStreamInvocationPort,
                logger,
                ct);
            return Results.Empty;
        }

        return await CollectAGuiBackedResponseAsync(
            normalized,
            createdAt,
            invocationRequest,
            staticGAgentStreamInvocationPort,
            logger,
            ct);
    }

    /// <summary>
    /// Handle a <see cref="ChatRouteAction.ForwardToGagent"/> decision on the LLM
    /// facade: resolve <see cref="ForwardToGAgent.ActorId"/> as a Studio
    /// <c>memberId</c> via <see cref="IMemberPublishedServiceResolver"/>, then
    /// invoke the resulting published service via
    /// <see cref="IStaticGAgentStreamInvocationPort{TFrame}"/> and map AGUI events
    /// back to OpenAI Responses (SSE or JSON).
    ///
    /// Endpoint selection: ForwardToGAgent has no <c>endpoint_id</c> field, so the
    /// caller is steered toward the default chat endpoint
    /// (<see cref="DefaultGAgentChatEndpointId"/>). This matches the contract a
    /// chat-route policy author can reasonably express through ForwardToGAgent —
    /// a single named GAgent run with no per-rule endpoint customization. Authors
    /// who need an explicit endpoint should switch to ForwardToTeam (which does
    /// carry endpoint_id) or to a direct Studio invoke URL.
    /// </summary>
    private static async Task<IResult> HandleForwardToGAgentAsync(
        HttpContext http,
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope,
        ForwardToGAgent forwardToGAgent,
        IMemberPublishedServiceResolver memberPublishedServiceResolver,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        ILogger logger,
        CancellationToken ct)
    {
        var memberId = forwardToGAgent.ActorId?.Trim() ?? string.Empty;
        if (memberId.Length == 0)
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "chat_route_invalid",
                "ForwardToGAgent decision missing actor_id.");

        MemberPublishedServiceResolution resolution;
        try
        {
            resolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(callerScope.ScopeId, memberId),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            // The resolver's normalization (empty / disallowed separator chars in
            // memberId) raises InvalidOperationException. Surface as a structured
            // 400 so the caller sees a real error code, not the resolver's bare
            // message bubbling up through generic exception handling.
            return ToErrorResult(
                StatusCodes.Status400BadRequest,
                "chat_route_invalid",
                ex.Message);
        }

        var identity = new ServiceIdentity
        {
            TenantId = resolution.ScopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = resolution.PublishedServiceId,
        };
        var input = new StaticGAgentStreamInvocationInput(
            Prompt: normalized.Prompt ?? string.Empty,
            SessionId: normalized.ResponseId,
            Headers: BuildStaticGAgentInvocationHeaders(http, normalized, callerScope));
        var invocationRequest = new StaticGAgentStreamInvocationRequest(identity, DefaultGAgentChatEndpointId, input);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (normalized.Stream)
        {
            await WriteAGuiBackedResponseStreamAsync(
                http.Response,
                normalized,
                createdAt,
                invocationRequest,
                staticGAgentStreamInvocationPort,
                logger,
                ct);
            return Results.Empty;
        }

        return await CollectAGuiBackedResponseAsync(
            normalized,
            createdAt,
            invocationRequest,
            staticGAgentStreamInvocationPort,
            logger,
            ct);
    }

    /// <summary>
    /// Default endpoint id used when ForwardToGAgent forwards to a single Studio
    /// member without naming an explicit endpoint. Members published by Studio's
    /// member-first authoring flow expose this as their canonical chat entry.
    /// </summary>
    internal const string DefaultGAgentChatEndpointId = "chat";

    private static Dictionary<string, string> BuildStaticGAgentInvocationHeaders(
        HttpContext http,
        NormalizedResponsesRequest normalized,
        ResponsesCallerScope callerScope)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = normalized.ResponseId,
            [ChannelMetadataKeys.RegistrationScopeId] = callerScope.ScopeId,
        };

        var bearerToken = ExtractBearerToken(http);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            headers[LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken;
            headers[ConnectorRequest.HttpAuthorizationMetadataKey] = $"Bearer {bearerToken}";
        }

        return headers;
    }

    private static async Task WriteAGuiBackedResponseStreamAsync(
        HttpResponse response,
        NormalizedResponsesRequest normalized,
        long createdAt,
        StaticGAgentStreamInvocationRequest invocationRequest,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        ILogger logger,
        CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);

        var adapter = new AGUIEventToResponsesSseAdapter(
            response,
            normalized.ResponseId,
            normalized.MessageItemId,
            JsonOptions);
        await adapter.WriteCreatedAsync(
            BuildCreatedResponse(normalized, createdAt),
            BuildOutputMessage(normalized.MessageItemId, "in_progress", text: null),
            ct);

        try
        {
            var result = await staticGAgentStreamInvocationPort.InvokeAsync(
                invocationRequest,
                emitAsync: adapter.WriteAsync,
                onAcceptedAsync: null,
                ct);
            if (!result.Succeeded)
            {
                await adapter.WriteFailureAsync(
                    result.StartError.ToString().ToLowerInvariant(),
                    "GAgent invocation could not be started.",
                    ct);
                return;
            }

            if (adapter.HasFailed || result.CompletionStatus == GAgentDraftRunCompletionStatus.Failed)
            {
                if (!adapter.HasFailed)
                {
                    await adapter.WriteFailureAsync(
                        "gagent_invocation_failed",
                        "GAgent invocation failed.",
                        ct);
                }
                return;
            }

            await adapter.WriteCompletedAsync(
                buildCompletedMessageItem: text => BuildOutputMessage(normalized.MessageItemId, "completed", text),
                buildFunctionCallItem: tool => BuildFunctionCallOutputItem(new ToolCall
                {
                    Id = tool.ToolCallId,
                    Name = tool.ToolName,
                    ArgumentsJson = tool.Result ?? "{}",
                }),
                buildCompletedResponse: text => BuildCompletedResponse(
                    normalized,
                    createdAt,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    text,
                    adapter.CompletedToolCalls
                        .Select(tc => new ToolCall
                        {
                            Id = tc.ToolCallId,
                            Name = tc.ToolName,
                            ArgumentsJson = tc.Result ?? "{}",
                        })
                        .ToArray(),
                    usage: null),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client aborted; nothing to forward.
        }
        catch (InvalidOperationException ex) when (IsServiceNotFoundException(ex))
        {
            logger.LogWarning(ex, "AGUI-backed stream invocation resolved to unknown service for response {ResponseId}", normalized.ResponseId);
            await adapter.WriteFailureAsync(
                "gagent_target_not_found",
                ex.Message,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AGUI-backed stream invocation failed for response {ResponseId}", normalized.ResponseId);
            await adapter.WriteFailureAsync(
                "gagent_invocation_failed",
                "GAgent invocation failed mid-stream.",
                ct);
        }
    }

    /// <summary>
    /// Recognizes the <see cref="InvalidOperationException"/> raised by the
    /// service-invocation resolution layer when the resolved
    /// <c>publishedServiceId</c> isn't registered as a Studio service. The
    /// resolver layer doesn't define a typed exception for this case (it's
    /// raised from <c>ServiceInvocationResolutionService.ResolveAsync</c> with
    /// a deterministic message prefix), so we match by message shape. Keeps
    /// chat-route policy authors out of the generic 500 bucket.
    /// </summary>
    private static bool IsServiceNotFoundException(InvalidOperationException ex) =>
        ex.Message.StartsWith("Service '", StringComparison.Ordinal) &&
        ex.Message.Contains("was not found", StringComparison.Ordinal);

    private static async Task<IResult> CollectAGuiBackedResponseAsync(
        NormalizedResponsesRequest normalized,
        long createdAt,
        StaticGAgentStreamInvocationRequest invocationRequest,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        ILogger logger,
        CancellationToken ct)
    {
        var aggregatedText = new StringBuilder();
        var completedToolCalls = new List<ToolCall>();
        var toolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? failureCode = null;
        string? failureMessage = null;

        async ValueTask EmitAsync(AGUIEvent evt, CancellationToken token)
        {
            switch (evt.EventCase)
            {
                case AGUIEvent.EventOneofCase.TextMessageContent:
                    var delta = evt.TextMessageContent?.Delta;
                    if (!string.IsNullOrEmpty(delta))
                        aggregatedText.Append(delta);
                    break;
                case AGUIEvent.EventOneofCase.ToolCallStart:
                    if (!string.IsNullOrWhiteSpace(evt.ToolCallStart?.ToolCallId))
                        toolCallNames[evt.ToolCallStart.ToolCallId] = evt.ToolCallStart.ToolName ?? string.Empty;
                    break;
                case AGUIEvent.EventOneofCase.ToolCallEnd:
                    var endId = evt.ToolCallEnd?.ToolCallId;
                    if (string.IsNullOrWhiteSpace(endId))
                        break;
                    var name = toolCallNames.GetValueOrDefault(endId!, string.Empty);
                    completedToolCalls.Add(new ToolCall
                    {
                        Id = endId!,
                        Name = name,
                        ArgumentsJson = evt.ToolCallEnd?.Result ?? "{}",
                    });
                    break;
                case AGUIEvent.EventOneofCase.RunError:
                    failureCode = string.IsNullOrWhiteSpace(evt.RunError?.Code)
                        ? "gagent_invocation_failed"
                        : evt.RunError!.Code;
                    failureMessage = string.IsNullOrWhiteSpace(evt.RunError?.Message)
                        ? "GAgent invocation failed."
                        : evt.RunError!.Message;
                    break;
            }
            await ValueTask.CompletedTask;
        }

        try
        {
            var result = await staticGAgentStreamInvocationPort.InvokeAsync(
                invocationRequest,
                emitAsync: EmitAsync,
                onAcceptedAsync: null,
                ct);
            if (!result.Succeeded)
            {
                return ToErrorResult(
                    StatusCodes.Status502BadGateway,
                    result.StartError.ToString().ToLowerInvariant(),
                    "GAgent invocation could not be started.");
            }
            if (failureMessage is not null || result.CompletionStatus == GAgentDraftRunCompletionStatus.Failed)
            {
                return ToErrorResult(
                    StatusCodes.Status500InternalServerError,
                    failureCode ?? "gagent_invocation_failed",
                    failureMessage ?? "GAgent invocation failed.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (InvalidOperationException ex) when (IsServiceNotFoundException(ex))
        {
            // The static port's resolution service throws InvalidOperationException
            // with "Service '<...>' was not found." when ForwardToTeam/ForwardToGAgent
            // resolves to a publishedServiceId that isn't actually registered as a
            // Studio service (e.g. chat-route policy points at a member that was
            // never bound). Surface as structured 404 so chat-route authors can
            // distinguish "configured wrong" from "service crashed".
            logger.LogWarning(ex, "AGUI-backed invocation resolved to unknown service for response {ResponseId}", normalized.ResponseId);
            return ToErrorResult(
                StatusCodes.Status404NotFound,
                "gagent_target_not_found",
                ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AGUI-backed invocation failed for response {ResponseId}", normalized.ResponseId);
            return ToErrorResult(
                StatusCodes.Status500InternalServerError,
                "gagent_invocation_failed",
                "GAgent invocation failed.");
        }

        var completed = BuildCompletedResponse(
            normalized,
            createdAt,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            aggregatedText.ToString(),
            completedToolCalls,
            usage: null);
        return Results.Json(completed, statusCode: StatusCodes.Status200OK);
    }

    private static int ResolveTeamEntryHttpStatusCode(string code) =>
        code switch
        {
            TeamEntryMemberErrorCodes.TeamNotFound => StatusCodes.Status404NotFound,
            TeamEntryMemberErrorCodes.EntryMemberNotFound => StatusCodes.Status404NotFound,
            TeamEntryMemberErrorCodes.TeamArchived => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberNotConfigured => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberMismatch => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberNotReady => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };

    private static ResponsesResponseSnapshot BuildCreatedResponse(
        NormalizedResponsesRequest normalized,
        long createdAt)
    {
        return new ResponsesResponseSnapshot
        {
            Id = normalized.ResponseId,
            CreatedAt = createdAt,
            Status = "in_progress",
            Input = [BuildInputMessage(normalized.Prompt)],
            MaxOutputTokens = normalized.MaxOutputTokens,
            Model = normalized.Model,
            Output = [],
            PreviousResponseId = normalized.PreviousResponseId,
            ParallelToolCalls = true,
            Reasoning = new ResponsesReasoningSettings(),
            Store = false,
            Temperature = normalized.Temperature,
            ToolChoice = "auto",
            Tools = [],
            Truncation = "disabled",
            Usage = null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static ResponsesResponseSnapshot BuildCompletedResponse(
        NormalizedResponsesRequest normalized,
        long createdAt,
        long completedAt,
        string outputText,
        IReadOnlyList<ToolCall> toolCalls,
        ResponsesUsage? usage)
    {
        var output = new List<object>
        {
            BuildOutputMessage(normalized.MessageItemId, "completed", outputText),
        };
        output.AddRange(toolCalls.Select(BuildFunctionCallOutputItem));

        return new ResponsesResponseSnapshot
        {
            Id = normalized.ResponseId,
            CreatedAt = createdAt,
            Status = "completed",
            CompletedAt = completedAt,
            Input = [BuildInputMessage(normalized.Prompt)],
            MaxOutputTokens = normalized.MaxOutputTokens,
            Model = normalized.Model,
            Output = output,
            PreviousResponseId = normalized.PreviousResponseId,
            ParallelToolCalls = true,
            Reasoning = new ResponsesReasoningSettings(),
            Store = false,
            Temperature = normalized.Temperature,
            ToolChoice = "auto",
            Tools = [],
            Truncation = "disabled",
            Usage = usage,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static ResponsesInputMessage BuildInputMessage(string prompt)
    {
        return new ResponsesInputMessage
        {
            Content =
            [
                new ResponsesInputTextContent
                {
                    Text = prompt,
                },
            ],
        };
    }

    private static ResponsesUsage MapUsage(TokenUsage usage) =>
        new()
        {
            InputTokens = usage.PromptTokens,
            InputTokensDetails = new ResponsesInputTokensDetails(),
            OutputTokens = usage.CompletionTokens,
            TotalTokens = usage.TotalTokens,
            OutputTokensDetails = new ResponsesOutputTokensDetails(),
        };

    private static ResponsesOutputMessage BuildOutputMessage(string id, string status, string? text)
    {
        IReadOnlyList<ResponsesOutputTextContent> content = text is null
            ? []
            :
            [
                new ResponsesOutputTextContent
                {
                    Text = text,
                },
            ];

        return new ResponsesOutputMessage
        {
            Id = id,
            Status = status,
            Content = string.IsNullOrWhiteSpace(text)
                ? []
                : content,
        };
    }

    private static ResponsesFunctionCallOutputItem BuildFunctionCallOutputItem(ToolCall toolCall) =>
        new()
        {
            Id = "fc_" + SanitizeOutputId(toolCall.Id),
            Status = "completed",
            CallId = toolCall.Id,
            Name = toolCall.Name,
            Arguments = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson,
        };

    private static string SanitizeOutputId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ResponsesIds.NewOpaqueId();

        var builder = new StringBuilder(id.Length);
        foreach (var ch in id)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return builder.ToString();
    }

    private static async Task WriteSseFrameAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\n");
        await response.Body.WriteAsync(bytes, ct);
        bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteStreamFailureAsync(
        HttpResponse response,
        NormalizedResponsesRequest normalized,
        long createdAt,
        int sequenceNumber,
        string code,
        string message,
        CancellationToken ct)
    {
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var failedResponse = BuildFailedResponse(normalized, createdAt, completedAt, code, message);
        await WriteSseFrameAsync(
            response,
            "response.failed",
            new
            {
                type = "response.failed",
                response = failedResponse,
                sequence_number = sequenceNumber,
            },
            ct);

        await WriteSseFrameAsync(
            response,
            "error",
            new
            {
                type = "error",
                code,
                message,
                param = (string?)null,
                sequence_number = sequenceNumber + 1,
            },
            ct);
    }

    private static ResponsesResponseSnapshot BuildFailedResponse(
        NormalizedResponsesRequest normalized,
        long createdAt,
        long completedAt,
        string code,
        string message)
    {
        return new ResponsesResponseSnapshot
        {
            Id = normalized.ResponseId,
            CreatedAt = createdAt,
            Status = "failed",
            CompletedAt = completedAt,
            Error = new ResponsesResponseError
            {
                Code = code,
                Message = message,
            },
            Input = [BuildInputMessage(normalized.Prompt)],
            MaxOutputTokens = normalized.MaxOutputTokens,
            Model = normalized.Model,
            Output = [],
            PreviousResponseId = normalized.PreviousResponseId,
            ParallelToolCalls = true,
            Reasoning = new ResponsesReasoningSettings(),
            Store = false,
            Temperature = normalized.Temperature,
            ToolChoice = "auto",
            Tools = [],
            Truncation = "disabled",
            Usage = null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static IResult ToErrorResult(int statusCode, string code, string message) =>
        Results.Json(
            new ResponsesApiErrorResponse
            {
                Error = new ResponsesApiError
                {
                    Code = code,
                    Message = message,
                    Type = GetErrorType(statusCode),
                    Param = null,
                },
            },
            statusCode: statusCode);

    private static string GetErrorType(int statusCode) =>
        statusCode switch
        {
            401 => "authentication_error",
            403 => "permission_error",
            429 => "rate_limit_error",
            >= 500 => "server_error",
            _ => "invalid_request_error",
        };

    private static string? ExtractBearerToken(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>OpenAI-spec `GET /v1/models`. Fans out across every NyxID-routed service the caller
    /// can reach (gateway providers + proxy-plane LLM services) and returns the union, with
    /// `vendor/model`-prefixed ids for non-gateway routes so the create handler can recover the
    /// route via <see cref="ResponsesModelRouteParser"/>. Gateway models stay bare for back-compat
    /// with existing callers that send plain `gpt-5.4` / `claude-3-5-sonnet-...`.</summary>
    internal static async Task<IResult> HandleListModelsAsync(
        HttpContext http,
        [FromServices] IResponsesModelsAggregator aggregator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(aggregator);

        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return ToErrorResult(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authorization bearer token is required.");
        }

        var entries = await aggregator.AggregateAsync(bearerToken, ct).ConfigureAwait(false);
        return Results.Json(new ResponsesModelsListResponse { Data = entries });
    }
}
