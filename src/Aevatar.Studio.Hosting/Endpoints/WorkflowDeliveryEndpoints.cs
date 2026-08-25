using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

internal static class WorkflowDeliveryEndpoints
{
    private const string NyxIdPlatform = "nyxid";
    private const string NyxIdProxyScope = "proxy";

    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var delivery = app.MapGroup("/api/delivery")
            .WithTags("WorkflowDelivery")
            .RequireAuthorization();
        delivery.MapGet("/session", GetSessionAsync);
        delivery.MapGet("/packages", ListPackagesAsync);
        delivery.MapPost("/packages:parse", ParsePackageAsync);
        delivery.MapPost("/packages", ParsePackageAsync);
        delivery.MapGet("/requests", ListRequestsAsync);
        delivery.MapPost("/requests", CreateRequestAsync);
        delivery.MapGet("/requests/{deliveryId}", GetRequestAsync);
        delivery.MapPost("/requests/{deliveryId}:validate-access", ValidateAccessAsync);
        delivery.MapPost("/requests/{deliveryId}:revoke", RevokeAsync);

        var scoped = app.MapGroup("/api/scopes/{scopeId}")
            .WithTags("WorkflowDelivery")
            .RequireAuthorization();
        scoped.MapPost(
            "/delivery-requests/{deliveryId}/connections/{slotKey}:connect",
            CreateConnectLinkAsync);
        scoped.MapGet(
            "/delivery-requests/{deliveryId}/connections/{slotKey}/available",
            ListExistingConnectionsAsync);
        scoped.MapPost(
            "/delivery-requests/{deliveryId}/connections/{slotKey}:attach",
            AttachExistingConnectionAsync);
        scoped.MapGet(
            "/delivery-requests/{deliveryId}/connections/{slotKey}",
            GetConnectStatusAsync);
        scoped.MapPost(
            "/delivery-requests/{deliveryId}/connections/{slotKey}:refresh",
            RefreshConnectStatusAsync);
        scoped.MapPost(
            "/delivery-requests/{deliveryId}:validate-config",
            ValidateConfigurationAsync);
        scoped.MapPost(
            "/delivery-requests/{deliveryId}:publish",
            PublishAsync);
        scoped.MapGet(
            "/installations/{installationId}",
            GetInstallationAsync);
        scoped.MapPost(
            "/installations/{installationId}:retry",
            RetryAsync);
    }

    internal static async Task<IResult> GetSessionAsync(
        HttpContext http,
        [FromServices] IPlatformAdminAuthorizer? adminAuthorizer,
        CancellationToken ct)
    {
        if (!TryResolveSubject(http.User, out var userId))
            return Error(StatusCodes.Status403Forbidden, "DELIVERY_SUBJECT_REQUIRED", "Authenticated NyxID subject is required.");

        var scopeId = AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var resolvedScopeId)
            ? resolvedScopeId
            : string.Empty;
        var admin = await ResolveAdminAsync(http, adminAuthorizer, requireAdmin: false, ct);
        var isAdmin = admin.Caller is not null;
        return Results.Ok(new WorkflowDeliverySessionResponse(
            Authenticated: true,
            ScopeId: scopeId,
            ScopeKind: "personal",
            IsAdmin: isAdmin,
            CanCreateDeliveries: isAdmin,
            Viewer: new WorkflowDeliveryViewerResponse(
                userId,
                ResolveDisplayName(http.User),
                ResolveEmail(http.User))));
    }

    internal static async Task<IResult> ListPackagesAsync(
        HttpContext http,
        [FromServices] IWorkflowDeliveryService service,
        [FromServices] IPlatformAdminAuthorizer? adminAuthorizer,
        CancellationToken ct)
    {
        var admin = await ResolveAdminAsync(http, adminAuthorizer, requireAdmin: true, ct);
        if (admin.Error is not null)
            return admin.Error;
        return await ExecuteAsync(() => service.ListPackagesAsync(admin.Caller!.UserId, ct));
    }

    internal static async Task<IResult> ParsePackageAsync(
        HttpContext http,
        WorkflowDeliveryPackageRequest? request,
        [FromServices] IWorkflowDeliveryService service,
        [FromServices] IPlatformAdminAuthorizer? adminAuthorizer,
        CancellationToken ct)
    {
        var admin = await ResolveAdminAsync(http, adminAuthorizer, requireAdmin: true, ct);
        if (admin.Error is not null)
            return admin.Error;
        if (request is null)
            return Error(StatusCodes.Status422UnprocessableEntity, "INVALID_DELIVERY_REQUEST", "Request body is required.");
        return await ExecuteAsync(() => service.GetPackageAsync(request.WorkflowName, admin.Caller!.UserId, ct));
    }

    internal static async Task<IResult> ListRequestsAsync(
        HttpContext http,
        [FromServices] IWorkflowDeliveryService service,
        [FromServices] IPlatformAdminAuthorizer? adminAuthorizer,
        CancellationToken ct,
        int? pageSize = null,
        string? pageToken = null)
    {
        var page = new WorkflowDeliveryPageRequest(pageSize, pageToken);
        var admin = await ResolveAdminAsync(http, adminAuthorizer, requireAdmin: false, ct);
        if (admin.Caller is not null)
            return await ExecuteAsync(() => service.ListAdminAsync(page, ct));
        if (!TryResolveCallerScope(http, out var scopeId, out var denied))
            return denied;
        return await ExecuteAsync(() => service.ListCustomerAsync(scopeId, page, ct));
    }

    internal static async Task<IResult> CreateRequestAsync(
        HttpContext http,
        WorkflowDeliveryCreateRequest? request,
        [FromServices] IWorkflowDeliveryService service,
        [FromServices] IPlatformAdminAuthorizer? adminAuthorizer,
        CancellationToken ct)
    {
        var admin = await ResolveAdminAsync(http, adminAuthorizer, requireAdmin: true, ct);
        if (admin.Error is not null)
            return admin.Error;
        if (request is null)
            return Error(StatusCodes.Status422UnprocessableEntity, "INVALID_DELIVERY_REQUEST", "Request body is required.");
        return await ExecuteAsync(
            async () =>
            {
                var response = await service.CreateAsync(admin.Caller!.UserId, request, ct);
                return Results.Accepted(response.CustomerUrl, response);
            });
    }

    internal static async Task<IResult> GetRequestAsync(
        HttpContext http,
        string deliveryId,
        [FromServices] IWorkflowDeliveryService service,
        [FromServices] IPlatformAdminAuthorizer? adminAuthorizer,
        CancellationToken ct)
    {
        var admin = await ResolveAdminAsync(http, adminAuthorizer, requireAdmin: false, ct);
        if (admin.Caller is not null)
        {
            return await ExecuteAsync(async () =>
                (IResult)(await service.GetAdminAsync(deliveryId, ct) is { } found
                    ? Results.Ok(found)
                    : NotFound()));
        }

        if (!TryResolveCallerScope(http, out var scopeId, out var denied))
            return denied;
        return await ExecuteAsync(async () =>
            (IResult)(await service.GetCustomerAsync(deliveryId, scopeId, ct) is { } found
                ? Results.Ok(found)
                : NotFound()));
    }

    internal static async Task<IResult> ValidateAccessAsync(
        HttpContext http,
        string deliveryId,
        [FromServices] IWorkflowDeliveryService service,
        CancellationToken ct)
    {
        if (!TryResolveCallerScope(http, out var scopeId, out var denied))
            return denied;
        return await ExecuteAsync(() => service.ValidateAccessAsync(deliveryId, scopeId, ct));
    }

    internal static async Task<IResult> RevokeAsync(
        HttpContext http,
        string deliveryId,
        [FromServices] IWorkflowDeliveryService service,
        [FromServices] IPlatformAdminAuthorizer? adminAuthorizer,
        CancellationToken ct)
    {
        var admin = await ResolveAdminAsync(http, adminAuthorizer, requireAdmin: true, ct);
        if (admin.Error is not null)
            return admin.Error;
        return await ExecuteAsync(async () =>
        {
            await service.RevokeAsync(deliveryId, admin.Caller!.UserId, ct);
            return Results.Accepted($"/api/delivery/requests/{Uri.EscapeDataString(deliveryId)}", new
            {
                deliveryId,
                status = "revocation_accepted",
            });
        });
    }

    internal static async Task<IResult> CreateConnectLinkAsync(
        HttpContext http,
        string scopeId,
        string deliveryId,
        string slotKey,
        [FromServices] IWorkflowDeliveryService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryGetBearer(http, out var bearerToken))
            return Results.Unauthorized();
        return await ExecuteAsync(async () =>
        {
            var response = await service.CreateConnectLinkAsync(
                deliveryId,
                scopeId,
                slotKey,
                bearerToken,
                ct);
            return Results.Accepted(response.StatusUrl, response);
        });
    }

    internal static async Task<IResult> GetConnectStatusAsync(
        HttpContext http,
        string scopeId,
        string deliveryId,
        string slotKey,
        [FromServices] IWorkflowDeliveryService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        return await ExecuteAsync(() => service.GetConnectStatusAsync(
            deliveryId,
            scopeId,
            slotKey,
            ct));
    }

    internal static async Task<IResult> ListExistingConnectionsAsync(
        HttpContext http,
        string scopeId,
        string deliveryId,
        string slotKey,
        [FromServices] IWorkflowDeliveryService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryGetBearer(http, out var bearerToken))
            return Results.Unauthorized();
        return await ExecuteAsync(() => service.ListExistingConnectionsAsync(
            deliveryId,
            scopeId,
            slotKey,
            bearerToken,
            ct));
    }

    internal static async Task<IResult> AttachExistingConnectionAsync(
        HttpContext http,
        string scopeId,
        string deliveryId,
        string slotKey,
        WorkflowDeliveryAttachConnectionRequest? request,
        [FromServices] IWorkflowDeliveryService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryGetBearer(http, out var bearerToken))
            return Results.Unauthorized();
        if (request is null)
            return Error(StatusCodes.Status422UnprocessableEntity, "INVALID_DELIVERY_REQUEST", "Request body is required.");
        return await ExecuteAsync(() => service.AttachExistingConnectionAsync(
            deliveryId,
            scopeId,
            slotKey,
            request,
            bearerToken,
            ct));
    }

    internal static async Task<IResult> RefreshConnectStatusAsync(
        HttpContext http,
        string scopeId,
        string deliveryId,
        string slotKey,
        [FromServices] IWorkflowDeliveryService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryGetBearer(http, out var bearerToken))
            return Results.Unauthorized();
        return await ExecuteAsync(async () =>
        {
            var response = await service.RefreshConnectStatusAsync(
                deliveryId,
                scopeId,
                slotKey,
                bearerToken,
                ct);
            return Results.Accepted(response.StatusUrl, response);
        });
    }

    internal static async Task<IResult> ValidateConfigurationAsync(
        HttpContext http,
        string scopeId,
        string deliveryId,
        WorkflowDeliveryValidateConfigurationRequest? request,
        [FromServices] IWorkflowDeliveryService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (request is null)
            return Error(StatusCodes.Status422UnprocessableEntity, "INVALID_DELIVERY_REQUEST", "Request body is required.");
        return await ExecuteAsync(async () =>
        {
            var admission = await StudioWorkflowCapabilityAdmissionHttpContext.CreateAsync(
                http,
                ResolveExecutionMode(request.TriggerIntent),
                ct: ct);
            return await service.ValidateConfigurationAsync(deliveryId, scopeId, request, admission, ct);
        });
    }

    internal static async Task<IResult> PublishAsync(
        HttpContext http,
        string scopeId,
        string deliveryId,
        WorkflowDeliveryPublishRequest? request,
        [FromServices] IWorkflowDeliveryService service,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (request is null)
            return Error(StatusCodes.Status422UnprocessableEntity, "INVALID_DELIVERY_REQUEST", "Request body is required.");
        if (!TryResolveSubject(http.User, out var userId))
            return Error(StatusCodes.Status403Forbidden, "DELIVERY_SUBJECT_REQUIRED", "Authenticated NyxID subject is required.");

        return await ExecuteAsync(async () =>
        {
            var executionMode = ResolveExecutionMode(request.TriggerIntent);
            var confirmationInputs = request.Confirmations?.Select(static item =>
                new NyxIdExplicitRequestConfirmationInput(
                    item.CallSiteId,
                    item.RequestContractDigest,
                    item.AttestedRisk));
            var admission = await StudioWorkflowCapabilityAdmissionHttpContext.CreateAsync(
                http,
                executionMode,
                confirmationInputs,
                ct);
            var authority = await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                http,
                bindingQuery,
                ct);
            var caller = new WorkflowDeliveryCallerContext(
                new ProvisionWorkflowCallerCredential(NyxIdPlatform, userId, NyxIdProxyScope),
                admission,
                authority.AuthenticatedOwner,
                authority.ProvisioningBearerToken);
            var response = await service.PublishAsync(deliveryId, scopeId, request, caller, ct);
            return Results.Accepted(response.StatusUrl, response);
        });
    }

    internal static async Task<IResult> GetInstallationAsync(
        HttpContext http,
        string scopeId,
        string installationId,
        [FromServices] IWorkflowDeliveryService service,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        return await ExecuteAsync(async () =>
            (IResult)(await service.GetInstallationAsync(scopeId, installationId, ct) is { } found
                ? Results.Ok(found)
                : InstallationNotFound()));
    }

    internal static async Task<IResult> RetryAsync(
        HttpContext http,
        string scopeId,
        string installationId,
        [FromServices] IWorkflowDeliveryService service,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryResolveSubject(http.User, out var userId))
            return Error(StatusCodes.Status403Forbidden, "DELIVERY_SUBJECT_REQUIRED", "Authenticated NyxID subject is required.");

        return await ExecuteAsync(async () =>
        {
            var current = await service.GetInstallationAsync(scopeId, installationId, ct);
            if (current is null)
                return InstallationNotFound();
            var executionMode = ResolveExecutionMode(current.TriggerKind);
            var admission = await StudioWorkflowCapabilityAdmissionHttpContext.CreateAsync(http, executionMode, ct: ct);
            var authority = executionMode == ExternalCapabilityExecutionMode.Durable
                ? await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(http, bindingQuery, ct)
                : null;
            var caller = new WorkflowDeliveryCallerContext(
                new ProvisionWorkflowCallerCredential(NyxIdPlatform, userId, NyxIdProxyScope),
                admission,
                authority?.AuthenticatedOwner,
                authority?.ProvisioningBearerToken);
            var response = await service.RetryAsync(scopeId, installationId, caller, ct);
            return Results.Accepted(response.StatusUrl, response);
        });
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var response = await action();
            return response is IResult result ? result : Results.Ok(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (WorkflowDeliveryHttpErrorMapper.TryMap(ex, out var result))
        {
            return result;
        }
    }

    private static async Task<AdminAuthorization> ResolveAdminAsync(
        HttpContext http,
        IPlatformAdminAuthorizer? authorizer,
        bool requireAdmin,
        CancellationToken ct)
    {
        if (authorizer is null)
        {
            return requireAdmin
                ? new(null, Error(StatusCodes.Status503ServiceUnavailable, "DELIVERY_ADMIN_AUTHORIZATION_UNAVAILABLE", "Admin authorization is unavailable."))
                : new(null, null);
        }
        if (!TryGetBearer(http, out var bearerToken))
            return requireAdmin ? new(null, Results.Unauthorized()) : new(null, null);

        PlatformCaller caller;
        try
        {
            caller = await authorizer.ResolveCallerAsync(bearerToken, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return requireAdmin ? new(null, Results.Forbid()) : new(null, null);
        }

        var allowed = caller.IsElevated &&
            !string.IsNullOrWhiteSpace(caller.UserId) &&
            string.Equals(caller.GrantSource, PlatformAdminGrantSources.AllowedUserId, StringComparison.Ordinal);
        return allowed
            ? new(caller, null)
            : requireAdmin
                ? new(null, Results.Forbid())
                : new(null, null);
    }

    private static bool TryResolveCallerScope(HttpContext http, out string scopeId, out IResult denied)
    {
        if (AevatarScopeAccessGuard.TryGetCallerScopeId(http, out scopeId))
        {
            denied = Results.Empty;
            return true;
        }
        denied = Error(
            StatusCodes.Status403Forbidden,
            "SCOPE_ACCESS_DENIED",
            "Authenticated scope is missing or ambiguous.");
        return false;
    }

    private static bool TryResolveSubject(ClaimsPrincipal? principal, out string subject)
    {
        subject = string.Empty;
        foreach (var claimType in new[] { "sub", ClaimTypes.NameIdentifier })
        {
            var values = principal?.Claims
                .Where(claim => string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
                .Select(claim => claim.Value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            if (values.Length == 1)
            {
                subject = values[0]!;
                return true;
            }
            if (values.Length > 1)
                return false;
        }
        return false;
    }

    private static string ResolveDisplayName(ClaimsPrincipal? principal) =>
        NormalizeOptional(principal?.FindFirst("name")?.Value) ??
        NormalizeOptional(principal?.Identity?.Name) ??
        ResolveEmail(principal) ??
        string.Empty;

    private static string? ResolveEmail(ClaimsPrincipal? principal) =>
        NormalizeOptional(principal?.FindFirst("email")?.Value) ??
        NormalizeOptional(principal?.FindFirst(ClaimTypes.Email)?.Value);

    private static bool TryGetBearer(HttpContext http, out string bearerToken)
    {
        bearerToken = string.Empty;
        var authorization = http.Request.Headers.Authorization.FirstOrDefault()?.Trim();
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = authorization[prefix.Length..].Trim();
        if (candidate.Length == 0 || candidate.Contains(','))
            return false;
        bearerToken = candidate;
        return true;
    }

    private static ExternalCapabilityExecutionMode ResolveExecutionMode(WorkflowDeliveryTriggerRequest? trigger) =>
        ResolveExecutionMode(trigger?.Kind);

    private static ExternalCapabilityExecutionMode ResolveExecutionMode(string? triggerKind) =>
        string.Equals(triggerKind?.Trim(), "cron", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(triggerKind?.Trim(), "one_shot", StringComparison.OrdinalIgnoreCase)
            ? ExternalCapabilityExecutionMode.Durable
            : ExternalCapabilityExecutionMode.Interactive;

    private static IResult NotFound() =>
        Error(StatusCodes.Status404NotFound, "DELIVERY_NOT_FOUND", "Workflow delivery was not found.");

    private static IResult InstallationNotFound() =>
        Error(StatusCodes.Status404NotFound, "INSTALLATION_NOT_FOUND", "Workflow installation was not found.");

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { code, message }, statusCode: statusCode);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AdminAuthorization(PlatformCaller? Caller, IResult? Error);
}

internal sealed record WorkflowDeliverySessionResponse(
    bool Authenticated,
    string ScopeId,
    string ScopeKind,
    bool IsAdmin,
    bool CanCreateDeliveries,
    WorkflowDeliveryViewerResponse Viewer);

internal sealed record WorkflowDeliveryViewerResponse(
    string UserId,
    string DisplayName,
    string? Email);

internal static class WorkflowDeliveryHttpErrorMapper
{
    public static bool TryMap(Exception exception, out IResult result)
    {
        result = exception switch
        {
            WorkflowDeliveryException delivery => MapDeliveryError(delivery),
            WorkflowDeliveryConfigurationException configuration =>
                Error(StatusCodes.Status422UnprocessableEntity, configuration.Code, configuration.Message),
            WorkflowDeliveryPackageNotAllowedException =>
                Error(StatusCodes.Status404NotFound, "DELIVERY_PACKAGE_NOT_ALLOWED", "Workflow package was not found in the delivery allowlist."),
            WorkflowDeliveryPackageUnavailableException unavailable =>
                Error(StatusCodes.Status503ServiceUnavailable, "DELIVERY_PACKAGE_UNAVAILABLE", unavailable.Message),
            NyxIdConnectLinkException connectLink => MapConnectLinkError(connectLink),
            NyxIdUserServiceInventoryException inventory => MapUserServiceInventoryError(inventory),
            // Delivery login finalization commits this binding for every trigger intent.
            // A miss here means that finalization or its projection is not ready yet.
            StudioMemberAutomationAuthorizationBindingRequiredException =>
                Error(
                    StatusCodes.Status409Conflict,
                    "DELIVERY_AUTHORIZATION_BINDING_REQUIRED",
                    "Retry publishing shortly. If the binding remains unavailable, sign out and sign back in to Delivery Center with the same NyxID account."),
            StudioTeamNotFoundException missingTeam =>
                Error(StatusCodes.Status404NotFound, "STUDIO_TEAM_NOT_FOUND", missingTeam.Message),
            StudioMemberAutomationProjectionPendingException pending =>
                Results.Json(new
                {
                    code = "DELIVERY_AUTHORIZATION_PROJECTION_PENDING",
                    message = "The refreshed authorization catalog is still being projected. Retry this request.",
                    retryable = true,
                    requiredStateVersion = pending.RequiredStateVersion,
                }, statusCode: StatusCodes.Status503ServiceUnavailable),
            StudioMemberAutomationCatalogRefreshSupersededException =>
                RetryableUnavailable("DELIVERY_AUTHORIZATION_REFRESH_SUPERSEDED", "A newer authorization catalog refresh superseded this request."),
            StudioMemberAutomationCatalogRefreshUnavailableException =>
                RetryableUnavailable("DELIVERY_AUTHORIZATION_REFRESH_UNAVAILABLE", "The authorization catalog could not be refreshed."),
            StudioMemberAutomationCatalogRouteUnresolvedException unresolved =>
                Results.Json(new
                {
                    code = "DELIVERY_AUTHORIZATION_ROUTE_UNRESOLVED",
                    message = unresolved.Message,
                    retryable = false,
                    requiredUserServiceIds = unresolved.RequiredUserServiceIds,
                }, statusCode: StatusCodes.Status409Conflict),
            StudioMemberAutomationPlanConflictException conflict =>
                Error(StatusCodes.Status409Conflict, "DELIVERY_AUTHORIZATION_PLAN_CONFLICT", conflict.Message),
            WorkflowExternalCapabilityAdmissionException admission =>
                Error(StatusCodes.Status422UnprocessableEntity, admission.StableCode, admission.SafeMessage),
            WorkflowCallerCredentialSelectionException =>
                Error(StatusCodes.Status401Unauthorized, WorkflowCallerCredentialSelectionException.ErrorCode, WorkflowCallerCredentialSelectionException.SafeMessage),
            NyxIdExplicitRequestConfirmationInputException confirmation =>
                Error(StatusCodes.Status422UnprocessableEntity, NyxIdExplicitRequestConfirmationInputException.ErrorCode, confirmation.Message),
            UnauthorizedAccessException unauthorized =>
                Error(StatusCodes.Status403Forbidden, "DELIVERY_ACCESS_DENIED", unauthorized.Message),
            ArgumentException argument =>
                Error(StatusCodes.Status422UnprocessableEntity, "INVALID_DELIVERY_REQUEST", argument.Message),
            InvalidOperationException invalid =>
                Error(StatusCodes.Status409Conflict, "DELIVERY_CONFLICT", invalid.Message),
            _ => null!,
        };
        return result is not null;
    }

    private static IResult MapConnectLinkError(NyxIdConnectLinkException exception) => exception.Kind switch
    {
        NyxIdConnectLinkFailureKind.AuthenticationRejected =>
            Error(StatusCodes.Status401Unauthorized, "NYXID_CONNECT_AUTHENTICATION_REJECTED", exception.Message),
        NyxIdConnectLinkFailureKind.Forbidden =>
            Error(StatusCodes.Status403Forbidden, "NYXID_CONNECT_FORBIDDEN", exception.Message),
        NyxIdConnectLinkFailureKind.NotFound =>
            Error(StatusCodes.Status404NotFound, "NYXID_CONNECT_LINK_NOT_FOUND", exception.Message),
        NyxIdConnectLinkFailureKind.RateLimited =>
            Error(StatusCodes.Status429TooManyRequests, "NYXID_CONNECT_RATE_LIMITED", exception.Message),
        NyxIdConnectLinkFailureKind.ResponseInvalid or NyxIdConnectLinkFailureKind.ResponseTooLarge =>
            Error(StatusCodes.Status502BadGateway, "NYXID_CONNECT_RESPONSE_INVALID", exception.Message),
        _ => Error(StatusCodes.Status503ServiceUnavailable, "NYXID_CONNECT_UNAVAILABLE", exception.Message),
    };

    private static IResult MapUserServiceInventoryError(
        NyxIdUserServiceInventoryException exception) => exception.Kind switch
    {
        NyxIdUserServiceInventoryFailureKind.AuthenticationRejected =>
            Error(StatusCodes.Status401Unauthorized, "NYXID_CONNECTION_INVENTORY_AUTHENTICATION_REJECTED", exception.Message),
        NyxIdUserServiceInventoryFailureKind.Forbidden =>
            Error(StatusCodes.Status403Forbidden, "NYXID_CONNECTION_INVENTORY_FORBIDDEN", exception.Message),
        NyxIdUserServiceInventoryFailureKind.RateLimited =>
            Error(StatusCodes.Status429TooManyRequests, "NYXID_CONNECTION_INVENTORY_RATE_LIMITED", exception.Message),
        NyxIdUserServiceInventoryFailureKind.ResponseInvalid =>
            Error(StatusCodes.Status502BadGateway, "NYXID_CONNECTION_INVENTORY_RESPONSE_INVALID", exception.Message),
        _ => Error(StatusCodes.Status503ServiceUnavailable, "NYXID_CONNECTION_INVENTORY_UNAVAILABLE", exception.Message),
    };

    private static IResult RetryableUnavailable(string code, string message) =>
        Results.Json(new { code, message, retryable = true }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult MapDeliveryError(WorkflowDeliveryException exception) => exception.Code switch
    {
        "DELIVERY_NOT_FOUND" or "INSTALLATION_NOT_FOUND" or "CONNECTION_SLOT_NOT_FOUND" or "CONNECTION_NOT_STARTED" =>
            Error(StatusCodes.Status404NotFound, exception.Code, exception.Message),
        "DELIVERY_REVOKED" or "DELIVERY_EXPIRED" =>
            Error(StatusCodes.Status410Gone, exception.Code, exception.Message),
        "PACKAGE_VERSION_CHANGED" or "CONFIRMATION_DIGEST_MISMATCH" or "CONNECTION_CONTRACT_MISMATCH" or
        "CONNECTIONS_LOCKED" or "CONNECTION_ALREADY_PENDING" or "CONNECTION_CHANGED" or
        "EXISTING_CONNECTION_NOT_AVAILABLE" or "DELIVERY_CONFLICT" =>
            Error(StatusCodes.Status409Conflict, exception.Code, exception.Message),
        "PROVISIONING_UNAUTHORIZED" =>
            Error(StatusCodes.Status401Unauthorized, exception.Code, exception.Message),
        "PROVISIONING_FAILED" =>
            Error(StatusCodes.Status503ServiceUnavailable, exception.Code, "Workflow provisioning is temporarily unavailable."),
        "CONNECTION_OBSERVATION_TIMEOUT" or "INSTALLATION_OBSERVATION_TIMEOUT" =>
            RetryableUnavailable(exception.Code, exception.Message),
        _ => Error(StatusCodes.Status422UnprocessableEntity, exception.Code, exception.Message),
    };

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { code, message }, statusCode: statusCode);
}
