using System.Text;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildAsync"/>.
/// </summary>
public sealed class IdentityOAuthClientRebuildEndpointTests
{
    private const string OperatorClientId = "17cecaad-214b-4521-9dba-d435462e4095";
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
            body: SampleBody(),
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
            body: SampleBody(),
            dispatch: dispatch);

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns400_WhenClientIdMissing()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            body: new IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest(
                client_id: null,
                client_id_issued_at_unix: null),
            dispatch: dispatch);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("client_id_required");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns400_WhenIssuedAtUnixOutOfRange()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            body: new IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest(
                client_id: OperatorClientId,
                client_id_issued_at_unix: long.MaxValue),
            dispatch: dispatch);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("client_id_issued_at_unix_invalid");
        dispatch.Commands.Should().BeEmpty(
            "rejected request must not dispatch the actor command");
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
            body: new IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest(
                client_id: OperatorClientId,
                client_id_issued_at_unix: 1700000000),
            dispatch: dispatch);

        dispatch.Commands.Should().ContainSingle();
        var cmd = dispatch.Commands[0];
        cmd.ClientId.Should().Be(OperatorClientId);
        cmd.ClientIdIssuedAtUnix.Should().Be(1700000000);
        cmd.RedirectUri.Should().Be(NyxIdRedirectUriResolver.Resolve());
        cmd.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
        cmd.NyxidAuthority.Should().NotBeNullOrWhiteSpace();
        cmd.DefaultServiceCatalogSlugs.Should().Equal(
            "aevatar",
            "chrono-llm-public",
            "ornn-api",
            "chrono-sandbox");

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
    public async Task Returns503_WhenDispatchThrows()
    {
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            body: SampleBody(),
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
            body: SampleBody(),
            dispatch: new RejectingCommandDispatch<ProvisionAevatarOAuthClientCommand>());

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_rejected");
    }

    private static IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest SampleBody() =>
        new(
            client_id: OperatorClientId,
            client_id_issued_at_unix: 1700000000);

    private static Task<IResult> InvokeRebuildAsync(
        IPlatformAdminAuthorizer? authorizer,
        string? bearer,
        IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest body,
        ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch,
        string? legacyStaticTokenHeader = null,
        CancellationToken ct = default)
    {
        var http = NewHttpContext();
        if (!string.IsNullOrEmpty(bearer))
            http.Request.Headers.Authorization = "Bearer " + bearer;
        if (legacyStaticTokenHeader is not null)
            http.Request.Headers[LegacyStaticTokenHeader] = legacyStaticTokenHeader;

        return IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildCoreAsync(
            http: http,
            body: body,
            adminAuthorizer: authorizer,
            brokerOptions: BrokerOptions(),
            rebuildDispatch: dispatch,
            loggerFactory: NullLoggerFactory.Instance,
            ct: ct);
    }

    private static IOptions<NyxIdBrokerOptions> BrokerOptions() =>
        Options.Create(new NyxIdBrokerOptions
        {
            RequiredLlmServiceSlug = "chrono-llm-public",
            AdditionalRequiredServiceSlugs = ["ornn-api", "chrono-sandbox"],
        });

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
