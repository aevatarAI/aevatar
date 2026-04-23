using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
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
        [FromServices] ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> interactionService,
        [FromServices] IGAgentDraftRunActorPreparationPort actorPreparationPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgentService.Hosting.ScopeGAgentEndpoints");
        var writer = new AGUISseWriter(http.Response);
        var responseStarted = false;
        GAgentDraftRunPreparedActor? preparedActor = null;

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

            var preparation = await actorPreparationPort.PrepareAsync(
                new GAgentDraftRunPreparationRequest(
                    scopeId,
                    request.ActorTypeName,
                    request.PreferredActorId),
                ct);
            if (!preparation.Succeeded)
            {
                if (preparation.Error == GAgentDraftRunStartError.UnknownActorType)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteJsonErrorAsync(
                        http.Response,
                        "UNKNOWN_GAGENT_TYPE",
                        $"GAgent type '{request.ActorTypeName}' could not be resolved.",
                        ct);
                }

                return;
            }

            preparedActor = preparation.PreparedActor;

            async Task EnsureSseStartedAsync(CancellationToken token)
            {
                if (responseStarted)
                    return;

                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["X-Accel-Buffering"] = "no";
                await http.Response.StartAsync(token);
                responseStarted = true;
            }

            async ValueTask EmitAsync(AGUIEvent aguiEvent, CancellationToken token)
            {
                await EnsureSseStartedAsync(token);
                await writer.WriteAsync(aguiEvent, token);
            }

            async ValueTask OnAcceptedAsync(GAgentDraftRunAcceptedReceipt receipt, CancellationToken token)
            {
                http.Response.Headers["X-Correlation-Id"] = receipt.CorrelationId;
                await EnsureSseStartedAsync(token);
                await writer.WriteAsync(
                    new AGUIEvent
                    {
                        RunStarted = new RunStartedEvent
                        {
                            ThreadId = receipt.ActorId,
                            RunId = receipt.CommandId,
                        },
                    },
                    token);
            }

            var bearerToken = ExtractBearerToken(http);
            string? defaultModel = null;
            string? preferredRoute = null;
            var userConfigStore = http.RequestServices.GetService<IUserConfigQueryPort>();
            if (userConfigStore != null)
            {
                try
                {
                    var userConfig = await userConfigStore.GetAsync(ct);
                    defaultModel = string.IsNullOrWhiteSpace(userConfig.DefaultModel)
                        ? null
                        : userConfig.DefaultModel.Trim();
                    preferredRoute = string.IsNullOrWhiteSpace(userConfig.PreferredLlmRoute)
                        ? null
                        : userConfig.PreferredLlmRoute.Trim();
                }
                catch
                {
                    // Best-effort.
                }
            }

            var command = new GAgentDraftRunCommand(
                ScopeId: scopeId,
                ActorTypeName: preparedActor!.ActorTypeName,
                Prompt: request.Prompt.Trim(),
                PreferredActorId: preparedActor.ActorId,
                SessionId: string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId.Trim(),
                NyxIdAccessToken: bearerToken,
                ModelOverride: defaultModel,
                PreferredLlmRoute: preferredRoute);

            var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : 120_000;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var interaction = await interactionService.ExecuteAsync(
                command,
                EmitAsync,
                OnAcceptedAsync,
                timeoutCts.Token);

            if (!interaction.Succeeded)
            {
                if (preparedActor.RequiresRollbackOnFailure)
                    await actorPreparationPort.RollbackAsync(preparedActor, CancellationToken.None);

                if (interaction.Error == GAgentDraftRunStartError.UnknownActorType)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteJsonErrorAsync(
                        http.Response,
                        "UNKNOWN_GAGENT_TYPE",
                        $"GAgent type '{request.ActorTypeName}' could not be resolved.",
                        ct);
                }
                else if (interaction.Error == GAgentDraftRunStartError.ActorTypeMismatch)
                {
                    http.Response.StatusCode = StatusCodes.Status409Conflict;
                    await WriteJsonErrorAsync(
                        http.Response,
                        "GAGENT_ACTOR_TYPE_MISMATCH",
                        $"Actor '{preparedActor.ActorId}' is not compatible with requested type '{preparedActor.ActorTypeName}'.",
                        ct);
                }

                return;
            }

            if (!responseStarted && interaction.Receipt != null)
                await OnAcceptedAsync(interaction.Receipt, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (preparedActor?.RequiresRollbackOnFailure == true && !responseStarted)
                await actorPreparationPort.RollbackAsync(preparedActor, CancellationToken.None);

            try
            {
                await EnsureTimeoutErrorAsync(writer, http.Response, responseStarted, ct: CancellationToken.None);
                responseStarted = true;
            }
            catch
            {
                // Best-effort.
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected.
        }
        catch (Exception ex)
        {
            if (preparedActor?.RequiresRollbackOnFailure == true && !responseStarted)
                await actorPreparationPort.RollbackAsync(preparedActor, CancellationToken.None);

            logger.LogError(ex, "GAgent draft-run failed for type {TypeName}", request.ActorTypeName);
            var isAuthRequired = IsNyxIdAuthenticationRequired(ex);

            if (!responseStarted)
            {
                http.Response.StatusCode = isAuthRequired
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status500InternalServerError;
                await WriteJsonErrorAsync(
                    http.Response,
                    isAuthRequired ? "authentication_required" : "GAGENT_DRAFT_RUN_FAILED",
                    isAuthRequired ? "NyxID authentication required. Please sign in." : ex.Message,
                    ct);
                return;
            }

            try
            {
                await writer.WriteAsync(
                    new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = isAuthRequired
                                ? "NyxID authentication required. Please sign in."
                                : ex.Message,
                            Code = isAuthRequired ? "authentication_required" : null,
                        },
                    },
                    CancellationToken.None);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private static async Task EnsureTimeoutErrorAsync(
        AGUISseWriter writer,
        HttpResponse response,
        bool responseStarted,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(response);

        if (!responseStarted)
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.Headers.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Accel-Buffering"] = "no";
            await response.StartAsync(ct);
        }

        await writer.WriteAsync(
            new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Message = "GAgent draft-run timed out.",
                }
            },
            ct);
    }

    /// <summary>
    /// Maps an EventEnvelope payload to an AGUIEvent wrapper.
    /// RoleGAgent publishes AI Abstractions event types (aevatar.ai.*);
    /// this maps them to the AGUI presentation types for SSE streaming.
    /// </summary>
    internal static AGUIEvent? TryMapEnvelopeToAguiEvent(EventEnvelope envelope)
        => ScopeGAgentAguiEventMapper.TryMap(envelope);

    /// <summary>
    /// Decode ToolApprovalRequestEvent from raw Any bytes into a google.protobuf.Struct
    /// so the AGUI SSE JsonFormatter can serialize it without needing the AI.Abstractions
    /// type registered in its TypeRegistry.
    /// </summary>
    private static Google.Protobuf.WellKnownTypes.Struct BuildToolApprovalStruct(Any payload)
        => ScopeGAgentAguiEventMapper.BuildToolApprovalStruct(payload);

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
