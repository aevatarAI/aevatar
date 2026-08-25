using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using Aevatar.Mainnet.Host.Api.AI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.ModelCatalog;

internal static class LLMModelCatalogEndpoints
{
    public static IEndpointRouteBuilder MapLLMModelCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var scopes = app.MapGroup("/api/scopes/{scopeId}/llm-model-catalog")
            .WithTags("LLMModelCatalog");
        scopes.MapGet("", GetScopeAsync);
        scopes.MapPut("", PutScopeAsync);
        scopes.MapDelete("", ResetScopeAsync);
        scopes.MapGet("/candidates", GetScopeCandidatesAsync);
        scopes.MapGet("/candidates/{userServiceId}/models", GetScopeCandidateModelsAsync);

        var admin = app.MapGroup("/api/admin/llm-model-catalog")
            .WithTags("LLMModelCatalogAdmin");
        admin.MapGet("", GetPlatformAsync);
        admin.MapPut("", PutPlatformAsync);
        admin.MapGet("/candidates", GetPlatformCandidatesAsync);
        admin.MapGet("/candidates/{catalogServiceId}/models", GetPlatformCandidateModelsAsync);
        return app;
    }

    internal static async Task<IResult> GetScopeAsync(
        HttpContext http,
        string scopeId,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        await GetScopeCoreAsync(
            http,
            scopeId,
            service,
            includeAuthorizationOwner: true,
            callerFacing: false,
            ct);

    internal static async Task<IResult> GetScopeForCallerFacadeAsync(
        HttpContext http,
        string scopeId,
        ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        await GetScopeCoreAsync(
            http,
            scopeId,
            service,
            includeAuthorizationOwner: false,
            callerFacing: true,
            ct);

    private static async Task<IResult> GetScopeCoreAsync(
        HttpContext http,
        string scopeId,
        ILLMModelCatalogPolicyApplicationService service,
        bool includeAuthorizationOwner,
        bool callerFacing,
        CancellationToken ct)
    {
        if (TryCreateAccessDeniedResult(http, scopeId, callerFacing, out var denied))
            return denied;

        try
        {
            var view = await service.GetScopeAsync(scopeId, ct).ConfigureAwait(false);
            return Results.Json(includeAuthorizationOwner
                ? ToWireView(view)
                : ToCallerWireView(view));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return callerFacing ? ToCallerError(ex) : ToError(ex);
        }
    }

    internal static async Task<IResult> PutScopeAsync(
        HttpContext http,
        string scopeId,
        [FromBody] ModelCatalogReplaceRequest? request,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        await PutScopeCoreAsync(
            http,
            scopeId,
            request,
            service,
            includeActorId: true,
            callerFacing: false,
            ct);

    internal static async Task<IResult> PutScopeForCallerFacadeAsync(
        HttpContext http,
        string scopeId,
        ModelCatalogReplaceRequest? request,
        ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        await PutScopeCoreAsync(
            http,
            scopeId,
            request,
            service,
            includeActorId: false,
            callerFacing: true,
            ct);

    private static async Task<IResult> PutScopeCoreAsync(
        HttpContext http,
        string scopeId,
        ModelCatalogReplaceRequest? request,
        ILLMModelCatalogPolicyApplicationService service,
        bool includeActorId,
        bool callerFacing,
        CancellationToken ct)
    {
        if (TryCreateAccessDeniedResult(http, scopeId, callerFacing, out var denied))
            return denied;
        if (request is null)
        {
            return Error(
                callerFacing,
                StatusCodes.Status400BadRequest,
                "REQUEST_REQUIRED",
                "Request body is required.");
        }

        var parsed = ParseScopeIntent(request, callerFacing);
        if (parsed.Error is not null)
            return parsed.Error;

        try
        {
            var receipt = await service
                .ReplaceScopeAsync(scopeId, parsed.ScopeIntent!, ct)
                .ConfigureAwait(false);
            return Accepted(receipt, includeActorId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return callerFacing ? ToCallerError(ex) : ToError(ex);
        }
    }

    internal static async Task<IResult> ResetScopeAsync(
        HttpContext http,
        string scopeId,
        [FromBody] ModelCatalogResetRequest? request,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        await ResetScopeCoreAsync(
            http,
            scopeId,
            request,
            service,
            includeActorId: true,
            callerFacing: false,
            ct);

    internal static async Task<IResult> ResetScopeForCallerFacadeAsync(
        HttpContext http,
        string scopeId,
        ModelCatalogResetRequest? request,
        ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        await ResetScopeCoreAsync(
            http,
            scopeId,
            request,
            service,
            includeActorId: false,
            callerFacing: true,
            ct);

    private static async Task<IResult> ResetScopeCoreAsync(
        HttpContext http,
        string scopeId,
        ModelCatalogResetRequest? request,
        ILLMModelCatalogPolicyApplicationService service,
        bool includeActorId,
        bool callerFacing,
        CancellationToken ct)
    {
        if (TryCreateAccessDeniedResult(http, scopeId, callerFacing, out var denied))
            return denied;
        if (request is null)
        {
            return Error(
                callerFacing,
                StatusCodes.Status400BadRequest,
                "REQUEST_REQUIRED",
                "Request body is required.");
        }
        if (request.ExpectedStateVersion is null)
        {
            return Error(
                callerFacing,
                StatusCodes.Status400BadRequest,
                "EXPECTED_STATE_VERSION_REQUIRED",
                "expectedStateVersion is required.");
        }

        try
        {
            var receipt = await service.ResetScopeAsync(
                scopeId,
                new LLMModelCatalogResetIntent(request.ExpectedStateVersion.Value, request.MutationId),
                ct).ConfigureAwait(false);
            return Accepted(receipt, includeActorId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return callerFacing ? ToCallerError(ex) : ToError(ex);
        }
    }

    internal static Task<IResult> GetScopeCandidatesAsync(
        HttpContext http,
        string scopeId,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        GetScopeCandidatesCoreAsync(http, scopeId, service, callerFacing: false, ct);

    internal static Task<IResult> GetScopeCandidatesForCallerFacadeAsync(
        HttpContext http,
        string scopeId,
        ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        GetScopeCandidatesCoreAsync(http, scopeId, service, callerFacing: true, ct);

    private static async Task<IResult> GetScopeCandidatesCoreAsync(
        HttpContext http,
        string scopeId,
        ILLMModelCatalogPolicyApplicationService service,
        bool callerFacing,
        CancellationToken ct)
    {
        if (TryCreateAccessDeniedResult(http, scopeId, callerFacing, out var denied))
            return denied;
        if (!TryGetBearerToken(http, out var bearerToken))
        {
            return Error(
                callerFacing,
                StatusCodes.Status401Unauthorized,
                "AUTHENTICATION_REQUIRED",
                "Bearer token is required.");
        }

        try
        {
            var candidates = await service
                .GetScopeCandidatesAsync(bearerToken, ct)
                .ConfigureAwait(false);
            return Results.Json(new
            {
                services = candidates.Select(static candidate => new
                {
                    userServiceId = candidate.UserServiceId,
                    catalogServiceId = candidate.CatalogServiceId,
                    serviceSlug = candidate.Slug,
                    displayName = candidate.DisplayName ?? candidate.CatalogServiceDisplayName ?? candidate.Slug,
                    isActive = candidate.IsActive,
                    credentialStatus = candidate.CredentialStatus.WireValue,
                    credentialMissing = candidate.CredentialMissing,
                    connectionStatus = candidate.ConnectionStatus.WireValue,
                    nodeStatus = candidate.NodeStatus.WireValue,
                    isCallable = candidate.IsCallable,
                    availabilityReason = AvailabilityReasonValue(candidate.AvailabilityReason),
                    serviceType = ServiceTypeValue(candidate.ServiceType),
                    credentialSource = CredentialSourceValue(candidate.CredentialSource),
                }),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return callerFacing ? ToCallerError(ex) : ToError(ex);
        }
    }

    internal static Task<IResult> GetScopeCandidateModelsAsync(
        HttpContext http,
        string scopeId,
        string userServiceId,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        GetScopeCandidateModelsCoreAsync(
            http,
            scopeId,
            userServiceId,
            service,
            callerFacing: false,
            ct);

    internal static Task<IResult> GetScopeCandidateModelsForCallerFacadeAsync(
        HttpContext http,
        string scopeId,
        string userServiceId,
        ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        GetScopeCandidateModelsCoreAsync(
            http,
            scopeId,
            userServiceId,
            service,
            callerFacing: true,
            ct);

    private static async Task<IResult> GetScopeCandidateModelsCoreAsync(
        HttpContext http,
        string scopeId,
        string userServiceId,
        ILLMModelCatalogPolicyApplicationService service,
        bool callerFacing,
        CancellationToken ct)
    {
        if (TryCreateAccessDeniedResult(http, scopeId, callerFacing, out var denied))
            return denied;
        if (!TryGetBearerToken(http, out var bearerToken))
        {
            return Error(
                callerFacing,
                StatusCodes.Status401Unauthorized,
                "AUTHENTICATION_REQUIRED",
                "Bearer token is required.");
        }

        try
        {
            var discovery = await service
                .DiscoverScopeModelsAsync(bearerToken, userServiceId, ct)
                .ConfigureAwait(false);
            return Results.Json(new
            {
                sourceIdentity = discovery.SourceIdentity,
                serviceSlug = discovery.ServiceSlug,
                modelIds = discovery.ModelIds,
                defaultModelId = discovery.DefaultModelId,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return callerFacing ? ToCallerError(ex) : ToError(ex);
        }
    }

    private static async Task<IResult> GetPlatformAsync(
        HttpContext http,
        [FromServices] IPlatformAdminAuthorizer? authorizer,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct)
    {
        var authorization = await AuthorizePlatformAdminAsync(http, authorizer, ct).ConfigureAwait(false);
        if (authorization.Error is not null)
            return authorization.Error;

        try
        {
            var view = await service.GetPlatformAsync(ct).ConfigureAwait(false);
            return Results.Json(ToWireView(view));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> PutPlatformAsync(
        HttpContext http,
        [FromBody] ModelCatalogReplaceRequest? request,
        [FromServices] IPlatformAdminAuthorizer? authorizer,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct)
    {
        var authorization = await AuthorizePlatformAdminAsync(http, authorizer, ct).ConfigureAwait(false);
        if (authorization.Error is not null)
            return authorization.Error;
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "REQUEST_REQUIRED", "Request body is required.");

        var parsed = ParsePlatformIntent(request);
        if (parsed.Error is not null)
            return parsed.Error;

        try
        {
            var receipt = await service
                .ReplacePlatformAsync(parsed.PlatformIntent!, ct)
                .ConfigureAwait(false);
            return Accepted(receipt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> GetPlatformCandidatesAsync(
        HttpContext http,
        [FromServices] IPlatformAdminAuthorizer? authorizer,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct)
    {
        var authorization = await AuthorizePlatformAdminAsync(http, authorizer, ct).ConfigureAwait(false);
        if (authorization.Error is not null)
            return authorization.Error;

        try
        {
            var candidates = await service
                .GetPlatformCandidatesAsync(authorization.BearerToken, ct)
                .ConfigureAwait(false);
            return Results.Json(new
            {
                services = candidates.Select(static candidate => new
                {
                    catalogServiceId = candidate.CatalogServiceId,
                    serviceSlug = candidate.Slug,
                    displayName = candidate.DisplayName,
                    isActive = candidate.IsActive,
                    catalogMatched = true,
                    isSelectable = candidate.IsSelectable,
                    availabilityReason = PlatformAvailabilityReasonValue(candidate.AvailabilityReason),
                    serviceType = ServiceTypeValue(candidate.ServiceType),
                    visibility = candidate.Visibility.WireValue,
                    authMethod = candidate.AuthMethod.WireValue,
                    serviceCategory = candidate.ServiceCategory.WireValue,
                    requiresUserCredential = candidate.RequiresUserCredential,
                }),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> GetPlatformCandidateModelsAsync(
        HttpContext http,
        string catalogServiceId,
        [FromServices] IPlatformAdminAuthorizer? authorizer,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct)
    {
        var authorization = await AuthorizePlatformAdminAsync(http, authorizer, ct).ConfigureAwait(false);
        if (authorization.Error is not null)
            return authorization.Error;

        try
        {
            var discovery = await service
                .DiscoverPlatformModelsAsync(authorization.BearerToken, catalogServiceId, ct)
                .ConfigureAwait(false);
            return Results.Json(new
            {
                sourceIdentity = discovery.SourceIdentity,
                serviceSlug = discovery.ServiceSlug,
                modelIds = discovery.ModelIds,
                defaultModelId = discovery.DefaultModelId,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            return ToError(ex);
        }
    }

    private static ParsedIntent ParseScopeIntent(
        ModelCatalogReplaceRequest request,
        bool callerFacing = false)
    {
        var mode = request.Mode?.Trim().ToLowerInvariant() switch
        {
            "inherit_platform" => LLMModelCatalogPolicyMode.InheritPlatform,
            "custom_replace" => LLMModelCatalogPolicyMode.Custom,
            _ => LLMModelCatalogPolicyMode.Unspecified,
        };
        if (mode == LLMModelCatalogPolicyMode.Unspecified)
        {
            return ParsedIntent.Invalid(Error(
                callerFacing,
                StatusCodes.Status400BadRequest,
                "INVALID_MODE",
                "mode must be inherit_platform or custom_replace."));
        }
        if (request.ExpectedStateVersion is null)
        {
            return ParsedIntent.Invalid(Error(
                callerFacing,
                StatusCodes.Status400BadRequest,
                "EXPECTED_STATE_VERSION_REQUIRED",
                "expectedStateVersion is required."));
        }

        var parsedSources = ParseScopeSources(request.Sources, callerFacing);
        if (parsedSources.Error is not null)
            return ParsedIntent.Invalid(parsedSources.Error);
        return ParsedIntent.Scope(new ReplaceScopeLLMModelCatalogIntent(
            mode,
            request.ExpectedStateVersion.Value,
            request.MutationId,
            parsedSources.ScopeSources));
    }

    private static ParsedIntent ParsePlatformIntent(ModelCatalogReplaceRequest request)
    {
        if (!string.Equals(request.Mode?.Trim(), "custom_replace", StringComparison.OrdinalIgnoreCase))
        {
            return ParsedIntent.Invalid(Error(
                StatusCodes.Status400BadRequest,
                "INVALID_MODE",
                "Platform mode must be custom_replace."));
        }
        if (request.ExpectedStateVersion is null)
        {
            return ParsedIntent.Invalid(Error(
                StatusCodes.Status400BadRequest,
                "EXPECTED_STATE_VERSION_REQUIRED",
                "expectedStateVersion is required."));
        }

        var parsedSources = ParsePlatformSources(request.Sources);
        if (parsedSources.Error is not null)
            return ParsedIntent.Invalid(parsedSources.Error);
        return ParsedIntent.Platform(new ReplacePlatformLLMModelCatalogIntent(
            request.ExpectedStateVersion.Value,
            request.MutationId,
            parsedSources.PlatformSources));
    }

    private static ParsedSources ParseScopeSources(
        IReadOnlyList<ModelCatalogSourceInput?>? inputs,
        bool callerFacing)
    {
        if (inputs is null)
            return ParsedSources.Scope(null);

        var sources = new List<ScopeLLMModelCatalogSourceIntent?>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input is null)
            {
                sources.Add(null);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(input.CatalogServiceId))
            {
                return ParsedSources.Invalid(Error(
                    callerFacing,
                    StatusCodes.Status400BadRequest,
                    callerFacing ? "AI_MODEL_SOURCE_INVALID" : "SCOPE_CATALOG_SERVICE_FORBIDDEN",
                    callerFacing
                        ? "Custom model sources must reference an exact userServiceId."
                        : "Scope sources must reference only an exact userServiceId."));
            }

            var selection = ParseSelection(input.ModelSelection, callerFacing);
            if (selection.Error is not null)
                return ParsedSources.Invalid(selection.Error);
            sources.Add(new ScopeLLMModelCatalogSourceIntent(
                input.ServiceSlugSnapshot,
                input.UserServiceId,
                selection.Intent));
        }
        return ParsedSources.Scope(sources);
    }

    private static ParsedSources ParsePlatformSources(IReadOnlyList<ModelCatalogSourceInput?>? inputs)
    {
        if (inputs is null)
            return ParsedSources.Platform(null);

        var sources = new List<PlatformLLMModelCatalogSourceIntent?>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input is null)
            {
                sources.Add(null);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(input.UserServiceId))
            {
                return ParsedSources.Invalid(Error(
                    StatusCodes.Status400BadRequest,
                    "PLATFORM_USER_SERVICE_FORBIDDEN",
                    "Platform defaults cannot reference a user's service instance."));
            }

            var selection = ParseSelection(input.ModelSelection);
            if (selection.Error is not null)
                return ParsedSources.Invalid(selection.Error);
            sources.Add(new PlatformLLMModelCatalogSourceIntent(
                input.ServiceSlugSnapshot,
                input.CatalogServiceId,
                selection.Intent));
        }
        return ParsedSources.Platform(sources);
    }

    private static ParsedSelection ParseSelection(
        ModelCatalogSelectionInput? input,
        bool callerFacing = false) =>
        input?.Mode?.Trim().ToLowerInvariant() switch
        {
            "explicit_models" => ParsedSelection.Valid(new ExplicitLLMModelsIntent(input.ModelIds)),
            _ => ParsedSelection.Invalid(Error(
                callerFacing,
                StatusCodes.Status400BadRequest,
                "INVALID_MODEL_SELECTION",
                "modelSelection.mode must be explicit_models.")),
        };

    private static ModelCatalogView ToWireView(LLMModelCatalogView view) =>
        new(
            view.Owner.Kind == LLMModelCatalogPolicyOwnerKind.Platform ? "platform" : "scope",
            view.Owner.ScopeId,
            ModeValue(view.Mode),
            view.Configured,
            view.StateVersion,
            view.UpdatedAtUtc,
            MapSources(view.Sources),
            view.EffectiveSource == LLMModelCatalogEffectiveSourceKind.Scope ? "scope" : "platform",
            MapSources(view.EffectiveSources),
            view.LastMutationId);

    private static CallerModelCatalogView ToCallerWireView(LLMModelCatalogView view) =>
        new(
            ModeValue(view.Mode),
            view.Configured,
            view.StateVersion,
            view.UpdatedAtUtc,
            MapSources(view.Sources),
            view.EffectiveSource == LLMModelCatalogEffectiveSourceKind.Scope ? "custom" : "platform",
            MapSources(view.EffectiveSources),
            view.LastMutationId);

    private static IReadOnlyList<ModelCatalogSourceView> MapSources(
        IReadOnlyList<LLMModelCatalogPolicySource> sources) =>
        sources.Select(static source =>
        {
            var (catalogServiceId, userServiceId) = source.SourceIdentity switch
            {
                NyxIDCatalogServiceModelSourceIdentity catalog => (catalog.CatalogServiceId, (string?)null),
                NyxIDUserServiceModelSourceIdentity user => ((string?)null, user.UserServiceId),
                _ => (null, null),
            };
            var sourceId = catalogServiceId is not null
                ? $"catalog:{catalogServiceId}"
                : $"user:{userServiceId}";
            var serviceSlug = source.ServiceSlugSnapshot ?? string.Empty;
            return new ModelCatalogSourceView(
                sourceId,
                serviceSlug,
                serviceSlug,
                catalogServiceId,
                userServiceId,
                new ModelCatalogSelectionView(
                    "explicit_models",
                    source.ModelSelection.UpstreamModelIds));
        }).ToArray();

    private static async Task<PlatformAdminAuthorization> AuthorizePlatformAdminAsync(
        HttpContext http,
        IPlatformAdminAuthorizer? authorizer,
        CancellationToken ct)
    {
        if (authorizer is null)
        {
            return new(string.Empty, Error(
                StatusCodes.Status503ServiceUnavailable,
                "ADMIN_AUTHORIZATION_UNAVAILABLE",
                "Admin authorization is unavailable."));
        }
        if (!TryGetBearerToken(http, out var bearerToken))
            return new(string.Empty, PlatformAdminForbidden());

        try
        {
            var caller = await authorizer.ResolveCallerAsync(bearerToken, ct).ConfigureAwait(false);
            if (!caller.IsElevated || string.IsNullOrWhiteSpace(caller.UserId) ||
                !string.Equals(caller.GrantSource, PlatformAdminGrantSources.AllowedUserId, StringComparison.Ordinal))
            {
                return new(string.Empty, PlatformAdminForbidden());
            }
            return new(bearerToken, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(string.Empty, PlatformAdminForbidden());
        }
    }

    private static IResult Accepted(UserConfigSaveReceipt receipt, bool includeActorId = true) =>
        includeActorId
            ? Results.Accepted(value: new
        {
            accepted = true,
            actorId = receipt.ActorId,
            commandId = receipt.CommandId,
            correlationId = receipt.CorrelationId,
            ackStage = receipt.AckStage,
            ackedAt = receipt.AckedAtUtc,
            note = "Command accepted for dispatch. Re-query GET until lastMutationId matches mutationId.",
        })
            : Results.Accepted(value: new
            {
                accepted = true,
                commandId = receipt.CommandId,
                correlationId = receipt.CorrelationId,
                ackStage = receipt.AckStage,
                ackedAt = receipt.AckedAtUtc,
                note = "Command accepted for dispatch. Re-query GET until lastMutationId matches mutationId.",
            });

    private static IResult ToError(LLMModelCatalogApplicationException exception) =>
        Error(
            exception.Kind switch
            {
                LLMModelCatalogApplicationErrorKind.InvalidRequest => StatusCodes.Status400BadRequest,
                LLMModelCatalogApplicationErrorKind.Conflict => StatusCodes.Status409Conflict,
                LLMModelCatalogApplicationErrorKind.AuthenticationRejected => StatusCodes.Status401Unauthorized,
                LLMModelCatalogApplicationErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status503ServiceUnavailable,
            },
            exception.Code,
            exception.Message);

    private static IResult ToCallerError(LLMModelCatalogApplicationException exception) =>
        exception.Kind switch
        {
            LLMModelCatalogApplicationErrorKind.InvalidRequest => CallerError(
                StatusCodes.Status400BadRequest,
                "AI_MODEL_REQUEST_INVALID",
                "Model settings request is invalid."),
            LLMModelCatalogApplicationErrorKind.Conflict => CallerError(
                StatusCodes.Status409Conflict,
                "AI_MODEL_CONFLICT",
                "Model settings conflict with the current state."),
            LLMModelCatalogApplicationErrorKind.AuthenticationRejected => CallerError(
                StatusCodes.Status401Unauthorized,
                "AI_MODEL_AUTHENTICATION_REJECTED",
                "Model source authentication was rejected."),
            LLMModelCatalogApplicationErrorKind.Forbidden => CallerError(
                StatusCodes.Status403Forbidden,
                "AI_MODEL_ACCESS_DENIED",
                "Model source access was denied."),
            _ => CallerError(
                StatusCodes.Status503ServiceUnavailable,
                "AI_MODEL_SERVICE_UNAVAILABLE",
                "Model settings are temporarily unavailable."),
        };

    private static bool TryCreateAccessDeniedResult(
        HttpContext http,
        string scopeId,
        bool callerFacing,
        out IResult denied)
    {
        if (!callerFacing)
        {
            return AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(
                http,
                scopeId,
                out denied);
        }

        if (AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var callerScopeId) &&
            string.Equals(callerScopeId, scopeId, StringComparison.Ordinal))
        {
            denied = Results.Empty;
            return false;
        }

        denied = CallerError(
            StatusCodes.Status403Forbidden,
            "AI_MODEL_ACCESS_CONTEXT_REQUIRED",
            "Authenticated caller access context is required.");
        return true;
    }

    private static IResult PlatformAdminForbidden() =>
        Error(
            StatusCodes.Status403Forbidden,
            "PLATFORM_ADMIN_REQUIRED",
            "Platform administrator access is required.");

    private static string ModeValue(LLMModelCatalogPolicyMode mode) => mode switch
    {
        LLMModelCatalogPolicyMode.Custom => "custom_replace",
        _ => "inherit_platform",
    };

    private static string ServiceTypeValue(NyxIdModelSourceServiceType serviceType) =>
        serviceType.Kind switch
        {
            NyxIdModelSourceServiceTypeKind.HTTP => "http",
            NyxIdModelSourceServiceTypeKind.SSH => "ssh",
            _ => serviceType.WireValue ?? "unknown",
        };

    private static string AvailabilityReasonValue(NyxIdModelSourceAvailabilityReason reason) => reason switch
    {
        NyxIdModelSourceAvailabilityReason.Available => "available",
        NyxIdModelSourceAvailabilityReason.UnsupportedServiceType => "unsupported_service_type",
        NyxIdModelSourceAvailabilityReason.ServiceInactive => "service_inactive",
        NyxIdModelSourceAvailabilityReason.CredentialMissing => "credential_missing",
        NyxIdModelSourceAvailabilityReason.CredentialInactive => "credential_inactive",
        NyxIdModelSourceAvailabilityReason.ConnectionExpired => "connection_expired",
        NyxIdModelSourceAvailabilityReason.ConnectionUnavailable => "connection_unavailable",
        NyxIdModelSourceAvailabilityReason.OrganizationAccessDenied => "organization_access_denied",
        NyxIdModelSourceAvailabilityReason.NodeUnavailable => "node_unavailable",
        NyxIdModelSourceAvailabilityReason.UnsupportedServiceSlug => "unsupported_service_slug",
        _ => "unavailable",
    };

    private static string PlatformAvailabilityReasonValue(
        NyxIdPlatformModelSourceAvailabilityReason reason) => reason switch
        {
            NyxIdPlatformModelSourceAvailabilityReason.Available => "available",
            NyxIdPlatformModelSourceAvailabilityReason.NotPublic => "not_public",
            NyxIdPlatformModelSourceAvailabilityReason.ServiceInactive => "service_inactive",
            NyxIdPlatformModelSourceAvailabilityReason.UnsupportedServiceType => "unsupported_service_type",
            NyxIdPlatformModelSourceAvailabilityReason.InvalidServiceSlug => "invalid_service_slug",
            NyxIdPlatformModelSourceAvailabilityReason.ProviderService => "provider_service",
            NyxIdPlatformModelSourceAvailabilityReason.UnsupportedServiceCategory =>
                "unsupported_service_category",
            NyxIdPlatformModelSourceAvailabilityReason.UserCredentialRequired =>
                "user_credential_required",
            NyxIdPlatformModelSourceAvailabilityReason.TokenExchangeUnsupported =>
                "token_exchange_unsupported",
            NyxIdPlatformModelSourceAvailabilityReason.UnsupportedAuthMethod =>
                "unsupported_auth_method",
            _ => "unavailable",
        };

    private static object CredentialSourceValue(NyxIdScopeCredentialSource source) => source switch
    {
        NyxIdOrganizationCredentialSource organization => new
        {
            type = "organization",
            organizationId = organization.OrganizationId,
            organizationName = organization.OrganizationName,
            role = organization.Role.ToString().ToLowerInvariant(),
            allowed = organization.Allowed,
        },
        _ => new { type = "personal" },
    };

    private static bool TryGetBearerToken(HttpContext http, out string bearerToken)
    {
        var header = http.Request.Headers.Authorization.FirstOrDefault();
        if (header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
        {
            bearerToken = header["Bearer ".Length..].Trim();
            return bearerToken.Length != 0;
        }
        bearerToken = string.Empty;
        return false;
    }

    private static IResult Error(int statusCode, string code, string detail) =>
        Results.Json(new { error = code, detail }, statusCode: statusCode);

    private static IResult Error(
        bool callerFacing,
        int statusCode,
        string code,
        string message) =>
        callerFacing
            ? CallerError(statusCode, code, message)
            : Error(statusCode, code, message);

    private static IResult CallerError(int statusCode, string code, string message) =>
        AIWorkspaceEndpoints.Error(statusCode, code, message);

    private sealed record PlatformAdminAuthorization(string BearerToken, IResult? Error);

    private sealed record ParsedIntent(
        ReplaceScopeLLMModelCatalogIntent? ScopeIntent,
        ReplacePlatformLLMModelCatalogIntent? PlatformIntent,
        IResult? Error)
    {
        public static ParsedIntent Scope(ReplaceScopeLLMModelCatalogIntent intent) => new(intent, null, null);
        public static ParsedIntent Platform(ReplacePlatformLLMModelCatalogIntent intent) => new(null, intent, null);
        public static ParsedIntent Invalid(IResult error) => new(null, null, error);
    }

    private sealed record ParsedSources(
        IReadOnlyList<ScopeLLMModelCatalogSourceIntent?>? ScopeSources,
        IReadOnlyList<PlatformLLMModelCatalogSourceIntent?>? PlatformSources,
        IResult? Error)
    {
        public static ParsedSources Scope(IReadOnlyList<ScopeLLMModelCatalogSourceIntent?>? sources) =>
            new(sources, null, null);
        public static ParsedSources Platform(IReadOnlyList<PlatformLLMModelCatalogSourceIntent?>? sources) =>
            new(null, sources, null);
        public static ParsedSources Invalid(IResult error) => new(null, null, error);
    }

    private sealed record ParsedSelection(ExplicitLLMModelsIntent? Intent, IResult? Error)
    {
        public static ParsedSelection Valid(ExplicitLLMModelsIntent intent) => new(intent, null);
        public static ParsedSelection Invalid(IResult error) => new(null, error);
    }
}

internal sealed class ModelCatalogReplaceRequest
{
    public string? Mode { get; init; }
    public long? ExpectedStateVersion { get; init; }
    public string? MutationId { get; init; }
    public IReadOnlyList<ModelCatalogSourceInput?>? Sources { get; init; }
}

internal sealed record ModelCatalogResetRequest(long? ExpectedStateVersion, string? MutationId);

internal sealed record ModelCatalogSourceInput(
    string? ServiceSlugSnapshot,
    string? CatalogServiceId,
    string? UserServiceId,
    ModelCatalogSelectionInput? ModelSelection);

internal sealed record ModelCatalogSelectionInput(string? Mode, IReadOnlyList<string?>? ModelIds);

internal sealed record ModelCatalogView(
    string OwnerKind,
    string? ScopeId,
    string Mode,
    bool Configured,
    long StateVersion,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<ModelCatalogSourceView> Sources,
    string EffectiveSource,
    IReadOnlyList<ModelCatalogSourceView> EffectiveSources,
    string? LastMutationId);

internal sealed record CallerModelCatalogView(
    string Mode,
    bool Configured,
    long StateVersion,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<ModelCatalogSourceView> Sources,
    string EffectiveSource,
    IReadOnlyList<ModelCatalogSourceView> EffectiveSources,
    string? LastMutationId);

internal sealed record ModelCatalogSourceView(
    string SourceId,
    string DisplayName,
    string ServiceSlugSnapshot,
    string? CatalogServiceId,
    string? UserServiceId,
    ModelCatalogSelectionView ModelSelection);

internal sealed record ModelCatalogSelectionView(
    string Mode,
    IReadOnlyList<string> ModelIds);
