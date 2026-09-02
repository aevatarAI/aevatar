using System.Text;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildAsync"/>.
/// </summary>
public sealed class IdentityOAuthClientRebuildEndpointTests
{
    private const string ConfiguredClientId = "17cecaad-214b-4521-9dba-d435462e4095";
    private const string AdminBearer = "admin-bearer-token";
    private const string LegacyStaticTokenHeader = "X-Aevatar-Admin-Token";

    [Fact]
    public async Task Returns503_WhenAuthorizerMissing()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var result = await InvokeRebuildAsync(
            authorizer: null,
            bearer: AdminBearer,
            dispatch: dispatch);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rebuild_admin_authorizer_unavailable");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns403_WhenLegacyStaticTokenHeaderIsPresentedWithoutBearer()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: null,
            legacyStaticTokenHeader: "legacy-token",
            dispatch: dispatch);

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns503_WhenConfiguredClientIdMissing()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            dispatch: dispatch,
            configuredClientId: "  ");

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        doc.RootElement.GetProperty("error").GetString().Should().Be("oauth_client_id_not_configured");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchesProvisionCommand_WithCanonicalSnapshotAndReturnsAccepted()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(
                elevated: true,
                role: "user",
                grantSource: PlatformAdminGrantSources.AllowedEmail),
            bearer: AdminBearer,
            dispatch: dispatch);

        dispatch.Commands.Should().ContainSingle();
        var cmd = dispatch.Commands[0];
        cmd.ClientId.Should().Be(ConfiguredClientId);
        cmd.ClientIdIssuedAtUnix.Should().BeGreaterThan(0);
        cmd.RedirectUri.Should().Be(NyxIdRedirectUriResolver.Resolve());
        cmd.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
        cmd.NyxidAuthority.Should().NotBeNullOrWhiteSpace();

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("rebuild_pending");
        doc.RootElement.GetProperty("status_url").GetString().Should().Be("/api/oauth/aevatar-client/status");
        doc.RootElement.GetProperty("admin_grant_source").GetString().Should().Be(PlatformAdminGrantSources.AllowedEmail);
    }

    [Fact]
    public async Task DispatchesProjectionRebuild_AfterProvisionReconciliation()
    {
        var projectionDispatch = new RecordingCommandDispatch<RebuildAevatarOAuthClientProjectionCommand>(
            static _ => new ChannelIdentityOAuthAcceptedReceipt(
                ActorId: AevatarOAuthClientGAgent.WellKnownId,
                CommandId: "cmd-2",
                CorrelationId: "cmd-2"));
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            dispatch: new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
                static _ => OAuthClientReceipt()),
            projectionRebuildDispatch: projectionDispatch);

        projectionDispatch.Commands.Should().ContainSingle(
            "a same-snapshot reconciliation appends no event, so a wiped readmodel is only rebuilt by the explicit projection command");

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("projection_rebuild_command_id").GetString().Should().Be("cmd-2");
    }

    [Fact]
    public async Task Returns503_WhenProjectionRebuildDispatchRejects()
    {
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            dispatch: new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
                static _ => OAuthClientReceipt()),
            projectionRebuildDispatch: new RejectingCommandDispatch<RebuildAevatarOAuthClientProjectionCommand>());

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        doc.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_rejected");
    }

    [Fact]
    public async Task Returns503_WhenDispatchThrows()
    {
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            dispatch: new ThrowingCommandDispatch<ProvisionAevatarOAuthClientCommand>());

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Returns503_WhenDispatchRejects()
    {
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            dispatch: new RejectingCommandDispatch<ProvisionAevatarOAuthClientCommand>());

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_rejected");
    }

    private static Task<IResult> InvokeRebuildAsync(
        IPlatformAdminAuthorizer? authorizer,
        string? bearer,
        ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch,
        ICommandDispatchService<RebuildAevatarOAuthClientProjectionCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>? projectionRebuildDispatch = null,
        string? legacyStaticTokenHeader = null,
        string configuredClientId = ConfiguredClientId,
        CancellationToken ct = default)
    {
        var http = NewHttpContext();
        if (!string.IsNullOrEmpty(bearer))
            http.Request.Headers.Authorization = "Bearer " + bearer;
        if (legacyStaticTokenHeader is not null)
            http.Request.Headers[LegacyStaticTokenHeader] = legacyStaticTokenHeader;

        return IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildCoreAsync(
            http: http,
            clientOptions: new AevatarOAuthClientOptions { ClientId = configuredClientId },
            adminAuthorizer: authorizer,
            rebuildDispatch: dispatch,
            projectionRebuildDispatch: projectionRebuildDispatch
                ?? new RecordingCommandDispatch<RebuildAevatarOAuthClientProjectionCommand>(
                    static _ => OAuthClientReceipt()),
            loggerFactory: NullLoggerFactory.Instance,
            ct: ct);
    }

    private sealed class FakePlatformAdminAuthorizer(
        bool elevated,
        string role = "admin",
        string grantSource = PlatformAdminGrantSources.NyxIdPlatformRole) : IPlatformAdminAuthorizer
    {
        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            return Task.FromResult(elevated
                ? new PlatformCaller(true, role, "admin@example.com", "admin-1", grantSource)
                : PlatformCaller.NotElevated);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return JsonDocument.Parse(text);
    }

    private static async Task<(JsonDocument Document, int StatusCode)> ReadJsonWithStatusAsync(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (JsonDocument.Parse(text), context.Response.StatusCode);
    }

    private static HttpContext NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = provider,
            Response =
            {
                Body = new MemoryStream(),
            },
        };
    }

    private static ChannelIdentityOAuthAcceptedReceipt OAuthClientReceipt() =>
        new(
            ActorId: AevatarOAuthClientGAgent.WellKnownId,
            CommandId: "cmd-1",
            CorrelationId: "cmd-1");
}
