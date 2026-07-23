using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Hosting.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

public static class AgentProfileEndpoints
{
    public static IEndpointRouteBuilder MapAgentProfileEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var management = ScopeEndpointRouteGroups.MapScopeGroup(app).WithTags("AgentProfiles");
        management.MapPost("/{scopeId}/agent-profiles", HandleCreateAsync);
        management.MapGet("/{scopeId}/agent-profiles/{profileSlug}", HandleGetOwnedAsync);
        management.MapPut("/{scopeId}/agent-profiles/{profileSlug}/draft", HandleUpdateDraftAsync);
        management.MapPut(
            "/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}",
            HandleUpsertSkillBindingAsync);
        management.MapDelete(
            "/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}",
            HandleRemoveSkillBindingAsync);
        management.MapPost("/{scopeId}/agent-profiles/{profileSlug}:validate", HandleValidateAsync);
        management.MapPost("/{scopeId}/agent-profiles/{profileSlug}:publish", HandlePublishAsync);

        var discovery = app.MapGroup("/api/agent-profiles").WithTags("AgentProfiles");
        if (ScopeEndpointRouteGroups.IsAuthenticationEnabled(app.ServiceProvider))
            discovery.RequireAuthorization();
        discovery.MapGet("/{ownerHandle}/{profileSlug}", HandleDiscoveryAsync);
        return app;
    }

    private static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IAgentProfileCommandService commands,
        CancellationToken ct)
    {
        if (!AgentProfileHttpCallerContext.TryRequireScope(http, scopeId, out var caller, out var denied))
            return denied;
        if (!AgentProfileHttpPreconditions.TryReadRequiredIdempotencyKey(
                http,
                out var idempotencyKey,
                out var rejected))
        {
            return rejected;
        }

        var (request, bodyRejected) = await ReadRequestAsync<CreateAgentProfileHttpRequest>(http, ct);
        if (bodyRejected is not null)
            return bodyRejected;
        if (!AgentProfileHttpRequestMapper.TryMap(request, out var mappedRequest))
            return InvalidHttpBody();

        try
        {
            var receipt = await commands.CreateAsync(
                caller,
                mappedRequest,
                idempotencyKey,
                ct);
            return AgentProfileHttpResults.Accepted(receipt, scopeId, mappedRequest.ProfileSlug);
        }
        catch (Exception exception) when (
            AgentProfileHttpResults.TryMapException(exception, out var result))
        {
            return result;
        }
    }

    private static async Task<IResult> HandleGetOwnedAsync(
        HttpContext http,
        string scopeId,
        string profileSlug,
        [FromServices] IAgentProfileQueryService queries,
        CancellationToken ct)
    {
        if (!AgentProfileHttpCallerContext.TryRequireScope(http, scopeId, out var caller, out var denied))
            return denied;

        try
        {
            var snapshot = await queries.GetOwnedAsync(caller, profileSlug, ct);
            return snapshot is null
                ? AgentProfileHttpResults.Error(
                    StatusCodes.Status404NotFound,
                    "AGENT_PROFILE_NOT_FOUND")
                : AgentProfileHttpResults.Management(http, snapshot);
        }
        catch (Exception exception) when (
            AgentProfileHttpResults.TryMapException(exception, out var result))
        {
            return result;
        }
    }

    private static async Task<IResult> HandleUpdateDraftAsync(
        HttpContext http,
        string scopeId,
        string profileSlug,
        [FromServices] IAgentProfileCommandService commands,
        CancellationToken ct)
    {
        if (!TryRequireVersionedMutation(
                http,
                scopeId,
                out var caller,
                out var expectedVersion,
                out var idempotencyKey,
                out var rejected))
        {
            return rejected;
        }

        var (request, bodyRejected) = await ReadRequestAsync<UpdateAgentProfileDraftHttpRequest>(http, ct);
        if (bodyRejected is not null)
            return bodyRejected;
        if (!AgentProfileHttpRequestMapper.TryMap(request, out var mappedRequest))
            return InvalidHttpBody();

        try
        {
            var receipt = await commands.UpdateDraftAsync(
                caller,
                profileSlug,
                expectedVersion,
                mappedRequest,
                idempotencyKey,
                ct);
            return AgentProfileHttpResults.Accepted(receipt, scopeId, profileSlug);
        }
        catch (Exception exception) when (
            AgentProfileHttpResults.TryMapException(exception, out var result))
        {
            return result;
        }
    }

    private static async Task<IResult> HandleUpsertSkillBindingAsync(
        HttpContext http,
        string scopeId,
        string profileSlug,
        string bindingId,
        [FromServices] IAgentProfileCommandService commands,
        CancellationToken ct)
    {
        if (!TryRequireVersionedMutation(
                http,
                scopeId,
                out var caller,
                out var expectedVersion,
                out var idempotencyKey,
                out var rejected))
        {
            return rejected;
        }

        var (request, bodyRejected) = await ReadRequestAsync<AgentProfileSkillBindingHttpRequest>(http, ct);
        if (bodyRejected is not null)
            return bodyRejected;
        if (!AgentProfileHttpRequestMapper.TryMap(request, out var mappedRequest))
            return InvalidHttpBody();

        try
        {
            var receipt = await commands.UpsertSkillBindingAsync(
                caller,
                profileSlug,
                bindingId,
                expectedVersion,
                mappedRequest,
                idempotencyKey,
                ct);
            return AgentProfileHttpResults.Accepted(receipt, scopeId, profileSlug);
        }
        catch (Exception exception) when (
            AgentProfileHttpResults.TryMapException(exception, out var result))
        {
            return result;
        }
    }

    private static async Task<IResult> HandleRemoveSkillBindingAsync(
        HttpContext http,
        string scopeId,
        string profileSlug,
        string bindingId,
        [FromServices] IAgentProfileCommandService commands,
        CancellationToken ct)
    {
        if (!TryRequireVersionedMutation(
                http,
                scopeId,
                out var caller,
                out var expectedVersion,
                out var idempotencyKey,
                out var rejected))
        {
            return rejected;
        }

        try
        {
            var receipt = await commands.RemoveSkillBindingAsync(
                caller,
                profileSlug,
                bindingId,
                expectedVersion,
                idempotencyKey,
                ct);
            return AgentProfileHttpResults.Accepted(receipt, scopeId, profileSlug);
        }
        catch (Exception exception) when (
            AgentProfileHttpResults.TryMapException(exception, out var result))
        {
            return result;
        }
    }

    private static async Task<IResult> HandleValidateAsync(
        HttpContext http,
        string scopeId,
        string profileSlug,
        [FromServices] IAgentProfileCommandService commands,
        CancellationToken ct)
    {
        if (!AgentProfileHttpCallerContext.TryRequireScope(http, scopeId, out var caller, out var denied))
            return denied;

        try
        {
            var report = await commands.ValidateAsync(caller, profileSlug, ct);
            return AgentProfileHttpResults.Validation(report);
        }
        catch (Exception exception) when (
            AgentProfileHttpResults.TryMapException(exception, out var result))
        {
            return result;
        }
    }

    private static async Task<IResult> HandlePublishAsync(
        HttpContext http,
        string scopeId,
        string profileSlug,
        [FromServices] IAgentProfileCommandService commands,
        CancellationToken ct)
    {
        if (!TryRequireVersionedMutation(
                http,
                scopeId,
                out var caller,
                out var expectedVersion,
                out var idempotencyKey,
                out var rejected))
        {
            return rejected;
        }

        try
        {
            var receipt = await commands.PublishAsync(
                caller,
                profileSlug,
                expectedVersion,
                idempotencyKey,
                ct);
            return AgentProfileHttpResults.Accepted(receipt, scopeId, profileSlug);
        }
        catch (Exception exception) when (
            AgentProfileHttpResults.TryMapException(exception, out var result))
        {
            return result;
        }
    }

    private static async Task<IResult> HandleDiscoveryAsync(
        HttpContext http,
        string ownerHandle,
        string profileSlug,
        [FromServices] IAgentProfileQueryService queries,
        CancellationToken ct)
    {
        if (!AgentProfileHttpCallerContext.TryRequireDiscovery(http, out var caller, out var denied))
            return denied;

        try
        {
            var snapshot = await queries.ResolveVisibleAsync(
                caller,
                new AgentProfileReference
                {
                    OwnerHandle = ownerHandle,
                    ProfileSlug = profileSlug,
                },
                ct);
            return snapshot is null
                ? AgentProfileHttpResults.Error(
                    StatusCodes.Status404NotFound,
                    "AGENT_PROFILE_NOT_FOUND")
                : AgentProfileHttpResults.Discovery(snapshot);
        }
        catch (Exception exception) when (
            AgentProfileHttpResults.TryMapException(exception, out var result))
        {
            return result;
        }
    }

    private static bool TryRequireVersionedMutation(
        HttpContext http,
        string scopeId,
        out AgentProfileCallerContext caller,
        out long expectedVersion,
        out string? idempotencyKey,
        out IResult rejected)
    {
        expectedVersion = 0;
        idempotencyKey = null;
        if (!AgentProfileHttpCallerContext.TryRequireScope(http, scopeId, out caller, out rejected))
            return false;
        if (!AgentProfileHttpPreconditions.TryRequireIfMatch(http, out expectedVersion, out rejected))
            return false;
        return AgentProfileHttpPreconditions.TryReadOptionalIdempotencyKey(
            http,
            out idempotencyKey,
            out rejected);
    }

    private static async Task<(T? Request, IResult? Rejected)> ReadRequestAsync<T>(
        HttpContext http,
        CancellationToken ct)
        where T : class
    {
        var canHaveBody = http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ??
            http.Request.ContentLength.GetValueOrDefault() > 0;
        if (!canHaveBody || http.Request.ContentLength == 0)
            return (null, InvalidHttpBody());

        if (!HasSupportedJsonContentType(http.Request))
            return (null, UnsupportedMediaType());

        try
        {
            return (await http.Request.ReadFromJsonAsync<T>(cancellationToken: ct), null);
        }
        catch (JsonException)
        {
            return (null, InvalidHttpBody());
        }
    }

    private static bool HasSupportedJsonContentType(HttpRequest request)
    {
        if (!request.HasJsonContentType() ||
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType))
        {
            return false;
        }

        var charset = mediaType.CharSet?.Trim('"');
        if (string.IsNullOrEmpty(charset))
            return true;

        try
        {
            _ = Encoding.GetEncoding(charset);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IResult InvalidHttpBody() =>
        AgentProfileHttpResults.Error(
            StatusCodes.Status400BadRequest,
            "INVALID_AGENT_PROFILE_HTTP_BODY");

    private static IResult UnsupportedMediaType() =>
        AgentProfileHttpResults.Error(
            StatusCodes.Status415UnsupportedMediaType,
            "UNSUPPORTED_MEDIA_TYPE");
}
