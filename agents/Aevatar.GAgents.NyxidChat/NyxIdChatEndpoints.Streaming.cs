using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Hosting;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private static async Task HandleStreamMessageAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdChatStreamRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort actorDispatchPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] INyxIdChatSessionProjectionPort sessionProjectionPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        IActor? actor = null;
        var accessToken = string.Empty;
        var prompt = string.Empty;

        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            accessToken = ExtractBearerToken(http);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            prompt = request.Prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt) && request.InputParts is not { Count: > 0 })
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!await TryAuthorizeConversationAsync(
                    http,
                    admissionPort,
                    scopeId,
                    actorId,
                    ScopeResourceOperation.Stream,
                    ct))
                return;

            actor = await actorRuntime.GetAsync(actorId);
            if (actor == null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID chat request setup failed for actor {ActorId}", actorId);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        await NyxIdChatStreamingRunner.RunAsync(
            http,
            actorId,
            sessionProjectionPort,
            logger,
            dispatchAsync: async (messageId, runCt) =>
            {
                // Refactor (iter1/cluster-004):
                //   Old pattern: endpoint coupled command dispatch with raw EventEnvelope stream observation.
                //   New principle: endpoint dispatches through IActorDispatchPort and observes typed projection events.
                var chatRequest = new ChatRequestEvent
                {
                    Prompt = prompt,
                    SessionId = request.SessionId ?? messageId,
                    ScopeId = scopeId,
                };
                if (request.InputParts is { Count: > 0 })
                {
                    foreach (var part in request.InputParts)
                        chatRequest.InputParts.Add(part.ToProto());
                }

                chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken;
                chatRequest.Metadata["scope_id"] = scopeId;
                await InjectUserConfigMetadataAsync(http, chatRequest.Metadata, runCt);
                await InjectUserMemoryAsync(http, chatRequest.Metadata, runCt);
                await InjectConnectedServicesAsync(http, accessToken, chatRequest.Metadata, runCt);

                var envelope = new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Payload = Any.Pack(chatRequest),
                    Route = new EnvelopeRoute
                    {
                        Direct = new DirectRoute { TargetActorId = actor.Id },
                    },
                };

                await actorDispatchPort.DispatchAsync(actor.Id, envelope, runCt);
            },
            errorMessages: new NyxIdChatStreamingRunner.ErrorMessages(
                "The chat request failed before completion. Please try again.",
                "Request timed out.",
                "The chat request failed. Please try again."),
            ct);
    }

    /// <summary>
    /// Handles tool approval decisions from the frontend.
    /// Opens an SSE connection to stream the continuation chat response.
    /// </summary>
    private static async Task HandleApproveAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdApprovalRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort actorDispatchPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] INyxIdChatSessionProjectionPort sessionProjectionPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        IActor? actor = null;

        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var accessToken = ExtractBearerToken(http);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!await TryAuthorizeConversationAsync(
                    http,
                    admissionPort,
                    scopeId,
                    actorId,
                    ScopeResourceOperation.Approve,
                    ct))
                return;

            actor = await actorRuntime.GetAsync(actorId);
            if (actor == null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID approval request setup failed for actor {ActorId}", actorId);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        await NyxIdChatStreamingRunner.RunAsync(
            http,
            actorId,
            sessionProjectionPort,
            logger,
            dispatchAsync: async (_, runCt) =>
            {
                // Refactor (iter1/cluster-004):
                //   Old pattern: approval continuation reused raw EventEnvelope stream completion detection.
                //   New principle: approval dispatch is a command; completion arrives via typed projection session events.
                var decisionEvent = new ToolApprovalDecisionEvent
                {
                    RequestId = request.RequestId,
                    SessionId = request.SessionId ?? scopeId,
                    Approved = request.Approved,
                    Reason = request.Reason ?? string.Empty,
                };

                var envelope = new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Payload = Any.Pack(decisionEvent),
                    Route = new EnvelopeRoute
                    {
                        Direct = new DirectRoute { TargetActorId = actor.Id },
                    },
                };

                await actorDispatchPort.DispatchAsync(actor.Id, envelope, runCt);
            },
            errorMessages: new NyxIdChatStreamingRunner.ErrorMessages(
                "The approval continuation failed before completion. Please try again.",
                "Approval continuation timed out.",
                "The approval continuation failed. Please try again."),
            ct);
    }

    public sealed record NyxIdApprovalRequest(
        string? RequestId,
        bool Approved = true,
        string? Reason = null,
        string? SessionId = null);

    public sealed record NyxIdChatStreamRequest(
        string? Prompt,
        string? SessionId = null,
        IReadOnlyList<ContentPartDto>? InputParts = null);

    public sealed record ContentPartDto(
        string Type,
        string? Text = null,
        string? DataBase64 = null,
        string? MediaType = null,
        string? Uri = null,
        string? Name = null)
    {
        public ChatContentPart ToProto() => new()
        {
            Kind = Type?.ToLowerInvariant() switch
            {
                "image" => ChatContentPartKind.Image,
                "audio" => ChatContentPartKind.Audio,
                "video" => ChatContentPartKind.Video,
                "text" => ChatContentPartKind.Text,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = Text ?? string.Empty,
            DataBase64 = DataBase64 ?? string.Empty,
            MediaType = MediaType ?? string.Empty,
            Uri = Uri ?? string.Empty,
            Name = Name ?? string.Empty,
        };
    }
}
