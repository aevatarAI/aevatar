using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Capabilities;
using Aevatar.Capabilities.ExecutionActivity;
using Aevatar.AGUI.Contracts;
using Aevatar.GAgentService.Hosting.Sse;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
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
        group.MapGet("/gagent-types", HandleListGAgentKindsAsync);
        group.MapPost("/{scopeId}/gagent/draft-run", HandleDraftRunAsync);
        group.MapGet("/{scopeId}/gagent-actors", HandleListActorsAsync);
        group.MapPost("/{scopeId}/gagent-actors", HandleAddActorAsync);
        group.MapDelete("/{scopeId}/gagent-actors/{actorId}", HandleRemoveActorAsync);
        group.MapGet("/{scopeId}/execution-events", HandleExecutionEventsAsync);
        return app;
    }

    private static async Task<IResult> HandleListGAgentKindsAsync(
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct)
    {
        var services = await catalogReader.QueryAllAsync(ct: ct);
        var gAgentKinds = new Dictionary<string, GAgentKindCatalogHttpResponse>(StringComparer.Ordinal);

        foreach (var service in services)
        {
            var identity = BuildServiceIdentity(service);
            var revisions = await revisionCatalogReader.GetAsync(identity, ct);
            if (revisions == null)
                continue;

            foreach (var revision in revisions.Revisions)
            {
                var agentKind = revision.Implementation?.Static?.AgentKind?.Trim() ?? string.Empty;
                if (agentKind.Length == 0)
                    continue;

                var endpoints = revision.Endpoints.Select(MapGAgentEndpoint).ToList();
                if (gAgentKinds.TryGetValue(agentKind, out var existing))
                {
                    MergeEndpoints(existing.Endpoints, endpoints);
                    continue;
                }

                gAgentKinds[agentKind] = new GAgentKindCatalogHttpResponse(
                    agentKind,
                    string.IsNullOrWhiteSpace(service.DisplayName) ? agentKind : service.DisplayName,
                    revision.Implementation?.Static?.ActorTypeName?.Trim() ?? string.Empty,
                    endpoints);
            }
        }

        return Results.Ok(gAgentKinds.Values
            .OrderBy(x => x.DisplayName, StringComparer.Ordinal)
            .ThenBy(x => x.AgentKind, StringComparer.Ordinal)
            .ToList());
    }

    private static ServiceIdentity BuildServiceIdentity(ServiceCatalogSnapshot service) =>
        new()
        {
            TenantId = service.TenantId,
            AppId = service.AppId,
            Namespace = service.Namespace,
            ServiceId = service.ServiceId,
        };

    private static GAgentEndpointCatalogHttpResponse MapGAgentEndpoint(ServiceEndpointSnapshot endpoint) =>
        new(
            endpoint.EndpointId,
            string.IsNullOrWhiteSpace(endpoint.DisplayName) ? endpoint.EndpointId : endpoint.DisplayName,
            NormalizeEndpointKind(endpoint.Kind),
            endpoint.RequestTypeUrl,
            endpoint.ResponseTypeUrl,
            endpoint.Description,
            Auto: false);

    private static void MergeEndpoints(
        List<GAgentEndpointCatalogHttpResponse> target,
        IReadOnlyList<GAgentEndpointCatalogHttpResponse> source)
    {
        foreach (var endpoint in source)
        {
            if (target.Any(x => string.Equals(x.EndpointId, endpoint.EndpointId, StringComparison.Ordinal)))
                continue;

            target.Add(endpoint);
        }
    }

    private static string NormalizeEndpointKind(string kind) =>
        kind switch
        {
            nameof(ServiceEndpointKind.Chat) => "chat",
            nameof(ServiceEndpointKind.Command) => "command",
            _ => kind?.Trim().ToLowerInvariant() ?? string.Empty,
        };

    // ─── Draft Run ───

    private static async Task HandleDraftRunAsync(
        HttpContext http,
        string scopeId,
        GAgentDraftRunHttpRequest request,
        [FromServices] IGAgentDraftRunInteractionPort interactionPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.GAgentService.Hosting.ScopeGAgentEndpoints");
        await using var session = new DraftRunSseSession(http.Response);

        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            if (!await TryValidateDraftRunRequestAsync(http.Response, request, ct))
                return;

            var (defaultModel, preferredRoute) = await TryGetUserLlmDefaultsAsync(http, ct);
            var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : 120_000;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            // Refactor (iter56/cluster-868-endpoint-runtime-lifecycle): old=endpoint direct IActorRuntime, new=IGAgentDraftRunInteractionPort + CQRS Core
            // Host keeps HTTP validation and SSE error mapping only.
            // Application owns draft-run actor lifecycle and rollback around command interaction.
            // This covers pre-dispatch observation failures without changing CQRS Core cleanup semantics.
            var interaction = await interactionPort.ExecuteAsync(
                new GAgentDraftRunInteractionRequest(
                    ScopeId: scopeId,
                    AgentKind: request.AgentKind,
                    Prompt: request.Prompt,
                    PreferredActorId: request.PreferredActorId,
                    SessionId: request.SessionId,
                    NyxIdAccessToken: ExtractBearerToken(http),
                    ModelOverride: defaultModel,
                    PreferredLlmRoute: preferredRoute),
                session.EmitAsync,
                session.WriteAcceptedAsync,
                timeoutCts.Token);

            if (!interaction.Succeeded)
            {
                await WriteDraftRunStartErrorAsync(
                    http.Response,
                    interaction.Receipt,
                    request.AgentKind,
                    request.PreferredActorId,
                    interaction.Error,
                    ct);
                return;
            }

            if (!session.ResponseStarted && interaction.Receipt != null)
                await session.WriteAcceptedAsync(interaction.Receipt, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
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
            logger.LogError(ex, "GAgent draft-run failed for agent kind {AgentKind}", request.AgentKind);
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

    private static async Task<bool> TryValidateDraftRunRequestAsync(
        HttpResponse response,
        GAgentDraftRunHttpRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);

        if (request.HasLegacyActorTypeName)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonErrorAsync(
                response,
                "LEGACY_ACTOR_TYPE_NAME_REJECTED",
                "actorTypeName is not accepted. Use agentKind.",
                ct);
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.AgentKind))
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
            var route = UserLlmSelectionRoute.Resolve(userConfig.LlmSelection);
            var model = string.IsNullOrWhiteSpace(userConfig.DefaultModel)
                ? null
                : userConfig.DefaultModel.Trim();

            return (model, route);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task WriteDraftRunStartErrorAsync(
        HttpResponse response,
        GAgentDraftRunAcceptedReceipt? receipt,
        string requestedAgentKind,
        string? requestedActorId,
        GAgentDraftRunStartError error,
        CancellationToken ct)
    {
        switch (error)
        {
            case GAgentDraftRunStartError.UnknownAgentKind:
                response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonErrorAsync(
                    response,
                    "UNKNOWN_GAGENT_KIND",
                    $"GAgent kind '{requestedAgentKind}' could not be resolved.",
                    ct);
                break;
            case GAgentDraftRunStartError.ActorKindMismatch:
                var actorId = string.IsNullOrWhiteSpace(receipt?.ActorId)
                    ? requestedActorId?.Trim()
                    : receipt.ActorId;
                response.StatusCode = StatusCodes.Status409Conflict;
                await WriteJsonErrorAsync(
                    response,
                    "GAGENT_ACTOR_KIND_MISMATCH",
                    string.IsNullOrWhiteSpace(actorId)
                        ? $"Requested actor is not compatible with requested agent kind '{requestedAgentKind}'."
                        : $"Actor '{actorId}' is not compatible with requested agent kind '{requestedAgentKind}'.",
                    ct);
                break;
            case GAgentDraftRunStartError.ProjectionUnavailable:
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await WriteJsonErrorAsync(
                    response,
                    "GAGENT_PROJECTION_UNAVAILABLE",
                    "GAgent projection is unavailable.",
                    ct);
                break;
        }
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

    // Refactor (iter5/cluster-010):
    //   Old: Host exposed EventEnvelope -> AGUI mapper wrappers for endpoint-local tests.
    //   New: AGUI mapping lives behind ScopeGAgentAguiEventMapper and projection session projectors.

    // ─── GAgent Registry ───

    private static async Task<IResult> HandleListActorsAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IGAgentActorRegistryQueryPort registryQueryPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var snapshot = await registryQueryPort.ListActorsAsync(scopeId, ct);
            return Results.Ok(new
            {
                snapshot.ScopeId,
                snapshot.StateVersion,
                snapshot.UpdatedAt,
                snapshot.ObservedAt,
                snapshot.Groups,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { code = "GAGENT_ACTOR_REGISTRY_ERROR", message = ex.Message });
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Aevatar.GAgentService.Hosting.ScopeGAgentEndpoints")
                .LogWarning(ex, "Failed to list GAgent actors from registry read model");
            return Results.Json(
                new { code = "GAGENT_ACTOR_REGISTRY_ERROR", message = "Failed to list GAgent actors from registry read model." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleAddActorAsync(
        HttpContext http,
        string scopeId,
        AddGAgentActorHttpRequest request,
        [FromServices] IGAgentActorRegistryCommandPort registryCommandPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        _ = request;
        _ = registryCommandPort;
        _ = loggerFactory;
        _ = ct;

        return Results.Json(
            new
            {
                code = "DIRECT_GAGENT_ACTOR_REGISTRATION_UNSUPPORTED",
                message = "Direct GAgent actor registry registration is not supported. Create the target resource through its capability command endpoint.",
            },
            statusCode: StatusCodes.Status405MethodNotAllowed);
    }

    private static async Task<IResult> HandleRemoveActorAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        [FromQuery] string? agentKind,
        [FromQuery] string? gagentType,
        [FromServices] IGAgentActorRegistryCommandPort registryCommandPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            if (!string.IsNullOrWhiteSpace(gagentType))
                return Results.BadRequest(new { code = "LEGACY_GAGENT_TYPE_REJECTED", message = "gagentType is not accepted. Use agentKind." });

            if (string.IsNullOrWhiteSpace(agentKind))
                return Results.BadRequest(new { code = "INVALID_REQUEST", message = "agentKind query parameter is required." });

            var registration = new GAgentActorRegistration(scopeId, agentKind.Trim(), actorId.Trim());
            var admission = await admissionPort.AuthorizeTargetAsync(
                new ScopeResourceTarget(
                    registration.ScopeId,
                    ScopeResourceKind.GAgentActor,
                    registration.AgentKind,
                    registration.ActorId,
                    ScopeResourceOperation.Delete),
                ct);
            if (!admission.IsAllowed)
            {
                return admission.Status switch
                {
                    ScopeResourceAdmissionStatus.NotFound => Results.NotFound(new
                    {
                        code = "GAGENT_ACTOR_NOT_FOUND",
                        message = "GAgent actor is not registered in this scope.",
                    }),
                    ScopeResourceAdmissionStatus.Denied or ScopeResourceAdmissionStatus.ScopeMismatch => Results.Json(
                        new { code = "SCOPE_FORBIDDEN", message = "Scope access denied." },
                        statusCode: StatusCodes.Status403Forbidden),
                    _ => Results.Json(
                        new { code = "GAGENT_ACTOR_ADMISSION_UNAVAILABLE", message = "GAgent actor ownership could not be verified." },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                };
            }

            await registryCommandPort.UnregisterActorAsync(registration, ct);
            return Results.Ok();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { code = "GAGENT_ACTOR_REGISTRY_ERROR", message = ex.Message });
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Aevatar.GAgentService.Hosting.ScopeGAgentEndpoints")
                .LogWarning(ex, "Failed to unregister GAgent actor from registry");
            return Results.Json(
                new { code = "GAGENT_ACTOR_REGISTRY_ERROR", message = "Failed to unregister GAgent actor from registry." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // Implement (issue #1703):
    //   Behavior: expose handler lifecycle events as scope-scoped SSE frames without duplicating run-output streams.
    //   Why this shape: the host only adapts scoped stream subscription to SSE; scope auth and typed stream payload stay authoritative.
    private static async Task HandleExecutionEventsAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IStreamProvider streamProvider,
        CancellationToken ct)
    {
        if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
            return;

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var writer = new ExecutionActivityEventJsonSseWriter(http.Response);
        var streamId = ExecutionActivityStreamTopics.ForScope(scopeId);
        await using var subscription = await streamProvider
            .GetStream(streamId)
            .SubscribeAsync<ExecutionActivityEvent>(
                evt => WriteExecutionActivityFrameAsync(writer, scopeId, evt, ct),
                ct);
        await http.Response.StartAsync(ct);

        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            await completion.Task;
        }
        catch (OperationCanceledException)
        {
            // Client disconnected.
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

    private static Task WriteExecutionActivityFrameAsync(
        ExecutionActivityEventJsonSseWriter writer,
        string requestedScopeId,
        ExecutionActivityEvent evt,
        CancellationToken ct)
    {
        if (!string.Equals(evt.ScopeId, requestedScopeId, StringComparison.Ordinal))
            return Task.CompletedTask;

        var timestamp = evt.Ts?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return evt.Stage switch
        {
            ExecutionActivityLifecycleStage.Started => writer.WriteAsync(
                "handler.started",
                new
                {
                    scopeId = evt.ScopeId,
                    actorId = evt.ActorId,
                    agentType = evt.AgentType,
                    handlerName = evt.HandlerName,
                    eventType = evt.EventType,
                    eventId = evt.EventId,
                    ts = timestamp,
                },
                ct),
            ExecutionActivityLifecycleStage.Completed => writer.WriteAsync(
                "handler.completed",
                new
                {
                    scopeId = evt.ScopeId,
                    actorId = evt.ActorId,
                    agentType = evt.AgentType,
                    handlerName = evt.HandlerName,
                    eventType = evt.EventType,
                    eventId = evt.EventId,
                    durationMs = evt.Duration?.ToTimeSpan().TotalMilliseconds,
                    ts = timestamp,
                },
                ct),
            ExecutionActivityLifecycleStage.Failed => writer.WriteAsync(
                "handler.failed",
                new
                {
                    scopeId = evt.ScopeId,
                    actorId = evt.ActorId,
                    agentType = evt.AgentType,
                    handlerName = evt.HandlerName,
                    eventType = evt.EventType,
                    eventId = evt.EventId,
                    durationMs = evt.Duration?.ToTimeSpan().TotalMilliseconds,
                    error = evt.Error,
                    ts = timestamp,
                },
                ct),
            _ => Task.CompletedTask,
        };
    }

    private sealed class DraftRunSseSession(HttpResponse response) : IAsyncDisposable
    {
        private readonly HttpResponse _response = response ?? throw new ArgumentNullException(nameof(response));
        private readonly AGUISseWriter _writer = new(response);

        public bool ResponseStarted => _writer.ResponseStarted;

        public async ValueTask EmitAsync(AGUIEvent aguiEvent, CancellationToken ct)
        {
            await _writer.WriteAsync(aguiEvent, ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
        }

        public async ValueTask WriteAcceptedAsync(GAgentDraftRunAcceptedReceipt receipt, CancellationToken ct)
        {
            _response.Headers["X-Correlation-Id"] = receipt.CorrelationId;
            await _writer.StartAsync(ct);
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
            await _writer.StartAsync(ct);
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
    }

    // ─── Request models ───

    public sealed record GAgentKindCatalogHttpResponse(
        string AgentKind,
        string DisplayName,
        string DiagnosticClrTypeName,
        List<GAgentEndpointCatalogHttpResponse> Endpoints);

    public sealed record GAgentEndpointCatalogHttpResponse(
        string EndpointId,
        string DisplayName,
        string Kind,
        string RequestTypeUrl,
        string ResponseTypeUrl,
        string Description,
        bool Auto);

    public sealed class GAgentDraftRunHttpRequest
    {
        public GAgentDraftRunHttpRequest()
        {
        }

        public GAgentDraftRunHttpRequest(
            string agentKind,
            string prompt,
            string? preferredActorId = null,
            string? sessionId = null,
            int timeoutMs = 0)
        {
            AgentKind = agentKind;
            Prompt = prompt;
            PreferredActorId = preferredActorId;
            SessionId = sessionId;
            TimeoutMs = timeoutMs;
        }

        public string AgentKind { get; init; } = string.Empty;
        public string Prompt { get; init; } = string.Empty;
        public string? PreferredActorId { get; init; }
        public string? SessionId { get; init; }
        public int TimeoutMs { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; init; }

        public bool HasLegacyActorTypeName =>
            ExtensionData?.Keys.Any(key =>
                string.Equals(key, "actorTypeName", StringComparison.Ordinal) ||
                string.Equals(key, "ActorTypeName", StringComparison.Ordinal)) == true;
    }

    public sealed record AddGAgentActorHttpRequest(
        string AgentKind,
        string ActorId);
}
