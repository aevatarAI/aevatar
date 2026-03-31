using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Type = System.Type;
// AI Abstractions types (published by RoleGAgent) — aliased to avoid conflict with AGUI types
using AiTextStart = Aevatar.AI.Abstractions.TextMessageStartEvent;
using AiTextContent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextReasoning = Aevatar.AI.Abstractions.TextMessageReasoningEvent;
using AiTextEnd = Aevatar.AI.Abstractions.TextMessageEndEvent;
using AiToolCall = Aevatar.AI.Abstractions.ToolCallEvent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeGAgentEndpoints
{
    public static IEndpointRouteBuilder MapScopeGAgentCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("ScopeGAgent");
        group.MapGet("/gagent-types", HandleListGAgentTypesAsync);
        group.MapPost("/{scopeId}/gagent/draft-run", HandleDraftRunAsync);
        group.MapGet("/{scopeId}/gagent-actors", HandleListActorsAsync);
        group.MapPost("/{scopeId}/gagent-actors", HandleAddActorAsync);
        group.MapDelete("/{scopeId}/gagent-actors/{actorId}", HandleRemoveActorAsync);
        return app;
    }

    // ─── List GAgent Types (reflection) ───

    private static IResult HandleListGAgentTypesAsync()
    {
        var aiGAgentBaseType = FindOpenGenericBaseType("Aevatar.AI.Core.AIGAgentBase`1");
        if (aiGAgentBaseType is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var types = new List<object>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            Type[] exportedTypes;
            try
            {
                exportedTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                exportedTypes = ex.Types.Where(t => t is not null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in exportedTypes)
            {
                if (!type.IsClass || type.IsAbstract)
                    continue;

                if (!DerivesFromOpenGeneric(type, aiGAgentBaseType))
                    continue;

                types.Add(new
                {
                    typeName = type.Name,
                    fullName = type.FullName ?? type.Name,
                    assemblyName = assembly.GetName().Name ?? assembly.FullName ?? string.Empty,
                });
            }
        }

        return Results.Ok(types);
    }

    private static Type? FindOpenGenericBaseType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            try
            {
                var type = assembly.GetType(fullName);
                if (type is not null)
                    return type;
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static bool DerivesFromOpenGeneric(Type type, Type openGenericBase)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openGenericBase)
                return true;
            current = current.BaseType;
        }

        return false;
    }

    // ─── Draft Run ───

    private static async Task HandleDraftRunAsync(
        HttpContext http,
        string scopeId,
        GAgentDraftRunHttpRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgentService.Hosting.ScopeGAgentEndpoints");
        var writer = new AGUISseWriter(http.Response);

        try
        {
            if (string.IsNullOrWhiteSpace(request.ActorTypeName))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Resolve agent type
            var agentType = ResolveAgentType(request.ActorTypeName);
            if (agentType is null)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonErrorAsync(http.Response, "UNKNOWN_GAGENT_TYPE",
                    $"GAgent type '{request.ActorTypeName}' could not be resolved.", ct);
                return;
            }

            // Create or reuse actor
            var preferredId = string.IsNullOrWhiteSpace(request.PreferredActorId)
                ? null
                : request.PreferredActorId.Trim();

            IActor actor;
            if (preferredId is not null)
            {
                var existing = await actorRuntime.GetAsync(preferredId);
                actor = existing ?? await actorRuntime.CreateAsync(agentType, preferredId, ct);
            }
            else
            {
                actor = await actorRuntime.CreateAsync(agentType, null, ct);
            }

            // Set up SSE response
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.StartAsync(ct);

            // Write run started
            var runId = Guid.NewGuid().ToString("N");
            await writer.WriteAsync(new AGUIEvent
            {
                RunStarted = new RunStartedEvent
                {
                    ThreadId = actor.Id,
                    RunId = runId,
                },
            }, ct);

            // Subscribe to raw EventEnvelope on the actor's stream.
            // RoleGAgent publishes individual event types (TextMessageStartEvent, etc.)
            // with TopologyAudience.Parent. When no parent exists, events fall back to self stream.
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = ct.Register(() => tcs.TrySetCanceled());

            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                async envelope =>
                {
                    try
                    {
                        var aguiEvent = TryMapEnvelopeToAguiEvent(envelope);
                        if (aguiEvent is null)
                            return;

                        await writer.WriteAsync(aguiEvent, CancellationToken.None);

                        // Detect completion: RoleGAgent ends with TextMessageEnd;
                        // other agents may use RunFinished/RunError directly.
                        if (aguiEvent.EventCase is AGUIEvent.EventOneofCase.RunFinished
                            or AGUIEvent.EventOneofCase.RunError
                            or AGUIEvent.EventOneofCase.TextMessageEnd)
                        {
                            tcs.TrySetResult();
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                },
                ct);

            // Dispatch ChatRequestEvent to the actor
            var chatRequest = new ChatRequestEvent
            {
                Prompt = request.Prompt.Trim(),
                SessionId = request.SessionId ?? string.Empty,
                ScopeId = scopeId,
            };

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

            await actor.HandleEventAsync(envelope, ct);

            // Wait for completion or timeout
            var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : 120_000;
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));

            if (completedTask == tcs.Task)
            {
                // Completed — write RunFinished to close the SSE stream
                await writer.WriteAsync(new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        ThreadId = actor.Id,
                        RunId = runId,
                    },
                }, CancellationToken.None);
            }
            else
            {
                // Timeout — write error and finish
                await writer.WriteAsync(new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = "GAgent draft-run timed out.",
                    },
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GAgent draft-run failed for type {TypeName}", request.ActorTypeName);
            try
            {
                await writer.WriteAsync(new AGUIEvent
                {
                    RunError = new RunErrorEvent { Message = ex.Message },
                }, CancellationToken.None);
            }
            catch
            {
                // Best-effort
            }
        }
    }

    /// <summary>
    /// Maps an EventEnvelope payload to an AGUIEvent wrapper.
    /// RoleGAgent publishes AI Abstractions event types (aevatar.ai.*);
    /// this maps them to the AGUI presentation types for SSE streaming.
    /// </summary>
    internal static AGUIEvent? TryMapEnvelopeToAguiEvent(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        if (payload is null)
            return null;

        // Match AI Abstractions types (published by RoleGAgent) → AGUI presentation types
        if (payload.Is(AiTextStart.Descriptor))
        {
            var ai = payload.Unpack<AiTextStart>();
            return new AGUIEvent
            {
                TextMessageStart = new Presentation.AGUI.TextMessageStartEvent
                {
                    MessageId = ai.SessionId,
                    Role = "assistant",
                },
            };
        }

        if (payload.Is(AiTextContent.Descriptor))
        {
            var ai = payload.Unpack<AiTextContent>();
            return new AGUIEvent
            {
                TextMessageContent = new Presentation.AGUI.TextMessageContentEvent
                {
                    MessageId = ai.SessionId,
                    Delta = ai.Delta,
                },
            };
        }

        if (payload.Is(AiTextReasoning.Descriptor))
        {
            // Map reasoning to a custom event (AGUI has no reasoning oneof field)
            var ai = payload.Unpack<AiTextReasoning>();
            return new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "TEXT_MESSAGE_REASONING",
                    Payload = Any.Pack(new Presentation.AGUI.TextMessageContentEvent
                    {
                        MessageId = ai.SessionId,
                        Delta = ai.Delta,
                    }),
                },
            };
        }

        if (payload.Is(AiTextEnd.Descriptor))
        {
            var ai = payload.Unpack<AiTextEnd>();
            return new AGUIEvent
            {
                TextMessageEnd = new Presentation.AGUI.TextMessageEndEvent
                {
                    MessageId = ai.SessionId,
                },
            };
        }

        if (payload.Is(AiToolCall.Descriptor))
        {
            var ai = payload.Unpack<AiToolCall>();
            return new AGUIEvent
            {
                ToolCallStart = new ToolCallStartEvent
                {
                    ToolCallId = ai.CallId,
                    ToolName = ai.ToolName,
                },
            };
        }

        // Also accept pre-wrapped AGUIEvent
        if (payload.Is(AGUIEvent.Descriptor))
            return payload.Unpack<AGUIEvent>();

        return null;
    }

    internal static Type? ResolveAgentType(string typeName)
    {
        // Try exact match first
        var type = Type.GetType(typeName, throwOnError: false);
        if (type is not null)
            return type;

        // Search loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            try
            {
                type = assembly.GetType(typeName);
                if (type is not null)
                    return type;
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    // ─── Actor CRUD (chrono-storage) ───

    private static async Task<IResult> HandleListActorsAsync(
        string scopeId,
        [FromServices] IGAgentActorStore actorStore,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            var groups = await actorStore.GetAsync(ct);
            return Results.Ok(groups);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { code = "GAGENT_ACTOR_STORE_ERROR", message = ex.Message });
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Aevatar.GAgentService.Hosting.ScopeGAgentEndpoints")
                .LogWarning(ex, "Failed to list GAgent actors from storage");
            return Results.Ok(Array.Empty<GAgentActorGroup>());
        }
    }

    private static async Task<IResult> HandleAddActorAsync(
        string scopeId,
        AddGAgentActorHttpRequest request,
        [FromServices] IGAgentActorStore actorStore,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.GAgentType) || string.IsNullOrWhiteSpace(request.ActorId))
                return Results.BadRequest(new { code = "INVALID_REQUEST", message = "gagentType and actorId are required." });

            await actorStore.AddActorAsync(request.GAgentType.Trim(), request.ActorId.Trim(), ct);
            return Results.Ok();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { code = "GAGENT_ACTOR_STORE_ERROR", message = ex.Message });
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Aevatar.GAgentService.Hosting.ScopeGAgentEndpoints")
                .LogWarning(ex, "Failed to persist GAgent actor to storage");
            return Results.Ok();
        }
    }

    private static async Task<IResult> HandleRemoveActorAsync(
        string scopeId,
        string actorId,
        [FromQuery] string? gagentType,
        [FromServices] IGAgentActorStore actorStore,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gagentType))
                return Results.BadRequest(new { code = "INVALID_REQUEST", message = "gagentType query parameter is required." });

            await actorStore.RemoveActorAsync(gagentType.Trim(), actorId.Trim(), ct);
            return Results.Ok();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { code = "GAGENT_ACTOR_STORE_ERROR", message = ex.Message });
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Aevatar.GAgentService.Hosting.ScopeGAgentEndpoints")
                .LogWarning(ex, "Failed to remove GAgent actor from storage");
            return Results.Ok();
        }
    }

    private static async Task WriteJsonErrorAsync(HttpResponse response, string code, string message, CancellationToken ct)
    {
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new { code, message });
        await response.WriteAsync(json, ct);
    }

    // ─── Request models ───

    public sealed record GAgentDraftRunHttpRequest(
        string ActorTypeName,
        string Prompt,
        string? PreferredActorId = null,
        string? SessionId = null,
        int TimeoutMs = 0);

    public sealed record AddGAgentActorHttpRequest(
        string GAgentType,
        string ActorId);
}
