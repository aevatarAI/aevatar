using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.Mainnet.Host.Api.ModelCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.AI;

/// <summary>
/// Caller-scoped command facade for the AI model settings surface. Scope identity is
/// always derived from the authenticated principal and never accepted in the URL or body.
/// </summary>
internal static class AIWorkspaceModelsManagementEndpoints
{
    public static RouteGroupBuilder MapAIWorkspaceModelsManagementEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var models = api.MapGroup("/models");
        var getPersonalDefault = models.MapGet("/personal-default", GetPersonalDefaultAsync);
        var putPersonalDefault = models.MapPut("/personal-default", PutPersonalDefaultAsync);
        var getCatalog = models.MapGet("/catalog", GetCatalogAsync);
        var putCatalog = models.MapPut("/catalog", PutCatalogAsync);
        var deleteCatalog = models.MapDelete("/catalog", DeleteCatalogAsync);
        var getCandidates = models.MapGet("/catalog/candidates", GetCandidatesAsync);
        var getCandidateModels = models.MapGet(
            "/catalog/candidates/{userServiceId}/models",
            GetCandidateModelsAsync);

        AIWorkspaceEndpoints.Audit(getPersonalDefault, "models.personal-default.get");
        AIWorkspaceEndpoints.Audit(putPersonalDefault, "models.personal-default.put");
        AIWorkspaceEndpoints.Audit(getCatalog, "models.catalog.get");
        AIWorkspaceEndpoints.Audit(putCatalog, "models.catalog.put");
        AIWorkspaceEndpoints.Audit(deleteCatalog, "models.catalog.delete");
        AIWorkspaceEndpoints.Audit(getCandidates, "models.catalog.candidates");
        AIWorkspaceEndpoints.Audit(getCandidateModels, "models.catalog.candidate-models");
        return api;
    }

    private static async Task<IResult> GetPersonalDefaultAsync(
        HttpContext http,
        [FromServices] IUserLlmPreferenceService service,
        CancellationToken ct)
    {
        if (!AIWorkspaceEndpoints.TryGetScopeId(http, out _, out var denied))
            return denied;

        try
        {
            var view = await service
                .GetSettingsAsync(BearerToken(http), ct)
                .ConfigureAwait(false);
            return Results.Json(UserLlmSettingsResponse.FromApplication(view));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return AIWorkspaceEndpoints.Error(
                StatusCodes.Status400BadRequest,
                "AI_PERSONAL_MODEL_DEFAULT_INVALID",
                "Personal model settings request is invalid.");
        }
        catch
        {
            return AIWorkspaceEndpoints.Error(
                StatusCodes.Status503ServiceUnavailable,
                "AI_PERSONAL_MODEL_DEFAULT_UNAVAILABLE",
                "Personal model settings are temporarily unavailable.");
        }
    }

    private static async Task<IResult> PutPersonalDefaultAsync(
        HttpContext http,
        [FromBody] AIWorkspacePersonalModelDefaultRequest? request,
        [FromServices] IUserLlmPreferenceService preferenceService,
        [FromServices] IUserConfigService configService,
        CancellationToken ct)
    {
        if (!AIWorkspaceEndpoints.TryGetScopeId(http, out _, out var denied))
            return denied;
        if (request is null)
        {
            return AIWorkspaceEndpoints.Error(
                StatusCodes.Status400BadRequest,
                "REQUEST_REQUIRED",
                "Request body is required.");
        }

        try
        {
            var token = BearerToken(http);
            var intent = await BuildPersonalDefaultIntentAsync(
                request,
                token,
                preferenceService,
                ct).ConfigureAwait(false);
            var receipt = await configService
                .SaveLlmPreferenceAsync(token, intent, ct)
                .ConfigureAwait(false);
            return Accepted(receipt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return AIWorkspaceEndpoints.Error(
                StatusCodes.Status400BadRequest,
                "AI_PERSONAL_MODEL_DEFAULT_INVALID",
                "Personal model settings request is invalid.");
        }
        catch
        {
            return AIWorkspaceEndpoints.Error(
                StatusCodes.Status503ServiceUnavailable,
                "AI_PERSONAL_MODEL_DEFAULT_UNAVAILABLE",
                "Personal model settings are temporarily unavailable.");
        }
    }

    private static Task<IResult> GetCatalogAsync(
        HttpContext http,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        WithCallerScopeAsync(
            http,
            (scopeId, cancellationToken) => LLMModelCatalogEndpoints.GetScopeForCallerFacadeAsync(
                http,
                scopeId,
                service,
                cancellationToken),
            ct);

    private static Task<IResult> PutCatalogAsync(
        HttpContext http,
        [FromBody] AIWorkspaceModelCatalogReplaceRequest? request,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        WithCallerScopeAsync(
            http,
            (scopeId, cancellationToken) => LLMModelCatalogEndpoints.PutScopeForCallerFacadeAsync(
                http,
                scopeId,
                request?.ToCanonical(),
                service,
                cancellationToken),
            ct);

    private static Task<IResult> DeleteCatalogAsync(
        HttpContext http,
        [FromBody] AIWorkspaceModelCatalogResetRequest? request,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        WithCallerScopeAsync(
            http,
            (scopeId, cancellationToken) => LLMModelCatalogEndpoints.ResetScopeForCallerFacadeAsync(
                http,
                scopeId,
                request?.ToCanonical(),
                service,
                cancellationToken),
            ct);

    private static Task<IResult> GetCandidatesAsync(
        HttpContext http,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        WithCallerScopeAsync(
            http,
            (scopeId, cancellationToken) => LLMModelCatalogEndpoints.GetScopeCandidatesForCallerFacadeAsync(
                http,
                scopeId,
                service,
                cancellationToken),
            ct);

    private static Task<IResult> GetCandidateModelsAsync(
        HttpContext http,
        string userServiceId,
        [FromServices] ILLMModelCatalogPolicyApplicationService service,
        CancellationToken ct) =>
        WithCallerScopeAsync(
            http,
            (scopeId, cancellationToken) => LLMModelCatalogEndpoints.GetScopeCandidateModelsForCallerFacadeAsync(
                http,
                scopeId,
                userServiceId,
                service,
                cancellationToken),
            ct);

    private static async Task<UserLlmPreferenceIntent> BuildPersonalDefaultIntentAsync(
        AIWorkspacePersonalModelDefaultRequest request,
        string? bearerToken,
        IUserLlmPreferenceService preferenceService,
        CancellationToken ct)
    {
        var routeValue = request.RouteValue?.Trim();
        if (string.IsNullOrEmpty(routeValue))
            throw new InvalidOperationException("routeValue is required.");
        if (request.ModelId is not null && string.IsNullOrWhiteSpace(request.ModelId))
            throw new InvalidOperationException("modelId must be null or a non-empty model ID.");

        var modelSelection = request.ModelId is null
            ? new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault }
            : new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = request.ModelId,
            };
        if (UserLlmPreferenceWriteCore.IsGatewayWriteAlias(routeValue))
            return new SelectGatewayUserLlmPreferenceIntent(modelSelection);

        var settings = await preferenceService
            .GetSettingsAsync(bearerToken, ct)
            .ConfigureAwait(false);
        var route = settings.RouteOptions
            .Where(option => string.Equals(option.RouteValue, routeValue, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var userServiceId = route.Length == 1 ? route[0].UserServiceId?.Trim() : null;
        if (string.IsNullOrEmpty(userServiceId))
            throw new InvalidOperationException($"LLM route '{routeValue}' is not selectable.");

        return new SelectUserServiceUserLlmPreferenceIntent(
            userServiceId,
            modelSelection);
    }

    private static Task<IResult> WithCallerScopeAsync(
        HttpContext http,
        Func<string, CancellationToken, Task<IResult>> action,
        CancellationToken ct)
    {
        if (!AIWorkspaceEndpoints.TryGetScopeId(http, out var scopeId, out var denied))
            return Task.FromResult(denied);
        return action(scopeId, ct);
    }

    private static string? BearerToken(HttpContext http)
    {
        var value = http.Request.Headers.Authorization.ToString().Trim();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = value[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static IResult Accepted(UserConfigSaveReceipt receipt) =>
        Results.Accepted(value: new
        {
            accepted = receipt.Accepted,
            commandId = receipt.CommandId,
            correlationId = receipt.CorrelationId,
            ackStage = receipt.AckStage,
            ackedAt = receipt.AckedAtUtc,
        });
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AIWorkspacePersonalModelDefaultRequest(
    string? RouteValue,
    string? ModelId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AIWorkspaceModelCatalogReplaceRequest(
    string? Mode,
    long? ExpectedVersion,
    string? IdempotencyKey,
    IReadOnlyList<AIWorkspaceModelCatalogSourceRequest?>? Sources)
{
    public ModelCatalogReplaceRequest ToCanonical() => new()
    {
        Mode = Mode,
        ExpectedStateVersion = ExpectedVersion,
        MutationId = IdempotencyKey,
        Sources = Sources?.Select(static source => source?.ToCanonical()).ToArray(),
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AIWorkspaceModelCatalogSourceRequest(
    string? ServiceSlugSnapshot,
    string? UserServiceId,
    AIWorkspaceModelCatalogSelectionRequest? ModelSelection)
{
    public ModelCatalogSourceInput ToCanonical() => new(
        ServiceSlugSnapshot,
        CatalogServiceId: null,
        UserServiceId,
        ModelSelection?.ToCanonical());
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AIWorkspaceModelCatalogSelectionRequest(
    string? Mode,
    IReadOnlyList<string?>? ModelIds)
{
    public ModelCatalogSelectionInput ToCanonical() => new(Mode, ModelIds);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AIWorkspaceModelCatalogResetRequest(
    long? ExpectedVersion,
    string? IdempotencyKey)
{
    public ModelCatalogResetRequest ToCanonical() => new(ExpectedVersion, IdempotencyKey);
}
