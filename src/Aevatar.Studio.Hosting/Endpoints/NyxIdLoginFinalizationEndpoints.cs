using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgentService.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Hosting.Endpoints;

public static class NyxIdLoginFinalizationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/nyxid/config", HandleConfigAsync)
            .WithTags("Auth")
            .AllowAnonymous()
            .Produces<NyxIdLoginConfigurationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/auth/nyxid/finalize", HandleFinalizeAsync)
            .WithTags("Auth")
            .AllowAnonymous()
            .Produces<NyxIdLoginFinalizationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<IResult> HandleConfigAsync(
        [FromServices] IAevatarOAuthClientProvider oauthClientProvider,
        CancellationToken ct = default)
    {
        try
        {
            var snapshot = await oauthClientProvider.GetAsync(ct).ConfigureAwait(false);
            return Results.Ok(new NyxIdLoginConfigurationResponse(
                BaseUrl: snapshot.NyxIdAuthority.TrimEnd('/'),
                ClientId: snapshot.ClientId,
                Scope: string.IsNullOrWhiteSpace(snapshot.OauthScope)
                    ? AevatarOAuthClientScopes.AuthorizationScope
                    : snapshot.OauthScope.Trim(),
                RedirectUri: ResolveStudioLoginRedirectUri(snapshot)));
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            return Results.Json(new
            {
                error = "oauth_client_not_provisioned",
                detail = "Aevatar OAuth client has not been provisioned at NyxID yet.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static async Task<IResult> HandleFinalizeAsync(
        NyxIdLoginFinalizationRequest request,
        [FromServices] INyxIdBrokerCallbackClient brokerCallback,
        [FromServices] IAevatarOAuthClientProvider oauthClientProvider,
        [FromServices] IExternalIdentityBindingQueryPort bindingQueryPort,
        [FromServices] ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var logger = loggerFactory.CreateLogger("Aevatar.Studio.NyxIdLoginFinalization");

        if (string.IsNullOrWhiteSpace(request.Code))
            return Results.BadRequest(new { error = "code_missing" });
        if (string.IsNullOrWhiteSpace(request.CodeVerifier))
            return Results.BadRequest(new { error = "code_verifier_missing" });
        if (string.IsNullOrWhiteSpace(request.RedirectUri))
            return Results.BadRequest(new { error = "redirect_uri_missing" });

        string expectedRedirectUri;
        try
        {
            expectedRedirectUri = ResolveStudioLoginRedirectUri(await oauthClientProvider.GetAsync(ct).ConfigureAwait(false));
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            return Results.Json(new
            {
                error = "oauth_client_not_provisioned",
                detail = "Aevatar OAuth client has not been provisioned at NyxID yet.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var postedRedirectUri = request.RedirectUri.Trim();
        if (!string.Equals(postedRedirectUri, expectedRedirectUri, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "NyxID login finalization rejected redirect URI mismatch: posted='{Posted}', expected='{Expected}'.",
                postedRedirectUri,
                expectedRedirectUri);
            return Results.BadRequest(new
            {
                error = "redirect_uri_mismatch",
                detail = "NyxID login redirect_uri does not match the registered Studio login callback for the broker client.",
            });
        }

        BrokerAuthorizationCodeResult exchange;
        try
        {
            exchange = await brokerCallback
                .ExchangeAuthorizationCodeAsync(request.Code.Trim(), request.CodeVerifier.Trim(), postedRedirectUri, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID login finalization authorization-code exchange failed.");
            return Results.Json(new
            {
                error = "token_exchange_failed",
                detail = "NyxID login finalization failed during authorization-code exchange.",
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        if (string.IsNullOrWhiteSpace(exchange.BindingId))
        {
            return Results.Json(new
            {
                error = "broker_capability_disabled",
                detail = "NyxID did not return a broker binding id for this login.",
            }, statusCode: StatusCodes.Status409Conflict);
        }

        if (string.IsNullOrWhiteSpace(exchange.AccessToken))
        {
            return Results.Json(new
            {
                error = "access_token_missing",
                detail = "NyxID did not return an access token for this login.",
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        var user = ResolveUserInfo(exchange.IdToken);
        if (string.IsNullOrWhiteSpace(user.Sub))
        {
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = "subject_missing",
                detail = "NyxID login finalization could not resolve a stable user subject.",
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        var subject = new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = user.Sub.Trim(),
        };

        var existingBinding = await bindingQueryPort.ResolveAsync(subject, ct).ConfigureAwait(false);
        if (existingBinding != null)
        {
            if (!string.Equals(existingBinding.Value, exchange.BindingId, StringComparison.Ordinal))
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);

            return Results.Ok(BuildResponse(exchange, user, bindingDispatchAccepted: false));
        }

        CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> accepted;
        try
        {
            accepted = await bindingDispatch.DispatchAsync(new CommitBindingCommand
            {
                ExternalSubject = subject,
                BindingId = exchange.BindingId.Trim(),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID login finalization failed to dispatch CommitBindingCommand for {Platform}:{Tenant}:{User}.",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = "actor_dispatch_failed",
                detail = "NyxID owner binding could not be queued for local persistence.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!accepted.Succeeded || accepted.Receipt == null)
        {
            logger.LogError("NyxID login finalization binding dispatch rejected: error={Error}.", accepted.Error);
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = "actor_dispatch_rejected",
                detail = "NyxID owner binding was rejected by the local persistence queue.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(BuildResponse(exchange, user, bindingDispatchAccepted: true));
    }

    private static NyxIdLoginFinalizationResponse BuildResponse(
        BrokerAuthorizationCodeResult exchange,
        NyxIdFinalizedUserInfo user,
        bool bindingDispatchAccepted) =>
        new(
            Tokens: new NyxIdFinalizedTokenSet(
                AccessToken: exchange.AccessToken ?? string.Empty,
                TokenType: string.IsNullOrWhiteSpace(exchange.TokenType) ? "Bearer" : exchange.TokenType,
                ExpiresIn: exchange.ExpiresIn ?? 3600,
                IdToken: exchange.IdToken,
                Scope: exchange.Scope),
            User: user,
            BindingDispatchAccepted: bindingDispatchAccepted);

    private static NyxIdFinalizedUserInfo ResolveUserInfo(string? idToken)
    {
        var payload = TryReadJwtPayload(idToken);
        return new NyxIdFinalizedUserInfo(
            Sub: ReadString(payload, "uid") ?? ReadString(payload, "sub") ?? string.Empty,
            Email: ReadString(payload, "email"),
            EmailVerified: ReadBool(payload, "email_verified"),
            Name: ReadString(payload, "name"),
            Picture: ReadString(payload, "picture"),
            Roles: ReadStringArray(payload, "roles"),
            Groups: ReadStringArray(payload, "groups"),
            Permissions: ReadStringArray(payload, "permissions"));
    }

    private static JsonElement? TryReadJwtPayload(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return null;

        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? payload, string propertyName)
    {
        if (payload is not { } element)
            return null;
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement? payload, string propertyName)
    {
        if (payload is not { } element)
            return null;
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement? payload, string propertyName)
    {
        if (payload is not { } element)
            return null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return null;

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                values.Add(item.GetString()!);
        }

        return values;
    }

    private static async Task TryRevokeOrphanBindingAsync(
        INyxIdBrokerCallbackClient brokerCallback,
        string bindingId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await brokerCallback.RevokeBindingByIdAsync(bindingId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to revoke orphan NyxID login binding {BindingId}.", bindingId);
        }
    }

    private static string ResolveStudioLoginRedirectUri(AevatarOAuthClientSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.StudioLoginRedirectUri))
        {
            throw new AevatarOAuthClientNotProvisionedException(
                "Aevatar OAuth client is missing the registered Studio login redirect URI. Bootstrap must re-run DCR.");
        }

        return snapshot.StudioLoginRedirectUri.Trim();
    }
}

public sealed record NyxIdLoginConfigurationResponse(
    string BaseUrl,
    string ClientId,
    string Scope,
    string RedirectUri);

public sealed record NyxIdLoginFinalizationRequest
{
    public string? Code { get; init; }
    public string? CodeVerifier { get; init; }
    public string? RedirectUri { get; init; }
}

public sealed record NyxIdLoginFinalizationResponse(
    NyxIdFinalizedTokenSet Tokens,
    NyxIdFinalizedUserInfo User,
    bool BindingDispatchAccepted);

public sealed record NyxIdFinalizedTokenSet(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string? IdToken,
    string? Scope);

public sealed record NyxIdFinalizedUserInfo(
    string Sub,
    string? Email = null,
    bool? EmailVerified = null,
    string? Name = null,
    string? Picture = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Groups = null,
    IReadOnlyList<string>? Permissions = null);
