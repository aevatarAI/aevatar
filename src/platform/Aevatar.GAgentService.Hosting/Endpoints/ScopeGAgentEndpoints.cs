using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Hosting;
using Aevatar.AGUI.Contracts;
using Aevatar.GAgentService.Hosting.Sse;
using Aevatar.Studio.Application.Studio.Abstractions;
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

    // Refactor (iter39/cluster-039-gagent-reflection-catalog):
    //   Old pattern: ScopeGAgentEndpoints 通过 AppDomain reflection + AIGAgentBase + [EventHandler] + protobuf descriptors 发现 GAgent 类型,把进程内加载的 CLR class 当成业务事实源。
    //   New principle: GAgent type 列表必须来自 registered service revision catalog readmodel,不是反射偶然加载的 CLR class。保留 endpoint 路由,换实现为读 readmodel。
    private static async Task<IResult> HandleListGAgentTypesAsync(
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct)
    {
        var services = await catalogReader.QueryAllAsync(ct: ct);
        var gAgentTypes = new Dictionary<string, GAgentTypeCatalogHttpResponse>(StringComparer.Ordinal);

        foreach (var service in services)
        {
            var identity = BuildServiceIdentity(service);
            var revisions = await revisionCatalogReader.GetAsync(identity, ct);
            if (revisions == null)
                continue;

            foreach (var revision in revisions.Revisions)
            {
                var actorTypeName = revision.Implementation?.Static?.ActorTypeName?.Trim() ?? string.Empty;
                if (actorTypeName.Length == 0)
                    continue;

                var endpoints = revision.Endpoints.Select(MapGAgentEndpoint).ToList();
                if (gAgentTypes.TryGetValue(actorTypeName, out var existing))
                {
                    MergeEndpoints(existing.Endpoints, endpoints);
                    continue;
                }

                gAgentTypes[actorTypeName] = new GAgentTypeCatalogHttpResponse(
                    ResolveTypeDisplayName(actorTypeName),
                    actorTypeName,
                    ResolveAssemblyName(actorTypeName),
                    endpoints);
            }
        }

        return Results.Ok(gAgentTypes.Values
            .OrderBy(x => x.TypeName, StringComparer.Ordinal)
            .ThenBy(x => x.FullName, StringComparer.Ordinal)
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

    private static string ResolveTypeDisplayName(string actorTypeName)
    {
        var typeName = actorTypeName.Split(',', 2)[0].Trim();
        var lastDot = typeName.LastIndexOf('.');
        return lastDot < 0 ? typeName : typeName[(lastDot + 1)..];
    }

    private static string ResolveAssemblyName(string actorTypeName)
    {
        var separator = actorTypeName.IndexOf(',');
        return separator < 0 ? string.Empty : actorTypeName[(separator + 1)..].Trim();
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
        var session = new DraftRunSseSession(http.Response);

        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            if (!TryValidateDraftRunRequest(http.Response, request))
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
                    ActorTypeName: request.ActorTypeName,
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
                    request.ActorTypeName,
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
            var route = string.IsNullOrWhiteSpace(userConfig.PreferredLlmRoute)
                ? null
                : userConfig.PreferredLlmRoute.Trim();
            var model = string.IsNullOrWhiteSpace(userConfig.DefaultModel)
                ? null
                : userConfig.DefaultModel.Trim();

            return await UserLlmRouteModelResolver
                .ResolveAsync(http, model, route, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task WriteDraftRunStartErrorAsync(
        HttpResponse response,
        GAgentDraftRunAcceptedReceipt? receipt,
        string requestedActorTypeName,
        string? requestedActorId,
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
            case GAgentDraftRunStartError.ActorTypeMismatch:
                var actorId = string.IsNullOrWhiteSpace(receipt?.ActorId)
                    ? requestedActorId?.Trim()
                    : receipt.ActorId;
                var actorTypeName = string.IsNullOrWhiteSpace(receipt?.ActorTypeName)
                    ? requestedActorTypeName
                    : receipt.ActorTypeName;
                response.StatusCode = StatusCodes.Status409Conflict;
                await WriteJsonErrorAsync(
                    response,
                    "GAGENT_ACTOR_TYPE_MISMATCH",
                    string.IsNullOrWhiteSpace(actorId)
                        ? $"Requested actor is not compatible with requested type '{actorTypeName}'."
                        : $"Actor '{actorId}' is not compatible with requested type '{actorTypeName}'.",
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
            if (string.IsNullOrWhiteSpace(gagentType))
                return Results.BadRequest(new { code = "INVALID_REQUEST", message = "gagentType query parameter is required." });

            var registration = new GAgentActorRegistration(scopeId, gagentType.Trim(), actorId.Trim());
            var admission = await admissionPort.AuthorizeTargetAsync(
                new ScopeResourceTarget(
                    registration.ScopeId,
                    ScopeResourceKind.GAgentActor,
                    registration.GAgentType,
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

    public sealed record GAgentTypeCatalogHttpResponse(
        string TypeName,
        string FullName,
        string AssemblyName,
        List<GAgentEndpointCatalogHttpResponse> Endpoints);

    public sealed record GAgentEndpointCatalogHttpResponse(
        string EndpointId,
        string DisplayName,
        string Kind,
        string RequestTypeUrl,
        string ResponseTypeUrl,
        string Description,
        bool Auto);

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
