using System.Security.Claims;
using Aevatar.AI.Application.CodexExecution;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.ManagedCodex;

internal static class ManagedCodexCredentialEndpoints
{
    public static IEndpointRouteBuilder MapManagedCodexCredentialEndpoints(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/managed-codex/credential")
            .WithTags("ManagedCodex")
            .RequireAuthorization();
        group.MapGet("", StatusAsync);
        group.MapPost("", ProvisionAsync);
        group.MapPost("/rotate", RotateAsync);
        group.MapDelete("", RevokeAsync);
        return app;
    }

    internal static async Task<IResult> StatusAsync(
        HttpContext http,
        IManagedCodexCredentialQueryPort queryPort,
        IOptions<ManagedCodexOptions> options,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queryPort);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!TryResolveSubject(http, out var userId))
            return Results.Unauthorized();

        var snapshot = await queryPort.ResolveAsync(Owner(userId), ct).ConfigureAwait(false);
        if (snapshot?.Credential is null)
        {
            return Results.Ok(new
            {
                enabled = options.Value.Enabled,
                status = "not_provisioned",
                state_version = 0L,
                cleanup_pending = 0,
            });
        }

        var credential = snapshot.Credential;
        return Results.Ok(new
        {
            enabled = options.Value.Enabled,
            status = EffectiveStatus(credential, timeProvider.GetUtcNow()),
            expires_at_unix_ms = credential.ExpiresAt?.ToDateTimeOffset().ToUnixTimeMilliseconds(),
            state_version = snapshot.StateVersion,
            cleanup_pending = snapshot.PendingRevocations.Count,
        });
    }

    internal static Task<IResult> ProvisionAsync(
        HttpContext http,
        IManagedCodexCredentialLifecycle lifecycle,
        CancellationToken ct) =>
        MutateAsync(http, lifecycle, static (service, bearer, userId, token) =>
            service.ProvisionAsync(bearer, userId, token), ct);

    internal static Task<IResult> RotateAsync(
        HttpContext http,
        IManagedCodexCredentialLifecycle lifecycle,
        CancellationToken ct) =>
        MutateAsync(http, lifecycle, static (service, bearer, userId, token) =>
            service.RotateAsync(bearer, userId, token), ct);

    internal static Task<IResult> RevokeAsync(
        HttpContext http,
        IManagedCodexCredentialLifecycle lifecycle,
        CancellationToken ct) =>
        MutateAsync(http, lifecycle, static (service, bearer, userId, token) =>
            service.RevokeAsync(bearer, userId, token), ct);

    private static async Task<IResult> MutateAsync(
        HttpContext http,
        IManagedCodexCredentialLifecycle lifecycle,
        Func<IManagedCodexCredentialLifecycle, string, string, CancellationToken,
            Task<ManagedCodexCredentialMutationResult>> mutate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        if (!TryResolveSubject(http, out var userId) || !TryGetBearerToken(http, out var bearerToken))
            return Results.Unauthorized();

        try
        {
            var result = await mutate(lifecycle, bearerToken, userId, ct).ConfigureAwait(false);
            return Results.Accepted(value: new
            {
                status = result.Status,
                actor_id = result.ActorId,
                api_key_id = result.ApiKeyId,
                expires_at_unix_ms = result.ExpiresAtUnixMs,
                command_id = result.CommandId,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManagedCodexCredentialLifecycleException exception)
        {
            return Results.Json(
                new { code = exception.Code, message = exception.Message },
                statusCode: StatusFor(exception.Code));
        }
    }

    private static int StatusFor(string code) => code switch
    {
        "managed_target_disabled" or
        "managed_credential_cleanup_pending" or
        "managed_credential_persistence_pending" or
        "managed_credential_vault_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "managed_feature_not_enabled" or
        "nyxid_identity_mismatch" => StatusCodes.Status403Forbidden,
        "managed_credential_not_provisioned" => StatusCodes.Status404NotFound,
        "managed_credential_already_provisioned" or
        "managed_credential_mutation_in_progress" or
        "managed_credential_untracked_key_exists" or
        "chrono_sandbox_service_changed" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status502BadGateway,
    };

    private static string EffectiveStatus(
        ManagedCodexCredentialDescriptor credential,
        DateTimeOffset now) =>
        credential.Status == ManagedCodexCredentialStatus.Active &&
        credential.ExpiresAt is not null &&
        credential.ExpiresAt.ToDateTimeOffset() <= now
            ? "expired"
            : credential.Status.ToString().ToLowerInvariant();

    private static bool TryResolveSubject(HttpContext http, out string userId)
    {
        ArgumentNullException.ThrowIfNull(http);
        userId = string.Empty;
        if (http.User.Identity?.IsAuthenticated != true)
            return false;

        foreach (var claimType in new[]
                 {
                     "scope_id", "uid", "sub", ClaimTypes.NameIdentifier, "user_id",
                 })
        {
            var value = http.User.FindFirst(claimType)?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            userId = value;
            return true;
        }

        return false;
    }

    private static bool TryGetBearerToken(HttpContext http, out string bearerToken)
    {
        bearerToken = string.Empty;
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        bearerToken = header[prefix.Length..].Trim();
        return bearerToken.Length > 0;
    }

    private static ExternalSubjectRef Owner(string userId) => new()
    {
        Platform = OwnerScope.NyxIdPlatform,
        Tenant = string.Empty,
        ExternalUserId = userId,
    };
}
