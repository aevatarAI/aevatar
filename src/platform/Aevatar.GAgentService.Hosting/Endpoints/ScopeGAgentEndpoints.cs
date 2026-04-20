using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Type = System.Type;
// AI Abstractions types (published by RoleGAgent) — aliased to avoid conflict with AGUI types
using AiTextStart = Aevatar.AI.Abstractions.TextMessageStartEvent;
using AiTextContent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextReasoning = Aevatar.AI.Abstractions.TextMessageReasoningEvent;
using AiTextEnd = Aevatar.AI.Abstractions.TextMessageEndEvent;
using AiToolCall = Aevatar.AI.Abstractions.ToolCallEvent;
using AiToolResult = Aevatar.AI.Abstractions.ToolResultEvent;
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
                try
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
                        endpoints = DiscoverEndpoints(type),
                    });
                }
                catch
                {
                    // Skip individual types that fail to inspect — don't let one broken
                    // GAgent type prevent the rest from being listed.
                }
            }
        }

        return Results.Ok(types);
    }

    /// <summary>
    /// Discovers available endpoints from a GAgent type by reflecting over [EventHandler] methods.
    /// Any AIGAgentBase subclass always has a "chat" endpoint (ChatRequestEvent).
    /// Additional endpoints are discovered from [EventHandler] methods whose parameter type
    /// is NOT a base framework event (TextMessageStart/End/Content, ToolCall, etc.).
    /// </summary>
    private static object[] DiscoverEndpoints(Type gAgentType)
    {
        // Well-known base event types that are internal framework plumbing,
        // not user-facing endpoints.
        var frameworkEventTypes = new HashSet<Type>
        {
            typeof(ChatRequestEvent),
            typeof(ChatResponseEvent),
            typeof(AiTextStart),
            typeof(AiTextContent),
            typeof(AiTextReasoning),
            typeof(AiTextEnd),
            typeof(AiToolCall),
            typeof(ToolResultEvent),
            typeof(InitializeRoleAgentEvent),
            typeof(RoleChatSessionStartedEvent),
            typeof(RoleChatSessionCompletedEvent),
        };

        var endpoints = new List<object>();

        // Chat endpoint is always present for AIGAgentBase subclasses.
        endpoints.Add(new
        {
            endpointId = "chat",
            displayName = "chat",
            kind = "chat",
            requestTypeUrl = GetProtoTypeUrl(ChatRequestEvent.Descriptor),
            description = "Default chat endpoint.",
            auto = true,
        });

        // Walk the type hierarchy and discover [EventHandler] methods.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = gAgentType; current != null && current != typeof(object); current = current.BaseType)
        {
            MethodInfo[] methods;
            try
            {
                methods = current.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                // Type hierarchy reflection failed — skip this level.
                continue;
            }

            foreach (var method in methods)
            {
                try
                {
                    var ehAttr = method.GetCustomAttribute<EventHandlerAttribute>();
                    if (ehAttr is null)
                        continue;

                    var parameters = method.GetParameters();
                    if (parameters.Length != 1)
                        continue;

                    var paramType = parameters[0].ParameterType;
                    if (!typeof(IMessage).IsAssignableFrom(paramType) || paramType.IsAbstract)
                        continue;

                    // Skip framework/internal event types — they're not user-facing endpoints.
                    if (frameworkEventTypes.Contains(paramType))
                        continue;

                    var typeUrl = TryGetProtoTypeUrl(paramType);
                    var customName = ehAttr.EndpointName;
                    var endpointId = !string.IsNullOrWhiteSpace(customName)
                        ? customName
                        : ToCamelCase(StripEventSuffix(paramType.Name));

                    if (!seen.Add(endpointId))
                        continue;

                    endpoints.Add(new
                    {
                        endpointId,
                        displayName = endpointId,
                        kind = "command",
                        requestTypeUrl = typeUrl ?? paramType.FullName ?? paramType.Name,
                        description = $"Handles {paramType.Name}",
                        auto = true,
                    });
                }
                catch
                {
                    // Skip individual methods that fail — don't let one broken
                    // handler prevent other endpoints from being discovered.
                }
            }
        }

        return endpoints.ToArray();
    }

    private static string GetProtoTypeUrl(Google.Protobuf.Reflection.MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";

    private static string? TryGetProtoTypeUrl(Type messageType)
    {
        // Try to get the Protobuf Descriptor property to build the proper TypeUrl.
        var descriptorProp = messageType.GetProperty(
            "Descriptor",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (descriptorProp?.GetValue(null) is Google.Protobuf.Reflection.MessageDescriptor desc)
            return $"type.googleapis.com/{desc.FullName}";
        return null;
    }

    private static string StripEventSuffix(string name) =>
        name.EndsWith("Event", StringComparison.Ordinal) ? name[..^5] : name;

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

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
        [FromServices] IGAgentActorStore actorStore,
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
            bool isNewActor;
            if (preferredId is not null)
            {
                var existing = await actorRuntime.GetAsync(preferredId);
                actor = existing ?? await actorRuntime.CreateAsync(agentType, preferredId, ct);
                isNewActor = existing is null;
            }
            else
            {
                actor = await actorRuntime.CreateAsync(agentType, null, ct);
                isNewActor = true;
            }

            // Persist newly created actor to the actor-backed GAgent actor store so it is
            // visible to scope-level listings.
            if (isNewActor)
            {
                try
                {
                    await actorStore.AddActorAsync(scopeId, request.ActorTypeName.Trim(), actor.Id, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to persist actor {ActorId} to actor store", actor.Id);
                }
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

            // Forward caller's Bearer token so NyxID-backed GAgents can pass it
            // to the NyxID LLM gateway. Other LLM providers ignore this metadata key.
            var bearerToken = ExtractBearerToken(http);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken;

            // Forward the user's preferred model from their config.
            var userConfigStore = http.RequestServices.GetService<IUserConfigQueryPort>();
            if (userConfigStore != null)
            {
                try
                {
                    var userConfig = await userConfigStore.GetAsync(ct);
                    if (!string.IsNullOrWhiteSpace(userConfig.DefaultModel))
                        chatRequest.Metadata[LLMRequestMetadataKeys.ModelOverride] = userConfig.DefaultModel.Trim();
                    if (!string.IsNullOrWhiteSpace(userConfig.PreferredLlmRoute))
                        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = userConfig.PreferredLlmRoute.Trim();
                }
                catch
                {
                    // Best-effort
                }
            }

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
                if (tcs.Task.IsFaulted)
                {
                    var faultEx = tcs.Task.Exception?.InnerException ?? tcs.Task.Exception;
                    var isAuthRequired = IsNyxIdAuthenticationRequired(faultEx!);
                    await writer.WriteAsync(new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = isAuthRequired
                                ? "NyxID authentication required. Please sign in."
                                : (faultEx?.Message ?? "An error occurred."),
                            Code = isAuthRequired ? "authentication_required" : null,
                        },
                    }, CancellationToken.None);
                }
                else
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
                var isAuthRequired = IsNyxIdAuthenticationRequired(ex);
                await writer.WriteAsync(new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = isAuthRequired
                            ? "NyxID authentication required. Please sign in."
                            : ex.Message,
                        Code = isAuthRequired ? "authentication_required" : null,
                    },
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
            // RoleGAgent embeds LLM errors in TextMessageEnd.Content with known prefixes.
            // Normal completion content also arrives here (duplicate of already-streamed deltas) — ignore it.
            if (!string.IsNullOrEmpty(ai.Content))
            {
                const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
                const string llmFailedPrefix = "LLM request failed:";
                if (ai.Content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
                {
                    return new AGUIEvent
                    {
                        RunError = new RunErrorEvent { Message = ai.Content[llmErrorPrefix.Length..].Trim() },
                    };
                }

                if (ai.Content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
                {
                    return new AGUIEvent
                    {
                        RunError = new RunErrorEvent { Message = ai.Content.Trim() },
                    };
                }
            }

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

        if (payload.Is(AiToolResult.Descriptor))
        {
            var ai = payload.Unpack<AiToolResult>();
            return new AGUIEvent
            {
                ToolCallEnd = new ToolCallEndEvent
                {
                    ToolCallId = ai.CallId,
                    Result = ai.ResultJson,
                },
            };
        }

        // ToolApprovalRequestEvent → AGUI CustomEvent.
        // Use TypeUrl string match to avoid TypeRegistry issues (AI.Abstractions
        // proto types are not registered in the AGUI SSE writer's TypeRegistry).
        // Serialize fields into a Struct so the JSON formatter can handle it.
        if (payload.TypeUrl.EndsWith("ToolApprovalRequestEvent", StringComparison.Ordinal))
        {
            var approvalPayload = BuildToolApprovalStruct(payload);
            return new AGUIEvent
            {
                Custom = new CustomEvent
                {
                    Name = "TOOL_APPROVAL_REQUEST",
                    Payload = Any.Pack(approvalPayload),
                },
            };
        }

        // Also accept pre-wrapped AGUIEvent
        if (payload.Is(AGUIEvent.Descriptor))
            return payload.Unpack<AGUIEvent>();

        return null;
    }

    /// <summary>
    /// Decode ToolApprovalRequestEvent from raw Any bytes into a google.protobuf.Struct
    /// so the AGUI SSE JsonFormatter can serialize it without needing the AI.Abstractions
    /// type registered in its TypeRegistry.
    /// </summary>
    private static Google.Protobuf.WellKnownTypes.Struct BuildToolApprovalStruct(Any payload)
    {
        // Decode the raw bytes using the well-known field numbers from ai_messages.proto:
        //   string request_id = 1; string session_id = 2; string tool_name = 3;
        //   string tool_call_id = 4; string arguments_json = 5; string approval_mode = 6;
        //   bool is_destructive = 7; int32 timeout_seconds = 8;
        var s = new Google.Protobuf.WellKnownTypes.Struct();
        try
        {
            var input = new CodedInputStream(payload.Value.ToByteArray());
            string requestId = "", toolName = "", toolCallId = "", argumentsJson = "";
            bool isDestructive = false;
            int timeoutSeconds = 15;

            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: requestId = input.ReadString(); break;
                    case 3: toolName = input.ReadString(); break;
                    case 4: toolCallId = input.ReadString(); break;
                    case 5: argumentsJson = input.ReadString(); break;
                    case 7: isDestructive = input.ReadBool(); break;
                    case 8: timeoutSeconds = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }

            s.Fields["requestId"] = Value.ForString(requestId);
            s.Fields["toolName"] = Value.ForString(toolName);
            s.Fields["toolCallId"] = Value.ForString(toolCallId);
            s.Fields["argumentsJson"] = Value.ForString(argumentsJson);
            s.Fields["isDestructive"] = Value.ForBool(isDestructive);
            s.Fields["timeoutSeconds"] = Value.ForNumber(timeoutSeconds);
        }
        catch
        {
            // Fallback: empty struct — frontend will show approval without details
            s.Fields["requestId"] = Value.ForString("");
            s.Fields["error"] = Value.ForString("Failed to decode approval request");
        }

        return s;
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
            var groups = await actorStore.GetAsync(scopeId, ct);
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

            await actorStore.AddActorAsync(scopeId, request.GAgentType.Trim(), request.ActorId.Trim(), ct);
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

            await actorStore.RemoveActorAsync(scopeId, gagentType.Trim(), actorId.Trim(), ct);
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

    private static string? ExtractBearerToken(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;
        return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;
    }

    private static async Task WriteJsonErrorAsync(HttpResponse response, string code, string message, CancellationToken ct)
    {
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new { code, message });
        await response.WriteAsync(json, ct);
    }

    private static bool IsNyxIdAuthenticationRequired(Exception ex) =>
        ex is NyxIdAuthenticationRequiredException
        || ex.InnerException is NyxIdAuthenticationRequiredException
        || (ex is AggregateException agg && agg.InnerExceptions.Any(e => e is NyxIdAuthenticationRequiredException));

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
