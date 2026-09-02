using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

internal static class AgentProfileEndpoints
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IfMatchHeader = "If-Match";

    internal sealed record SystemAdminAuthorization(string BearerToken, PlatformCaller? Caller, IResult? Error);

    public static IEndpointRouteBuilder MapAgentProfileEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var scopeProfiles = app.MapGroup("/api/scopes/{scopeId}").WithTags("AgentProfiles").RequireAuthorization();
        Audit(scopeProfiles.MapGet("/agent-profiles", ListScopeAsync), "list", "scopeId");
        Audit(scopeProfiles.MapPost("/agent-profiles", CreateScopeAsync), "create", "scopeId");
        Audit(scopeProfiles.MapGet("/agent-profiles/{profileSlug}", GetScopeAsync), "get", "scopeId", "profileSlug");
        Audit(scopeProfiles.MapPut("/agent-profiles/{profileSlug}/draft", UpdateScopeDraftAsync), "update-draft", "scopeId", "profileSlug");
        Audit(scopeProfiles.MapPost("/agent-profiles/{profileSlug}:validate", ValidateScopeAsync), "validate", "scopeId", "profileSlug");
        Audit(scopeProfiles.MapPost("/agent-profiles/{profileSlug}:publish", PublishScopeAsync), "publish", "scopeId", "profileSlug");
        Audit(scopeProfiles.MapGet("/agent-profile-bindings/{agentKind}", GetScopeBindingAsync), "get-binding", "scopeId", "agentKind");
        Audit(scopeProfiles.MapPut("/agent-profile-bindings/{agentKind}", SetScopeBindingAsync), "set-binding", "scopeId", "agentKind");
        Audit(scopeProfiles.MapDelete("/agent-profile-bindings/{agentKind}", ClearScopeBindingAsync), "clear-binding", "scopeId", "agentKind");

        var systemProfiles = app.MapGroup("/api/agent-profiles").WithTags("AgentProfiles").RequireAuthorization();
        systemProfiles.MapGet("/system", ListSystemAsync);
        systemProfiles.MapGet("/system/{profileSlug}", GetSystemAsync);

        var adminProfiles = app.MapGroup("/api/admin").WithTags("AgentProfileAdmin").RequireAuthorization();
        Audit(adminProfiles.MapGet("/agent-profiles", ListAdminAsync), "admin-list", "platform");
        Audit(adminProfiles.MapPost("/agent-profiles", CreateAdminAsync), "admin-create", "platform");
        Audit(adminProfiles.MapGet("/agent-profiles/{profileSlug}", GetAdminAsync), "admin-get", "profileSlug");
        Audit(adminProfiles.MapPut("/agent-profiles/{profileSlug}/draft", UpdateAdminDraftAsync), "admin-update-draft", "profileSlug");
        Audit(adminProfiles.MapPost("/agent-profiles/{profileSlug}:validate", ValidateAdminAsync), "admin-validate", "profileSlug");
        Audit(adminProfiles.MapPost("/agent-profiles/{profileSlug}:publish", PublishAdminAsync), "admin-publish", "profileSlug");
        Audit(adminProfiles.MapGet("/agent-profile-bindings/{agentKind}", GetAdminBindingAsync), "admin-get-binding", "agentKind");
        Audit(adminProfiles.MapPut("/agent-profile-bindings/{agentKind}", SetAdminBindingAsync), "admin-set-binding", "agentKind");
        Audit(adminProfiles.MapDelete("/agent-profile-bindings/{agentKind}", ClearAdminBindingAsync), "admin-clear-binding", "agentKind");

        app.MapGet("/api/agent-profiles/editor-options", GetEditorOptions)
            .WithTags("AgentProfiles")
            .RequireAuthorization();
        return app;
    }

    internal static async Task<SystemAdminAuthorization> AuthorizeSystemAdminAsync(
        HttpContext http, IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        if (authorizer is null)
            return new(string.Empty, null, Error(StatusCodes.Status503ServiceUnavailable, "ADMIN_AUTHORIZATION_UNAVAILABLE", "Admin authorization is unavailable."));

        var bearerToken = BearerToken(http);
        if (bearerToken.Length == 0)
            return new(string.Empty, null, Results.Forbid());

        PlatformCaller caller;
        try { caller = await authorizer.ResolveCallerAsync(bearerToken, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(string.Empty, null, Results.Forbid()); }

        if (!caller.IsElevated || string.IsNullOrWhiteSpace(caller.UserId) ||
            !string.Equals(caller.GrantSource, PlatformAdminGrantSources.AllowedUserId, StringComparison.Ordinal))
            return new(string.Empty, null, Results.Forbid());

        return new(bearerToken, caller, null);
    }

    private static Task<IResult> ListScopeAsync(HttpContext http, string scopeId, [FromServices] AgentProfileApplicationService service, string? cursor, int? take, int? pageSize, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        return ExecuteAsync(async () => List(await service.ListAsync(ScopeOwner(scopeId), cursor, Take(take, pageSize), ct), ScopeOwner(scopeId)));
    }

    private static Task<IResult> CreateScopeAsync(HttpContext http, string scopeId, AgentProfileCreateInput? input, [FromServices] AgentProfileApplicationService service, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        if (!TryAuditSubject(http, out var subject)) return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));
        return CreateAsync(
            http,
            service,
            ScopeOwner(scopeId),
            input?.ProfileSlug,
            input?.IdempotencyKey,
            subject,
            slug => ScopeProfileUrl(scopeId, slug),
            ct);
    }

    private static Task<IResult> GetScopeAsync(HttpContext http, string scopeId, string profileSlug, [FromServices] AgentProfileApplicationService service, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        return GetDetailAsync(service, ScopeOwner(scopeId), profileSlug, ct);
    }

    private static Task<IResult> UpdateScopeDraftAsync(HttpContext http, string scopeId, string profileSlug, AgentProfileDraftUpdateInput? input, [FromServices] AgentProfileApplicationService service, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        if (!TryAuditSubject(http, out var subject)) return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));
        return UpdateDraftAsync(http, service, ScopeOwner(scopeId), profileSlug, input, subject, ScopeProfileUrl(scopeId, profileSlug), ct);
    }

    private static Task<IResult> ValidateScopeAsync(HttpContext http, string scopeId, string profileSlug, [FromServices] AgentProfileApplicationService service, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        return ValidateAsync(service, ScopeOwner(scopeId), profileSlug, BearerToken(http), ct);
    }

    private static Task<IResult> PublishScopeAsync(HttpContext http, string scopeId, string profileSlug, [FromServices] AgentProfileApplicationService service, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        if (!TryAuditSubject(http, out var subject)) return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));
        return PublishAsync(http, service, ScopeOwner(scopeId), profileSlug, subject, BearerToken(http), ScopeProfileUrl(scopeId, profileSlug), ct);
    }

    private static Task<IResult> GetScopeBindingAsync(HttpContext http, string scopeId, string agentKind, [FromServices] AgentProfileApplicationService service, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        return GetBindingAsync(service, ScopeOwner(scopeId), agentKind, ct);
    }

    private static Task<IResult> SetScopeBindingAsync(HttpContext http, string scopeId, string agentKind, AgentProfileBindingInput? input, [FromServices] AgentProfileApplicationService service, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        if (!TryAuditSubject(http, out var subject)) return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));
        return SetBindingAsync(http, service, ScopeOwner(scopeId), agentKind, input, subject, ScopeBindingUrl(scopeId, agentKind), ct);
    }

    private static Task<IResult> ClearScopeBindingAsync(HttpContext http, string scopeId, string agentKind, [FromServices] AgentProfileApplicationService service, CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied)) return Task.FromResult(denied);
        if (!TryAuditSubject(http, out var subject)) return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));
        return ClearBindingAsync(http, service, ScopeOwner(scopeId), agentKind, subject, ScopeBindingUrl(scopeId, agentKind), ct);
    }

    private static Task<IResult> ListSystemAsync([FromServices] AgentProfileApplicationService service, string? cursor, int? take, int? pageSize, CancellationToken ct) => ExecuteAsync(async () =>
    {
        var page = await service.ListPublishedAsync(
            AgentProfileOwners.ForSystem(),
            cursor,
            Take(take, pageSize),
            ct);
        return List(page, AgentProfileOwners.ForSystem(), includeMutation: false);
    });

    private static Task<IResult> GetSystemAsync(string profileSlug, [FromServices] AgentProfileApplicationService service, CancellationToken ct) => ExecuteAsync(async () =>
    {
        var entry = await service.GetPublishedSummaryAsync(AgentProfileOwners.ForSystem(), profileSlug, ct);
        return entry is null
            ? Error(StatusCodes.Status404NotFound, "AGENT_PROFILE_NOT_FOUND", "Agent Profile was not found.")
            : Results.Ok(Summary(entry, AgentProfileOwners.ForSystem()));
    });

    private static async Task<IResult> ListAdminAsync(HttpContext http, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, string? cursor, int? take, int? pageSize, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await ExecuteAsync(async () => List(await service.ListAsync(AgentProfileOwners.ForSystem(), cursor, Take(take, pageSize), ct), AgentProfileOwners.ForSystem()));
    }

    private static async Task<IResult> CreateAdminAsync(HttpContext http, AgentProfileCreateInput? input, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await CreateAsync(
            http,
            service,
            AgentProfileOwners.ForSystem(),
            input?.ProfileSlug,
            input?.IdempotencyKey,
            admin.Caller!.UserId,
            AdminProfileUrl,
            ct);
    }

    private static async Task<IResult> GetAdminAsync(HttpContext http, string profileSlug, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await GetDetailAsync(service, AgentProfileOwners.ForSystem(), profileSlug, ct);
    }

    private static async Task<IResult> UpdateAdminDraftAsync(HttpContext http, string profileSlug, AgentProfileDraftUpdateInput? input, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await UpdateDraftAsync(http, service, AgentProfileOwners.ForSystem(), profileSlug, input, admin.Caller!.UserId, AdminProfileUrl(profileSlug), ct);
    }

    private static async Task<IResult> ValidateAdminAsync(HttpContext http, string profileSlug, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await ValidateAsync(service, AgentProfileOwners.ForSystem(), profileSlug, admin.BearerToken, ct);
    }

    private static async Task<IResult> PublishAdminAsync(HttpContext http, string profileSlug, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await PublishAsync(http, service, AgentProfileOwners.ForSystem(), profileSlug, admin.Caller!.UserId, admin.BearerToken, AdminProfileUrl(profileSlug), ct);
    }

    private static async Task<IResult> GetAdminBindingAsync(HttpContext http, string agentKind, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await GetBindingAsync(service, AgentProfileOwners.ForSystem(), agentKind, ct);
    }

    private static async Task<IResult> SetAdminBindingAsync(HttpContext http, string agentKind, AgentProfileBindingInput? input, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await SetBindingAsync(http, service, AgentProfileOwners.ForSystem(), agentKind, input, admin.Caller!.UserId, AdminBindingUrl(agentKind), ct);
    }

    private static async Task<IResult> ClearAdminBindingAsync(HttpContext http, string agentKind, [FromServices] AgentProfileApplicationService service, [FromServices] IPlatformAdminAuthorizer? authorizer, CancellationToken ct)
    {
        var admin = await AuthorizeSystemAdminAsync(http, authorizer, ct); if (admin.Error is not null) return admin.Error;
        return await ClearBindingAsync(http, service, AgentProfileOwners.ForSystem(), agentKind, admin.Caller!.UserId, AdminBindingUrl(agentKind), ct);
    }

    internal static IResult GetEditorOptions() => Results.Ok(new
    {
        activationModes = new[] { "SHADOW", "ENFORCED" },
        sideEffectClasses = new[] { "READ_ONLY", "EXTERNAL_HANDOFF", "SERVICE_CALL", "MAINTENANCE" },
        referenceOwnerKinds = new[] { "caller", "system" },
        supportedAgentKinds = new[]
        {
            AgentProfilePolicies.WorkspaceChatAgentKind,
            AgentProfilePolicies.ChannelReplyAgentKind,
            AgentProfilePolicies.NyxIdChatAgentKind,
        },
        allowedRouteToolSetRefs = new[]
        {
            AgentProfilePolicies.WorkspaceChatRouteToolSet,
            AgentProfilePolicies.NyxIdChatRouteToolSet,
        },
        runtimeParameters = new
        {
            maxPlanSteps = AgentProfileValidationLimits.RequiredMaxPlanSteps,
            handoffTtlSeconds = AgentProfileValidationLimits.RequiredHandoffTtlSeconds,
            classifierTimeoutMs = AgentProfileValidationLimits.RequiredClassifierTimeoutMs,
            exactSkillFetchTimeoutMs = AgentProfileValidationLimits.RequiredExactSkillFetchTimeoutMs,
            maxSelectedSkillBytes = AgentProfileValidationLimits.RequiredMaxSelectedSkillBytes,
            maximumMembers = AgentProfileValidationLimits.MaximumMembers,
        },
        maximumPageSize = AgentProfileApplicationService.MaximumPageSize,
    });

    internal static async Task<IResult> CreateAsync(
        HttpContext http,
        AgentProfileApplicationService service,
        AgentProfileOwner owner,
        string? profileSlug,
        string? bodyIdempotencyKey,
        string subject,
        Func<string, string> resourceUrl,
        CancellationToken ct,
        bool includeActorId = true,
        bool callerFacing = false) => await ExecuteAsync(async () =>
    {
        var normalizedSlug = Required(profileSlug, "profileSlug");
        var key = Idempotency(http, bodyIdempotencyKey);
        var receipt = await service.CreateAsync(new(owner, normalizedSlug, key, subject), ct);
        return Accepted(receipt, resourceUrl(normalizedSlug), includeActorId);
    }, callerFacing, ct);

    internal static async Task<IResult> GetDetailAsync(AgentProfileApplicationService service, AgentProfileOwner owner, string profileSlug, CancellationToken ct, bool includeOwnerKind = true, bool callerFacing = false) => await ExecuteAsync(async () =>
    {
        var detail = await service.GetAsync(owner, profileSlug, ct);
        if (detail is null)
        {
            return callerFacing
                ? CallerError(StatusCodes.Status404NotFound, "AI_AGENT_NOT_FOUND", "Agent was not found.")
                : Error(StatusCodes.Status404NotFound, "AGENT_PROFILE_NOT_FOUND", "Agent Profile was not found.");
        }
        return WithEtag(detail.StrongETag, Detail(detail, includeOwnerKind));
    }, callerFacing, ct);

    internal static async Task<IResult> UpdateDraftAsync(HttpContext http, AgentProfileApplicationService service, AgentProfileOwner owner, string slug, AgentProfileDraftUpdateInput? input, string subject, string resourceUrl, CancellationToken ct, bool includeActorId = true, bool callerFacing = false) => await ExecuteAsync(async () =>
    {
        var current = await service.GetAsync(owner, slug, ct) ?? throw new AgentProfileNotFoundException("Agent Profile was not found.");
        var receipt = await service.UpdateDraftAsync(new(owner, slug, ToDraft(input?.Draft), ExpectedVersion(http, input?.ExpectedVersion, false, current.StrongETag), Idempotency(http, input?.IdempotencyKey), subject), ct);
        return Accepted(receipt, resourceUrl, includeActorId);
    }, callerFacing, ct);

    internal static async Task<IResult> PublishAsync(HttpContext http, AgentProfileApplicationService service, AgentProfileOwner owner, string slug, string subject, string token, string resourceUrl, CancellationToken ct, bool includeActorId = true, bool callerFacing = false) => await ExecuteAsync(async () =>
    {
        var input = await OptionalBodyAsync<AgentProfilePublishInput>(http, ct);
        var current = await service.GetAsync(owner, slug, ct) ?? throw new AgentProfileNotFoundException("Agent Profile was not found.");
        var receipt = await service.PublishAsync(new(owner, slug, ExpectedVersion(http, input?.ExpectedVersion, false, current.StrongETag), Idempotency(http, input?.IdempotencyKey), subject, token), ct);
        return Accepted(receipt, resourceUrl, includeActorId);
    }, callerFacing, ct);

    internal static async Task<IResult> ValidateAsync(AgentProfileApplicationService service, AgentProfileOwner owner, string slug, string token, CancellationToken ct, bool callerFacing = false) => await ExecuteAsync(async () =>
    {
        var value = await service.ValidateAsync(owner, slug, token, ct);
        return Results.Ok(new { isValid = value.IsValid, draftRevision = value.DraftRevision, diagnostics = value.Diagnostics.Select(static item => new { code = item.Code, field = item.Field, message = item.Message }) });
    }, callerFacing, ct);

    internal static async Task<IResult> GetBindingAsync(AgentProfileApplicationService service, AgentProfileOwner owner, string agentKind, CancellationToken ct, bool includeSystemRollout = true, bool includeOwnerKind = true) => await ExecuteAsync(async () =>
    {
        var binding = await service.GetBindingAsync(owner, agentKind, ct);
        return WithEtag(binding.StrongETag, Binding(binding, includeSystemRollout, includeOwnerKind));
    });

    internal static Task<IResult> GetBindingForCallerFacadeAsync(
        AgentProfileApplicationService service,
        AgentProfileOwner owner,
        string agentKind,
        CancellationToken ct) =>
        ExecuteCallerBindingFacadeAsync(async () =>
        {
            var binding = await service.GetBindingAsync(owner, agentKind, ct).ConfigureAwait(false);
            return WithEtag(
                binding.StrongETag,
                Binding(binding, includeSystemRollout: false, includeOwnerKind: false));
        }, ct);

    internal static async Task<IResult> SetBindingAsync(HttpContext http, AgentProfileApplicationService service, AgentProfileOwner owner, string agentKind, AgentProfileBindingInput? input, string subject, string resourceUrl, CancellationToken ct, bool includeActorId = true) => await ExecuteAsync(async () =>
    {
        var reference = input?.AgentProfile ?? throw new ArgumentException("agentProfile is required.");
        var current = await service.GetBindingAsync(owner, agentKind, ct);
        var receipt = await service.SetBindingAsync(new(owner, agentKind, new AgentProfileReference { OwnerKind = ReferenceOwner(reference.OwnerKind), ProfileSlug = Required(reference.ProfileSlug, "agentProfile.profileSlug") }, ExpectedVersion(http, input?.ExpectedVersion, true, current.StrongETag), Idempotency(http, input?.IdempotencyKey), subject, input?.Enabled ?? true, input?.CohortBasisPoints ?? AgentProfilePolicies.FullCohortBasisPoints), ct);
        return Accepted(receipt, resourceUrl, includeActorId);
    });

    internal static Task<IResult> SetBindingForCallerFacadeAsync(
        HttpContext http,
        AgentProfileApplicationService service,
        AgentProfileOwner owner,
        string agentKind,
        AgentProfileReferenceOwnerKind referenceSource,
        string? profileSlug,
        long? expectedVersion,
        string? idempotencyKey,
        string subject,
        string resourceUrl,
        CancellationToken ct) =>
        ExecuteCallerBindingFacadeAsync(async () =>
        {
            var normalizedSlug = Required(profileSlug, "agentProfile.profileSlug");
            var current = await service.GetBindingAsync(owner, agentKind, ct).ConfigureAwait(false);
            var receipt = await service.SetBindingAsync(
                new AgentProfileBindingUpdateRequest(
                    owner,
                    agentKind,
                    new AgentProfileReference
                    {
                        OwnerKind = referenceSource,
                        ProfileSlug = normalizedSlug,
                    },
                    ExpectedVersion(http, expectedVersion, true, current.StrongETag),
                    Idempotency(http, idempotencyKey),
                    subject),
                ct).ConfigureAwait(false);
            return Accepted(receipt, resourceUrl, includeActorId: false);
        }, ct);

    internal static async Task<IResult> ClearBindingAsync(HttpContext http, AgentProfileApplicationService service, AgentProfileOwner owner, string agentKind, string subject, string resourceUrl, CancellationToken ct, bool includeActorId = true) => await ExecuteAsync(async () =>
    {
        var input = await OptionalBodyAsync<AgentProfileBindingClearInput>(http, ct);
        var current = await service.GetBindingAsync(owner, agentKind, ct);
        var receipt = await service.ClearBindingAsync(new(owner, agentKind, ExpectedVersion(http, input?.ExpectedVersion, true, current.StrongETag), Idempotency(http, input?.IdempotencyKey), subject), ct);
        return Accepted(receipt, resourceUrl, includeActorId);
    });

    internal static Task<IResult> ClearBindingForCallerFacadeAsync(
        HttpContext http,
        AgentProfileApplicationService service,
        AgentProfileOwner owner,
        string agentKind,
        string subject,
        string resourceUrl,
        CancellationToken ct) =>
        ExecuteCallerBindingFacadeAsync(async () =>
        {
            var input = await OptionalBodyAsync<AgentProfileBindingClearInput>(http, ct).ConfigureAwait(false);
            var current = await service.GetBindingAsync(owner, agentKind, ct).ConfigureAwait(false);
            var receipt = await service.ClearBindingAsync(
                new AgentProfileBindingClearRequest(
                    owner,
                    agentKind,
                    ExpectedVersion(http, input?.ExpectedVersion, true, current.StrongETag),
                    Idempotency(http, input?.IdempotencyKey),
                    subject),
                ct).ConfigureAwait(false);
            return Accepted(receipt, resourceUrl, includeActorId: false);
        }, ct);

    private static IResult List(AgentProfileListPage page, AgentProfileOwner owner, bool includeMutation = true) =>
        includeMutation
            ? Results.Ok(new { items = page.Items.Select(entry => Summary(entry, owner)), nextCursor = page.NextCursor, authorityStateVersion = page.AuthorityStateVersion, updatedAt = page.UpdatedAt, lastMutation = Mutation(page.LastMutation) })
            : Results.Ok(new { items = page.Items.Select(entry => Summary(entry, owner)), nextCursor = page.NextCursor, authorityStateVersion = page.AuthorityStateVersion, updatedAt = page.UpdatedAt });
    private static object Summary(AgentProfileCatalogEntry entry, AgentProfileOwner? owner) => new { profileId = entry.ProfileId, profileSlug = entry.ProfileSlug, displayName = entry.DisplayName, purpose = entry.Purpose, publishedRevision = entry.PublishedRevision, available = entry.Status == AgentProfileProvisioningStatus.Active, ownerKind = OwnerKind(owner), status = Short(entry.Status) };
    private static object Detail(AgentProfileManagementDetail detail, bool includeOwnerKind) =>
        includeOwnerKind
            ? new { profileId = detail.Identity.ProfileId, profileSlug = detail.Identity.ProfileSlug, ownerKind = OwnerKind(detail.Identity.Owner), draft = detail.Snapshot.Draft is null ? null : Draft(detail.Snapshot.Draft), draftRevision = detail.Snapshot.DraftRevision, publishedRevision = detail.Snapshot.PublishedRevision, executionAvailable = detail.ExecutionAvailable, authorityStateVersion = detail.Snapshot.AuthorityStateVersion, etag = detail.StrongETag, updatedAt = detail.Snapshot.UpdatedAt, lastMutation = Mutation(detail.Snapshot.LastMutation) }
            : (object)new { profileId = detail.Identity.ProfileId, profileSlug = detail.Identity.ProfileSlug, draft = detail.Snapshot.Draft is null ? null : Draft(detail.Snapshot.Draft), draftRevision = detail.Snapshot.DraftRevision, publishedRevision = detail.Snapshot.PublishedRevision, executionAvailable = detail.ExecutionAvailable, authorityStateVersion = detail.Snapshot.AuthorityStateVersion, etag = detail.StrongETag, updatedAt = detail.Snapshot.UpdatedAt, lastMutation = Mutation(detail.Snapshot.LastMutation) };
    private static object Binding(AgentProfileBindingDetail detail, bool includeSystemRollout, bool includeOwnerKind) =>
        includeSystemRollout
            ? new { agentKind = detail.Binding?.AgentKind, target = BindingTarget(detail, includeOwnerKind), previousReviewedTarget = BindingTarget(detail.Binding?.System?.PreviousReviewedTarget, includeOwnerKind), enabled = detail.Binding?.System?.Enabled ?? false, cohortBasisPoints = detail.Binding?.System?.CohortBasisPoints ?? 0, authorityStateVersion = detail.AuthorityStateVersion, etag = detail.StrongETag, updatedAt = detail.UpdatedAt, lastMutation = Mutation(detail.LastMutation) }
            : (object)new { agentKind = detail.Binding?.AgentKind, target = BindingTarget(detail, includeOwnerKind), authorityStateVersion = detail.AuthorityStateVersion, etag = detail.StrongETag, updatedAt = detail.UpdatedAt, lastMutation = Mutation(detail.LastMutation) };
    private static object? BindingTarget(AgentProfileBindingDetail detail, bool includeOwnerKind) =>
        BindingTarget(detail.Binding?.Target, includeOwnerKind);
    private static object? BindingTarget(AgentProfileBindingTarget? target, bool includeOwnerKind) =>
        target is null
            ? null
            : includeOwnerKind
                ? new { profileId = target.ProfileId, publishedRevision = target.PublishedRevision, ownerKind = OwnerKind(target.Owner) }
                : (object)new { profileId = target.ProfileId, publishedRevision = target.PublishedRevision };
    private static object? Mutation(AgentProfileMutationOutcome? value) => value?.Operation is null ? null : new { operationId = value.Operation.OperationId, commandId = value.Operation.CommandId, correlationId = value.Operation.CorrelationId, status = Short(value.Status), code = value.Code, authorityStateVersion = value.AuthorityStateVersion, draftRevision = value.DraftRevision, publishedRevision = value.PublishedRevision };
    private static object Draft(AgentProfileDraft value) => new { displayName = value.DisplayName, purpose = value.Purpose, instructions = value.Instructions, runtimeProfile = Runtime(value.RuntimeProfile) };
    private static object Runtime(AgentProfileSnapshot value) => new { agentKind = value.AgentKind, routeToolSetRef = value.RouteToolSetRef, activationMode = Short(value.ActivationMode), maximumToolPolicy = Policy(value.MaximumToolPolicy), recoveryToolPolicy = Policy(value.RecoveryToolPolicy), maxPlanSteps = value.MaxPlanSteps, handoffTtlSeconds = value.HandoffTtlSeconds, classifierTimeoutMs = value.ClassifierTimeoutMs, exactSkillFetchTimeoutMs = value.ExactSkillFetchTimeoutMs, maxSelectedSkillBytes = value.MaxSelectedSkillBytes, maxOwnedToolCount = value.HasMaxOwnedToolCount ? value.MaxOwnedToolCount : (int?)null, maxSchemaBytes = value.HasMaxSchemaBytes ? value.MaxSchemaBytes : (int?)null, members = value.Members.Select(member => new { intentId = member.IntentId, routingDescription = member.RoutingDescription, skillRef = new { guid = member.SkillRef?.Guid, literalVersion = member.SkillRef?.LiteralVersion }, explicitTriggerAliases = member.ExplicitTriggerAliases, taskToolPolicy = Policy(member.TaskToolPolicy), sideEffectClass = Short(member.SideEffectClass), expectedSkillName = member.ExpectedSkillName, reviewedPublisherId = member.ReviewedPublisherId }) };
    private static object Policy(AgentProfileToolPolicy? value) => new
    {
        toolNames = value is null ? Array.Empty<string>() : value.ToolNames.ToArray(),
        toolSetRefs = value is null ? Array.Empty<string>() : value.ToolSetRefs.ToArray(),
        connectedServiceSelectors = value is null
            ? Array.Empty<object>()
            : value.ConnectedServiceSelectors.Select(static selector => new
            {
                catalogServiceSlug = selector.CatalogServiceSlug,
                endpointId = string.IsNullOrEmpty(selector.EndpointId) ? null : selector.EndpointId,
                allowedRisks = selector.AllowedRisks.Select(Risk).ToArray(),
                readiness = selector.Readiness is null
                    ? null
                    : new
                    {
                        requestedScopes = selector.Readiness.RequestedScopes.ToArray(),
                    },
            }).ToArray<object>(),
    };

    private static AgentProfileDraft ToDraft(AgentProfileDraftInput? input)
    {
        if (input is null || input.RuntimeProfile is null) throw new ArgumentException("draft.runtimeProfile is required.");
        var runtime = input.RuntimeProfile;
        var runtimeProfile = new AgentProfileSnapshot
        {
            AgentKind = Required(runtime.AgentKind, "draft.runtimeProfile.agentKind"),
            RouteToolSetRef = Required(runtime.RouteToolSetRef, "draft.runtimeProfile.routeToolSetRef"),
            ActivationMode = Activation(runtime.ActivationMode),
            MaximumToolPolicy = Policy(runtime.MaximumToolPolicy),
            RecoveryToolPolicy = Policy(runtime.RecoveryToolPolicy),
            MaxPlanSteps = runtime.MaxPlanSteps,
            HandoffTtlSeconds = runtime.HandoffTtlSeconds,
            ClassifierTimeoutMs = runtime.ClassifierTimeoutMs,
            ExactSkillFetchTimeoutMs = runtime.ExactSkillFetchTimeoutMs,
            MaxSelectedSkillBytes = runtime.MaxSelectedSkillBytes,
            Members = { runtime.Members?.Select(Member) ?? [] },
        };
        if (runtime.MaxOwnedToolCount is { } maxOwnedToolCount)
            runtimeProfile.MaxOwnedToolCount = maxOwnedToolCount;
        if (runtime.MaxSchemaBytes is { } maxSchemaBytes)
            runtimeProfile.MaxSchemaBytes = maxSchemaBytes;

        return new AgentProfileDraft
        {
            DisplayName = Required(input.DisplayName, "draft.displayName"),
            Purpose = input.Purpose?.Trim() ?? string.Empty,
            Instructions = Required(input.Instructions, "draft.instructions"),
            RuntimeProfile = runtimeProfile,
        };
    }
    private static AgentProfileSkillMember Member(AgentProfileSkillMemberInput input) => new() { IntentId = input.IntentId?.Trim() ?? string.Empty, RoutingDescription = input.RoutingDescription?.Trim() ?? string.Empty, SkillRef = new ExactRemoteSkillRef { Guid = input.SkillRef?.Guid?.Trim() ?? string.Empty, LiteralVersion = input.SkillRef?.LiteralVersion?.Trim() ?? string.Empty }, ExplicitTriggerAliases = { input.ExplicitTriggerAliases ?? [] }, TaskToolPolicy = Policy(input.TaskToolPolicy), SideEffectClass = SideEffect(input.SideEffectClass), ExpectedSkillName = input.ExpectedSkillName?.Trim() ?? string.Empty, ReviewedPublisherId = input.ReviewedPublisherId?.Trim() ?? string.Empty };
    private static AgentProfileToolPolicy Policy(AgentProfileToolPolicyInput? input) => new()
    {
        ToolNames = { input?.ToolNames ?? [] },
        ToolSetRefs = { input?.ToolSetRefs ?? [] },
        ConnectedServiceSelectors =
        {
            input?.ConnectedServiceSelectors?.Select(static selector => new AgentProfileConnectedServiceSelector
            {
                CatalogServiceSlug = selector.CatalogServiceSlug?.Trim() ?? string.Empty,
                EndpointId = selector.EndpointId?.Trim() ?? string.Empty,
                AllowedRisks = { selector.AllowedRisks?.Select(Risk) ?? [] },
                Readiness = selector.Readiness is null
                    ? null
                    : new AgentProfileConnectedServiceReadiness
                    {
                        RequestedScopes = { selector.Readiness.RequestedScopes ?? [] },
                    },
            }) ?? [],
        },
    };

    private static async Task<T?> OptionalBodyAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true) return null;
        try { return await http.Request.ReadFromJsonAsync<T>(cancellationToken: ct); }
        catch (JsonException ex) { throw new ArgumentException("Request body is invalid.", ex); }
        catch (InvalidOperationException ex) { throw new ArgumentException("Request body must use application/json.", ex); }
    }

    private static Task<IResult> ExecuteAsync(
        Func<Task<IResult>> action,
        bool callerFacing = false,
        CancellationToken ct = default) =>
        callerFacing
            ? ExecuteCallerProfileFacadeAsync(action, ct)
            : ExecuteCoreAsync(action);
    private static async Task<IResult> ExecuteCoreAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AgentProfileNotFoundException ex) { return Error(StatusCodes.Status404NotFound, "AGENT_PROFILE_NOT_FOUND", ex.Message); }
        catch (AgentProfileUnavailableException ex) { return Error(StatusCodes.Status503ServiceUnavailable, "AGENT_PROFILE_UNAVAILABLE", ex.Message); }
        catch (AgentProfileIntegrityException ex) { return Error(StatusCodes.Status503ServiceUnavailable, "AGENT_PROFILE_INTEGRITY_UNAVAILABLE", ex.Message); }
        catch (AgentProfileSealingException ex) { return Error(StatusCodes.Status422UnprocessableEntity, "AGENT_PROFILE_VALIDATION_FAILED", ex.Message, ex.Diagnostics.Select(item => new { code = item.Code, field = item.Field, message = item.Message })); }
        catch (AgentProfileInvalidCursorException ex) { return Error(StatusCodes.Status400BadRequest, "INVALID_CURSOR", ex.Message); }
        catch (ArgumentOutOfRangeException ex) { return Error(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message); }
        catch (ArgumentException ex) { return Error(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message); }
        catch (PreconditionRequiredException ex) { return Error(StatusCodes.Status428PreconditionRequired, "PRECONDITION_REQUIRED", ex.Message); }
        catch (PreconditionFailedException ex) { return Error(StatusCodes.Status412PreconditionFailed, "PRECONDITION_FAILED", ex.Message); }
    }

    private static async Task<IResult> ExecuteCallerBindingFacadeAsync(
        Func<Task<IResult>> action,
        CancellationToken ct)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentProfileNotFoundException)
        {
            return CallerError(StatusCodes.Status404NotFound, "AI_AGENT_NOT_FOUND", "The selected Agent was not found.");
        }
        catch (AgentProfileUnavailableException)
        {
            return CallerBindingUnavailable();
        }
        catch (AgentProfileIntegrityException)
        {
            return CallerBindingUnavailable();
        }
        catch (PreconditionRequiredException)
        {
            return CallerError(StatusCodes.Status428PreconditionRequired, "PRECONDITION_REQUIRED", "A current default Agent version is required.");
        }
        catch (PreconditionFailedException)
        {
            return CallerError(StatusCodes.Status412PreconditionFailed, "PRECONDITION_FAILED", "The default Agent changed; refresh and try again.");
        }
        catch (ArgumentOutOfRangeException)
        {
            return CallerBindingInvalid();
        }
        catch (ArgumentException)
        {
            return CallerBindingInvalid();
        }
        catch (InvalidOperationException)
        {
            return CallerBindingUnavailable();
        }
    }

    private static async Task<IResult> ExecuteCallerProfileFacadeAsync(
        Func<Task<IResult>> action,
        CancellationToken ct)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentProfileNotFoundException)
        {
            return CallerError(StatusCodes.Status404NotFound, "AI_AGENT_NOT_FOUND", "Agent was not found.");
        }
        catch (AgentProfileUnavailableException)
        {
            return CallerAgentUnavailable();
        }
        catch (AgentProfileIntegrityException)
        {
            return CallerAgentUnavailable();
        }
        catch (AgentProfileSealingException)
        {
            return CallerError(
                StatusCodes.Status422UnprocessableEntity,
                "AI_AGENT_VALIDATION_FAILED",
                "Agent validation failed.");
        }
        catch (AgentProfileInvalidCursorException)
        {
            return CallerAgentInvalid();
        }
        catch (ArgumentOutOfRangeException)
        {
            return CallerAgentInvalid();
        }
        catch (ArgumentException)
        {
            return CallerAgentInvalid();
        }
        catch (PreconditionRequiredException)
        {
            return CallerError(
                StatusCodes.Status428PreconditionRequired,
                "PRECONDITION_REQUIRED",
                "A current Agent version is required.");
        }
        catch (PreconditionFailedException)
        {
            return CallerError(
                StatusCodes.Status412PreconditionFailed,
                "PRECONDITION_FAILED",
                "The Agent changed; refresh and try again.");
        }
        catch (InvalidOperationException)
        {
            return CallerAgentUnavailable();
        }
    }

    private static IResult CallerAgentInvalid() =>
        CallerError(StatusCodes.Status400BadRequest, "AI_AGENT_INVALID", "Agent request is invalid.");

    private static IResult CallerAgentUnavailable() =>
        CallerError(StatusCodes.Status503ServiceUnavailable, "AI_AGENT_UNAVAILABLE", "Agent is temporarily unavailable.");

    private static IResult CallerBindingInvalid() =>
        CallerError(StatusCodes.Status400BadRequest, "AI_AGENT_DEFAULT_INVALID", "Default Agent request is invalid.");

    private static IResult CallerBindingUnavailable() =>
        CallerError(StatusCodes.Status503ServiceUnavailable, "AI_AGENT_UNAVAILABLE", "The selected Agent is temporarily unavailable.");

    private static IResult Accepted(AgentProfileAcceptedReceipt receipt, string resourceUrl, bool includeActorId) =>
        includeActorId
            ? Results.Accepted(resourceUrl, new { operationId = receipt.OperationId, profileId = receipt.ProfileId, commandId = receipt.CommandId, correlationId = receipt.CorrelationId, actorId = receipt.ActorId, acceptedAt = receipt.AcceptedAt, resourceUrl })
            : Results.Accepted(resourceUrl, new { operationId = receipt.OperationId, profileId = receipt.ProfileId, commandId = receipt.CommandId, correlationId = receipt.CorrelationId, acceptedAt = receipt.AcceptedAt, resourceUrl });
    private static IResult WithEtag(string etag, object value) => new EtagJsonResult(etag, value);
    private static IResult Error(int status, string code, string message, object? diagnostics = null) => Results.Json(new { code, message, diagnostics }, statusCode: status);
    private static IResult CallerError(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);
    private static int Take(int? take, int? pageSize) { var value = take ?? pageSize ?? AgentProfileApplicationService.MaximumPageSize; if (take.HasValue && pageSize.HasValue && take != pageSize) throw new ArgumentException("take and pageSize must agree when both are supplied."); return value; }
    private static AgentProfileOwner ScopeOwner(string scopeId) => AgentProfileOwners.ForScope(Required(scopeId, "scopeId"));
    private static string ScopeProfileUrl(string scopeId, string profileSlug) => $"/api/scopes/{Uri.EscapeDataString(scopeId)}/agent-profiles/{Uri.EscapeDataString(profileSlug)}";
    private static string ScopeBindingUrl(string scopeId, string agentKind) => $"/api/scopes/{Uri.EscapeDataString(scopeId)}/agent-profile-bindings/{Uri.EscapeDataString(agentKind)}";
    private static string AdminProfileUrl(string profileSlug) => $"/api/admin/agent-profiles/{Uri.EscapeDataString(profileSlug)}";
    private static string AdminBindingUrl(string agentKind) => $"/api/admin/agent-profile-bindings/{Uri.EscapeDataString(agentKind)}";
    private static string OwnerKind(AgentProfileOwner? owner) => owner?.OwnerCase == AgentProfileOwner.OwnerOneofCase.System ? "system" : "scope";
    private static string Short<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString().Replace("AgentProfileActivationMode", string.Empty, StringComparison.Ordinal).Replace("AgentProfileSideEffectClass", string.Empty, StringComparison.Ordinal).Replace("AgentProfileProvisioningStatus", string.Empty, StringComparison.Ordinal).Trim('_'));
    private static string BearerToken(HttpContext http) { var value = http.Request.Headers.Authorization.ToString(); return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : string.Empty; }
    private static string Idempotency(HttpContext http, string? bodyValue)
    {
        var header = http.Request.Headers[IdempotencyKeyHeader].ToString().Trim();
        if (bodyValue is not null && string.IsNullOrWhiteSpace(bodyValue))
            throw new ArgumentException("idempotencyKey body value must not be blank.");
        var body = bodyValue?.Trim() ?? string.Empty;
        if (header.Length > 0 && body.Length > 0 && !string.Equals(header, body, StringComparison.Ordinal))
            throw new ArgumentException($"{IdempotencyKeyHeader} header and idempotencyKey body values must agree.");
        if (header.Length == 0 && body.Length == 0)
            throw new ArgumentException($"{IdempotencyKeyHeader} header or idempotencyKey body value is required.");
        return header.Length > 0 ? header : body;
    }

    private static long ExpectedVersion(HttpContext http, long? bodyVersion, bool binding, string currentEtag)
    {
        var header = http.Request.Headers[IfMatchHeader].ToString().Trim();
        if (bodyVersion < 0) throw new ArgumentException("expectedVersion must be non-negative.");
        if (header.Length == 0 && bodyVersion is null)
            throw new PreconditionRequiredException($"{IfMatchHeader} header or expectedVersion body value is required.");

        var prefix = binding ? "\"agent-profile-binding-v" : "\"agent-profile-v";
        long? headerVersion = null;
        if (header.Length > 0)
        {
            if (!header.StartsWith(prefix, StringComparison.Ordinal) || !header.EndsWith('"') ||
                !long.TryParse(header[prefix.Length..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVersion) ||
                parsedVersion < 0)
                throw new ArgumentException($"{IfMatchHeader} is not a valid Agent Profile ETag.");
            headerVersion = parsedVersion;
        }

        if (headerVersion is not null && bodyVersion is not null && headerVersion != bodyVersion)
            throw new ArgumentException($"{IfMatchHeader} header and expectedVersion body values must agree.");

        var version = headerVersion ?? bodyVersion!.Value;
        var suppliedEtag = $"{prefix}{version.ToString(CultureInfo.InvariantCulture)}\"";
        if (!string.Equals(suppliedEtag, currentEtag, StringComparison.Ordinal))
            throw new PreconditionFailedException($"{IfMatchHeader} or expectedVersion does not match the current resource version.");
        return version;
    }
    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.") : value.Trim();
    private static AgentProfileReferenceOwnerKind ReferenceOwner(string? value) => value?.Trim().ToLowerInvariant() switch { "caller" => AgentProfileReferenceOwnerKind.Caller, "system" => AgentProfileReferenceOwnerKind.System, _ => throw new ArgumentException("agentProfile.ownerKind must be caller or system.") };
    private static AgentProfileActivationMode Activation(string? value) => value?.Trim().ToUpperInvariant() switch { "SHADOW" => AgentProfileActivationMode.Shadow, "ENFORCED" => AgentProfileActivationMode.Enforced, _ => throw new ArgumentException("activationMode is invalid.") };
    private static AgentProfileSideEffectClass SideEffect(string? value) => value?.Trim().ToUpperInvariant() switch { "READ_ONLY" => AgentProfileSideEffectClass.ReadOnly, "EXTERNAL_HANDOFF" => AgentProfileSideEffectClass.ExternalHandoff, "SERVICE_CALL" => AgentProfileSideEffectClass.ServiceCall, "MAINTENANCE" => AgentProfileSideEffectClass.Maintenance, _ => throw new ArgumentException("sideEffectClass is invalid.") };
    private static string Risk(AgentToolOperationRiskPayload value) => value switch
    {
        AgentToolOperationRiskPayload.ReadOnly => "READ_ONLY",
        AgentToolOperationRiskPayload.Write => "WRITE",
        AgentToolOperationRiskPayload.Destructive => "DESTRUCTIVE",
        _ => "UNSPECIFIED",
    };
    private static AgentToolOperationRiskPayload Risk(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "READ_ONLY" => AgentToolOperationRiskPayload.ReadOnly,
        "WRITE" => AgentToolOperationRiskPayload.Write,
        "DESTRUCTIVE" => AgentToolOperationRiskPayload.Destructive,
        "UNSPECIFIED" => AgentToolOperationRiskPayload.Unspecified,
        _ => throw new ArgumentException("connectedServiceSelectors.allowedRisks is invalid."),
    };
    private static void Audit(RouteHandlerBuilder builder, string operation, params string[] routes) => builder.WithEndpointAudit($"agent-profile.{operation}", AuditSensitivityLevel.Confidential, "agent-profile", routes.Length == 1 ? EndpointAuditTargetResolvers.FromRouteValue("agent-profile", routes[0]) : EndpointAuditTargetResolvers.FromRouteValues("agent-profile", routes));

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileCreateInput(string? ProfileSlug, string? IdempotencyKey);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileDraftUpdateInput(AgentProfileDraftInput? Draft, long? ExpectedVersion, string? IdempotencyKey);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfilePublishInput(long? ExpectedVersion, string? IdempotencyKey);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileBindingInput(AgentProfileReferenceInput? AgentProfile, bool? Enabled, int? CohortBasisPoints, long? ExpectedVersion, string? IdempotencyKey);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileBindingClearInput(long? ExpectedVersion, string? IdempotencyKey);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileReferenceInput(string? OwnerKind, string? ProfileSlug);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileDraftInput(string? DisplayName, string? Purpose, string? Instructions, AgentProfileRuntimeInput? RuntimeProfile);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileRuntimeInput(string? AgentKind, string? RouteToolSetRef, string? ActivationMode, AgentProfileToolPolicyInput? MaximumToolPolicy, AgentProfileToolPolicyInput? RecoveryToolPolicy, int MaxPlanSteps, int HandoffTtlSeconds, int ClassifierTimeoutMs, int ExactSkillFetchTimeoutMs, int MaxSelectedSkillBytes, IReadOnlyList<AgentProfileSkillMemberInput>? Members, int? MaxOwnedToolCount = null, int? MaxSchemaBytes = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileToolPolicyInput(
        IReadOnlyList<string>? ToolNames,
        IReadOnlyList<string>? ToolSetRefs,
        IReadOnlyList<AgentProfileConnectedServiceSelectorInput>? ConnectedServiceSelectors);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileConnectedServiceSelectorInput(
        string? CatalogServiceSlug,
        IReadOnlyList<string>? AllowedRisks,
        AgentProfileConnectedServiceReadinessInput? Readiness,
        string? EndpointId = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileConnectedServiceReadinessInput(
        IReadOnlyList<string>? RequestedScopes);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AgentProfileSkillMemberInput(string? IntentId, string? RoutingDescription, ExactRemoteSkillRefInput? SkillRef, IReadOnlyList<string>? ExplicitTriggerAliases, AgentProfileToolPolicyInput? TaskToolPolicy, string? SideEffectClass, string? ExpectedSkillName, string? ReviewedPublisherId);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record ExactRemoteSkillRefInput(string? Guid, string? LiteralVersion);
    private sealed class PreconditionRequiredException(string message) : InvalidOperationException(message);
    private sealed class PreconditionFailedException(string message) : InvalidOperationException(message);
    private sealed class EtagJsonResult(string etag, object value) : IResult { public Task ExecuteAsync(HttpContext context) { context.Response.Headers.ETag = etag; return context.Response.WriteAsJsonAsync(value); } }

    private static bool TryAuditSubject(HttpContext http, out string subject)
    {
        subject = http.User.FindFirst("uid")?.Value?.Trim()
                  ?? http.User.FindFirst("sub")?.Value?.Trim()
                  ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value?.Trim()
                  ?? http.User.FindFirst("user_id")?.Value?.Trim()
                  ?? string.Empty;
        return subject.Length > 0;
    }
}
