using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Hosting;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Hosting.Endpoints;

/// <summary>
/// Team-first Studio HTTP surface mounted under
/// <c>/api/scopes/{scopeId}/teams</c> (ADR-0017). Endpoints depend only on
/// <see cref="IStudioTeamService"/>; they never reach for the projection
/// command port directly.
///
/// Error mapping mirrors <see cref="StudioMemberEndpoints"/>:
///   - <see cref="StudioTeamNotFoundException"/> → 404
///   - other <see cref="InvalidOperationException"/> (validation) → 400
///
/// Like the member endpoints, every <see cref="IStudioTeamService"/>
/// parameter must carry <see cref="FromServicesAttribute"/> so Minimal API's
/// <c>RequestDelegateFactory</c> resolves the dependency from DI rather than
/// probing the interface for a <c>BindAsync</c> custom-binder hook.
/// </summary>
internal static class StudioTeamEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/scopes/{scopeId}/teams", HandleCreateAsync)
            .WithTags("StudioTeams");
        app.MapGet("/api/scopes/{scopeId}/teams", HandleListAsync)
            .WithTags("StudioTeams");
        app.MapGet("/api/scopes/{scopeId}/teams/{teamId}", HandleGetAsync)
            .WithTags("StudioTeams");
        app.MapPatch("/api/scopes/{scopeId}/teams/{teamId}", HandlePatchAsync)
            .WithTags("StudioTeams");
        app.MapPost(
                "/api/scopes/{scopeId}/teams/{teamId}/archive",
                HandleArchiveAsync)
            .WithTags("StudioTeams");
        app.MapPut(
                "/api/scopes/{scopeId}/teams/{teamId}/entry-member",
                HandleSetEntryMemberAsync)
            .WithTags("StudioTeams");
        app.MapDelete(
                "/api/scopes/{scopeId}/teams/{teamId}/entry-member",
                HandleClearEntryMemberAsync)
            .WithTags("StudioTeams");

        // Team -> members listing: queries the member read model filtered by
        // team_id (per ADR-0017 §HTTP endpoints — the team read model itself
        // does NOT mirror the full roster).
        app.MapGet(
                "/api/scopes/{scopeId}/teams/{teamId}/members",
                HandleListMembersAsync)
            .WithTags("StudioTeams");
        app.MapPost(
                "/api/scopes/{scopeId}/teams/{teamId}/invoke/{endpointId}:stream",
                HandleInvokeTeamStreamAsync)
            .WithTags("StudioTeams");
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string scopeId,
        CreateStudioTeamRequest request,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var summary = await teamService.CreateAsync(scopeId, request, ct);
            return Results.Created($"/api/scopes/{scopeId}/teams/{summary.TeamId}", summary);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleListAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IStudioTeamService teamService,
        int? pageSize,
        string? pageToken,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var page = (pageSize.HasValue || !string.IsNullOrWhiteSpace(pageToken))
                ? new StudioTeamRosterPageRequest(pageSize, pageToken)
                : null;
            return Results.Ok(await teamService.ListAsync(scopeId, page, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleGetAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await teamService.GetAsync(scopeId, teamId, ct));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    /// <summary>
    /// Wire body for PATCH /teams/{teamId}. Same Merge-Patch semantics locked
    /// in ADR-0017 §Q6: a field absent in JSON means "no change"; an
    /// explicit null clears (description only); a non-empty string sets;
    /// empty string is rejected.
    /// </summary>
    public sealed class StudioTeamPatchBody
    {
        public System.Text.Json.JsonElement? DisplayName { get; set; }
        public System.Text.Json.JsonElement? Description { get; set; }
    }

    internal static async Task<IResult> HandlePatchAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        StudioTeamPatchBody body,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (body == null)
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", "request body is required.");

        // displayName: if present, must be a non-empty string. Reject null /
        // empty / non-string per ADR-0017 §Q6 (display_name is required-when-present).
        var displayNamePatch = PatchValue<string>.Absent;
        if (body.DisplayName.HasValue)
        {
            var v = body.DisplayName.Value;
            if (v.ValueKind != System.Text.Json.JsonValueKind.String)
                return BadRequest(
                    "INVALID_STUDIO_TEAM_REQUEST",
                    "displayName must be a non-empty string when present.");

            var raw = v.GetString();
            if (string.IsNullOrEmpty(raw))
                return BadRequest(
                    "INVALID_STUDIO_TEAM_REQUEST",
                    "displayName must be a non-empty string when present.");

            displayNamePatch = PatchValue<string>.Of(raw);
        }

        // description: if present, may be a string (set/clear). Reject non-string.
        var descriptionPatch = PatchValue<string>.Absent;
        if (body.Description.HasValue)
        {
            var v = body.Description.Value;
            switch (v.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Null:
                    descriptionPatch = PatchValue<string>.Of(null);
                    break;
                case System.Text.Json.JsonValueKind.String:
                    descriptionPatch = PatchValue<string>.Of(v.GetString());
                    break;
                default:
                    return BadRequest(
                        "INVALID_STUDIO_TEAM_REQUEST",
                        "description must be a string, null, or absent.");
            }
        }

        try
        {
            var detail = await teamService.UpdateAsync(
                scopeId,
                teamId,
                new UpdateStudioTeamRequest(displayNamePatch, descriptionPatch),
                ct);
            return Results.Ok(detail);
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleArchiveAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await teamService.ArchiveAsync(scopeId, teamId, ct));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleSetEntryMemberAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        SetStudioTeamEntryMemberRequest request,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await teamService.SetEntryMemberAsync(scopeId, teamId, request, ct));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (StudioMemberNotFoundException ex)
        {
            return MemberNotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_ENTRY_MEMBER_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleClearEntryMemberAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await teamService.ClearEntryMemberAsync(scopeId, teamId, ct));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_ENTRY_MEMBER_REQUEST", ex.Message);
        }
    }

    internal static async Task HandleInvokeTeamStreamAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string endpointId,
        StudioTeamGAgentStreamHttpRequest request,
        [FromServices] IStudioTeamGAgentStreamInvocationService streamInvocationService,
        CancellationToken ct)
    {
        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var responseStarted = false;
            var writer = new AGUISseWriter(http.Response);

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

            async ValueTask OnAcceptedAsync(StaticGAgentStreamAcceptedReceipt receipt, CancellationToken token)
            {
                http.Response.Headers["X-Correlation-Id"] = receipt.GAgentReceipt.CorrelationId;
                await EnsureSseStartedAsync(token);
                await writer.WriteAsync(
                    new AGUIEvent
                    {
                        RunStarted = new RunStartedEvent
                        {
                            ThreadId = receipt.GAgentReceipt.ActorId,
                            RunId = receipt.GAgentReceipt.CommandId,
                        },
                    },
                    token);
            }

            try
            {
                await streamInvocationService.InvokeAsync(
                    new StudioTeamGAgentStreamInvocationRequest(
                        scopeId,
                        teamId,
                        endpointId,
                        new StaticGAgentStreamInvocationInput(
                            Prompt: request.Prompt?.Trim() ?? string.Empty,
                            PreferredActorId: NormalizeOptional(request.ActorId),
                            SessionId: request.SessionId,
                            RevisionId: NormalizeOptional(request.RevisionId),
                            Headers: await BuildScopedHeadersAsync(scopeId, request.Headers, http, ct),
                            InputParts: MapInputParts(request.InputParts))),
                    EmitAsync,
                    OnAcceptedAsync,
                    ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await EnsureSseStartedAsync(CancellationToken.None);
                await writer.WriteAsync(
                    new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = "Studio team GAgent stream timed out.",
                        },
                    },
                    CancellationToken.None);
            }
            catch (Exception ex) when (responseStarted)
            {
                var isAuthRequired = ex is NyxIdAuthenticationRequiredException;
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
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                ResolveTeamEntryHttpStatusCode(ex.Code),
                ex.Code,
                ex.Message,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_STUDIO_TEAM_GAGENT_STREAM_REQUEST",
                ex.Message,
                ct);
        }
    }

    /// <summary>
    /// Lists members assigned to a given team. Queries the member read model
    /// filtered by <c>team_id</c> (ADR-0017 §HTTP endpoints) — the team read
    /// model never mirrors the roster.
    ///
    /// For v1 this iterates the scope's roster and filters in-process. The
    /// member query port today doesn't expose a typed <c>team_id</c> filter,
    /// so the filter happens after the read model returns. A typed filter on
    /// the query port is a follow-up that does not change the wire shape.
    ///
    /// To avoid silent empty pages when team members are spread across
    /// scope-level pages, this method iterates scope pages until enough
    /// team-filtered results are collected. The returned page token is
    /// the scope-level cursor of the page where collection stopped.
    /// </summary>
    internal static async Task<IResult> HandleListMembersAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        [FromServices] IStudioTeamService teamService,
        [FromServices] IStudioMemberService memberService,
        int? pageSize,
        string? pageToken,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            // 404 propagation: missing team is unambiguous, not "team exists
            // with empty roster".
            _ = await teamService.GetAsync(scopeId, teamId, ct);

            const int defaultPageSize = 50;
            const int maxScopePageIterations = 100;

            var effectivePageSize = pageSize ?? defaultPageSize;
            var filtered = new List<Aevatar.Studio.Application.Studio.Contracts.StudioMemberSummaryResponse>();
            var nextCursor = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken;
            string? finalNextPageToken = null;
            var iterations = 0;

            while (filtered.Count < effectivePageSize && iterations < maxScopePageIterations)
            {
                iterations++;
                var page = new StudioMemberRosterPageRequest(effectivePageSize, nextCursor);
                var roster = await memberService.ListAsync(scopeId, page, ct);

                foreach (var member in roster.Members)
                {
                    if (string.Equals(member.TeamId, teamId, StringComparison.Ordinal))
                        filtered.Add(member);
                }

                if (string.IsNullOrWhiteSpace(roster.NextPageToken))
                {
                    finalNextPageToken = null;
                    break;
                }

                nextCursor = roster.NextPageToken;
                finalNextPageToken = nextCursor;
            }

            return Results.Ok(new StudioMemberRosterResponse(
                ScopeId: scopeId,
                Members: filtered,
                NextPageToken: finalNextPageToken));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { code, message });

    private static async Task<Dictionary<string, string>> BuildScopedHeadersAsync(
        string scopeId,
        IReadOnlyDictionary<string, string>? headers,
        HttpContext http,
        CancellationToken ct)
    {
        var scopedHeaders = headers == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        scopedHeaders.Remove("scope_id");
        scopedHeaders.Remove(WorkflowRunCommandMetadataKeys.ScopeId);
        InjectBearerToken(http, scopedHeaders);
        await InjectUserLlmPreferencesAsync(http, scopedHeaders, ct);
        return scopedHeaders;
    }

    private static void InjectBearerToken(HttpContext http, Dictionary<string, string> headers)
    {
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return;

        var bearerToken = auth["Bearer ".Length..].Trim();
        headers[LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken;
        headers[ConnectorRequest.HttpAuthorizationMetadataKey] = $"Bearer {bearerToken}";
    }

    private static async Task InjectUserLlmPreferencesAsync(
        HttpContext http,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        var preferencesStore = http.RequestServices.GetService<INyxIdUserLlmPreferencesStore>();
        if (preferencesStore != null)
        {
            try
            {
                var preferences = await preferencesStore.GetOwnerAsync(ct);
                if (!headers.ContainsKey(LLMRequestMetadataKeys.ModelOverride) &&
                    !string.IsNullOrWhiteSpace(preferences.DefaultModel))
                    headers[LLMRequestMetadataKeys.ModelOverride] = preferences.DefaultModel.Trim();
                if (!headers.ContainsKey(LLMRequestMetadataKeys.NyxIdRoutePreference) &&
                    !string.IsNullOrWhiteSpace(preferences.PreferredRoute))
                    headers[LLMRequestMetadataKeys.NyxIdRoutePreference] = preferences.PreferredRoute.Trim();
            }
            catch
            {
                // Best-effort; fall back to provider defaults if config unavailable.
            }
            return;
        }

        var userConfigStore = http.RequestServices.GetService<IUserConfigQueryPort>();
        if (userConfigStore == null)
            return;

        try
        {
            var userConfig = await userConfigStore.GetAsync(ct);
            if (!headers.ContainsKey(LLMRequestMetadataKeys.ModelOverride) &&
                !string.IsNullOrWhiteSpace(userConfig.DefaultModel))
                headers[LLMRequestMetadataKeys.ModelOverride] = userConfig.DefaultModel.Trim();
            if (!headers.ContainsKey(LLMRequestMetadataKeys.NyxIdRoutePreference) &&
                !string.IsNullOrWhiteSpace(userConfig.PreferredLlmRoute))
                headers[LLMRequestMetadataKeys.NyxIdRoutePreference] = userConfig.PreferredLlmRoute.Trim();
        }
        catch
        {
            // Best-effort; fall back to provider defaults if config unavailable.
        }
    }

    private static IReadOnlyList<GAgentDraftRunInputPart>? MapInputParts(
        IReadOnlyList<StudioTeamStreamContentPartHttpRequest>? parts)
    {
        if (parts is not { Count: > 0 })
            return null;

        return parts
            .Where(p => p != null)
            .Select(p => new GAgentDraftRunInputPart
            {
                Kind = p.Type?.ToLowerInvariant() switch
                {
                    "image" => GAgentDraftRunInputPartKind.Image,
                    "audio" => GAgentDraftRunInputPartKind.Audio,
                    "video" => GAgentDraftRunInputPartKind.Video,
                    "text" => GAgentDraftRunInputPartKind.Text,
                    _ => GAgentDraftRunInputPartKind.Unspecified,
                },
                Text = p.Text,
                DataBase64 = p.DataBase64,
                MediaType = p.MediaType,
                Uri = p.Uri,
                Name = p.Name,
            }).ToList();
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static int ResolveTeamEntryHttpStatusCode(string code) =>
        code switch
        {
            TeamEntryMemberErrorCodes.TeamNotFound => StatusCodes.Status404NotFound,
            TeamEntryMemberErrorCodes.TeamArchived => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberNotConfigured => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberMismatch => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberNotReady => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberNotFound => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(new { code, message }, cancellationToken: ct);
    }

    private static IResult NotFound(StudioTeamNotFoundException ex) =>
        Results.Json(
            new
            {
                code = "STUDIO_TEAM_NOT_FOUND",
                message = ex.Message,
                scopeId = ex.ScopeId,
                teamId = ex.TeamId,
            },
            statusCode: StatusCodes.Status404NotFound);

    private static IResult MemberNotFound(StudioMemberNotFoundException ex) =>
        Results.Json(
            new
            {
                code = "STUDIO_MEMBER_NOT_FOUND",
                message = ex.Message,
                scopeId = ex.ScopeId,
                memberId = ex.MemberId,
            },
            statusCode: StatusCodes.Status404NotFound);

    public sealed record StudioTeamGAgentStreamHttpRequest(
        string? Prompt,
        string? ActorId = null,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null,
        string? RevisionId = null,
        IReadOnlyList<StudioTeamStreamContentPartHttpRequest>? InputParts = null);

    public sealed record StudioTeamStreamContentPartHttpRequest(
        string Type,
        string? Text = null,
        string? DataBase64 = null,
        string? MediaType = null,
        string? Uri = null,
        string? Name = null);
}
