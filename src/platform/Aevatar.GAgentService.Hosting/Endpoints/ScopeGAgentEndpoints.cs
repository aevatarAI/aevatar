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
        var session = new DraftRunSseSession(http.Response);
        GAgentDraftRunPreparedActor? preparedActor = null;

        try
        {
            if (!TryValidateDraftRunRequest(http.Response, request))
                return;

            preparedActor = await TryPrepareDraftRunActorAsync(
                actorPreparationPort,
                http.Response,
                scopeId,
                request,
                ct);
            if (preparedActor is null)
                return;
            var command = await BuildDraftRunCommandAsync(http, scopeId, request, preparedActor, ct);

            var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : 120_000;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var interaction = await interactionService.ExecuteAsync(
                command,
                session.EmitAsync,
                session.WriteAcceptedAsync,
                timeoutCts.Token);

            if (!interaction.Succeeded)
            {
                await RollbackPreparedActorAsync(actorPreparationPort, preparedActor);
                await WriteDraftRunStartErrorAsync(http.Response, preparedActor, request.ActorTypeName, interaction.Error, ct);
                return;
            }

            if (!session.ResponseStarted && interaction.Receipt != null)
                await session.WriteAcceptedAsync(interaction.Receipt, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await RollbackPreparedActorIfPendingAsync(actorPreparationPort, preparedActor, session.ResponseStarted);

            try
            {
                await session.WriteTimeoutAsync(CancellationToken.None);
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
            await RollbackPreparedActorIfPendingAsync(actorPreparationPort, preparedActor, session.ResponseStarted);

            logger.LogError(ex, "GAgent draft-run failed for type {TypeName}", request.ActorTypeName);
            var isAuthRequired = IsNyxIdAuthenticationRequired(ex);

            if (!session.ResponseStarted)
            {
                await WriteDraftRunExceptionJsonAsync(http.Response, ex, isAuthRequired, ct);
                return;
            }

            try
            {
                await session.WriteRunErrorAsync(
                    isAuthRequired ? "NyxID authentication required. Please sign in." : ex.Message,
                    isAuthRequired ? "authentication_required" : null,
                    CancellationToken.None);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private static bool TryValidateDraftRunRequest(
        HttpResponse response,
        GAgentDraftRunHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ActorTypeName))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }

        return true;
    }

    private static async Task<GAgentDraftRunPreparedActor?> TryPrepareDraftRunActorAsync(
        IGAgentDraftRunActorPreparationPort actorPreparationPort,
        HttpResponse response,
        string scopeId,
        GAgentDraftRunHttpRequest request,
        CancellationToken ct)
    {
        var preparation = await actorPreparationPort.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                scopeId,
                request.ActorTypeName,
                request.PreferredActorId),
            ct);
        if (!preparation.Succeeded)
        {
            await WriteDraftRunStartErrorAsync(response, preparedActor: null, request.ActorTypeName, preparation.Error, ct);
            return null;
        }

        return preparation.PreparedActor;
    }

    private static async Task<GAgentDraftRunCommand> BuildDraftRunCommandAsync(
        HttpContext http,
        string scopeId,
        GAgentDraftRunHttpRequest request,
        GAgentDraftRunPreparedActor preparedActor,
        CancellationToken ct)
    {
        var (defaultModel, preferredRoute) = await TryGetUserLlmDefaultsAsync(http, ct);
        return new GAgentDraftRunCommand(
            ScopeId: scopeId,
            ActorTypeName: preparedActor.ActorTypeName,
            Prompt: request.Prompt.Trim(),
            PreferredActorId: preparedActor.ActorId,
            SessionId: string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId.Trim(),
            NyxIdAccessToken: ExtractBearerToken(http),
            ModelOverride: defaultModel,
            PreferredLlmRoute: preferredRoute);
    }

    private static async Task<(string? DefaultModel, string? PreferredRoute)> TryGetUserLlmDefaultsAsync(
        HttpContext http,
        CancellationToken ct)
    {
        var userConfigStore = http.RequestServices.GetService<IUserConfigQueryPort>();
        if (userConfigStore is null)
            return (null, null);

        try
        {
            var userConfig = await userConfigStore.GetAsync(ct);
            return (
                string.IsNullOrWhiteSpace(userConfig.DefaultModel) ? null : userConfig.DefaultModel.Trim(),
                string.IsNullOrWhiteSpace(userConfig.PreferredLlmRoute) ? null : userConfig.PreferredLlmRoute.Trim());
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task WriteDraftRunStartErrorAsync(
        HttpResponse response,
        GAgentDraftRunPreparedActor? preparedActor,
        string requestedActorTypeName,
        GAgentDraftRunStartError error,
        CancellationToken ct)
    {
        switch (error)
        {
            case GAgentDraftRunStartError.UnknownActorType:
                response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonErrorAsync(
                    response,
                    "UNKNOWN_GAGENT_TYPE",
                    $"GAgent type '{requestedActorTypeName}' could not be resolved.",
                    ct);
                break;
            case GAgentDraftRunStartError.ActorTypeMismatch when preparedActor is not null:
                response.StatusCode = StatusCodes.Status409Conflict;
                await WriteJsonErrorAsync(
                    response,
                    "GAGENT_ACTOR_TYPE_MISMATCH",
                    $"Actor '{preparedActor.ActorId}' is not compatible with requested type '{preparedActor.ActorTypeName}'.",
                    ct);
                break;
        }
    }

    private static async Task RollbackPreparedActorAsync(
        IGAgentDraftRunActorPreparationPort actorPreparationPort,
        GAgentDraftRunPreparedActor? preparedActor)
    {
        if (preparedActor?.RequiresRollbackOnFailure == true)
            await actorPreparationPort.RollbackAsync(preparedActor, CancellationToken.None);
    }

    private static async Task RollbackPreparedActorIfPendingAsync(
        IGAgentDraftRunActorPreparationPort actorPreparationPort,
        GAgentDraftRunPreparedActor? preparedActor,
        bool responseStarted)
    {
        if (responseStarted)
            return;

        await RollbackPreparedActorAsync(actorPreparationPort, preparedActor);
    }

    private static async Task WriteDraftRunExceptionJsonAsync(
        HttpResponse response,
        Exception ex,
        bool isAuthRequired,
        CancellationToken ct)
    {
        response.StatusCode = isAuthRequired
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status500InternalServerError;
        await WriteJsonErrorAsync(
            response,
            isAuthRequired ? "authentication_required" : "GAGENT_DRAFT_RUN_FAILED",
            isAuthRequired ? "NyxID authentication required. Please sign in." : ex.Message,
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

    private sealed class DraftRunSseSession(HttpResponse response)
    {
        private readonly HttpResponse _response = response ?? throw new ArgumentNullException(nameof(response));
        private readonly AGUISseWriter _writer = new(response);

        public bool ResponseStarted { get; private set; }

        public async ValueTask EmitAsync(AGUIEvent aguiEvent, CancellationToken ct)
        {
            await EnsureStartedAsync(ct);
            await _writer.WriteAsync(aguiEvent, ct);
        }

        public async ValueTask WriteAcceptedAsync(GAgentDraftRunAcceptedReceipt receipt, CancellationToken ct)
        {
            _response.Headers["X-Correlation-Id"] = receipt.CorrelationId;
            await EnsureStartedAsync(ct);
            await _writer.WriteAsync(
                new AGUIEvent
                {
                    RunStarted = new RunStartedEvent
                    {
                        ThreadId = receipt.ActorId,
                        RunId = receipt.CommandId,
                    },
                },
                ct);
        }

        public Task WriteTimeoutAsync(CancellationToken ct) =>
            WriteRunErrorAsync("GAgent draft-run timed out.", code: null, ct);

        public async Task WriteRunErrorAsync(string message, string? code, CancellationToken ct)
        {
            await EnsureStartedAsync(ct);
            await _writer.WriteAsync(
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = message,
                        Code = code,
                    },
                },
                ct);
        }

        private async Task EnsureStartedAsync(CancellationToken ct)
        {
            if (ResponseStarted)
                return;

            _response.StatusCode = StatusCodes.Status200OK;
            _response.Headers.ContentType = "text/event-stream; charset=utf-8";
            _response.Headers.CacheControl = "no-store";
            _response.Headers["X-Accel-Buffering"] = "no";
            await _response.StartAsync(ct);
            ResponseStarted = true;
        }
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
