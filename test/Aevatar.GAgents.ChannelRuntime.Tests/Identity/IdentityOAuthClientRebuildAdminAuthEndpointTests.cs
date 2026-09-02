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
using Microsoft.Extensions.Options;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// <c>POST /api/oauth/aevatar-client/rebuild</c> is gated on aevatar admin access
/// resolved through <see cref="IPlatformAdminAuthorizer"/>.
/// </summary>
public sealed class IdentityOAuthClientRebuildAdminAuthEndpointTests
{
    private const string ConfiguredClientId = "17cecaad-214b-4521-9dba-d435462e4095";
    private const string AdminBearer = "admin-bearer-token";
    private const string LegacyStaticTokenHeader = "X-Aevatar-Admin-Token";

    [Fact]
    public async Task Denies403_WhenCallerIsNotPlatformAdmin()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>();
        var authorizer = new FakePlatformAdminAuthorizer(elevated: false);

        var result = await InvokeRebuildAsync(
            authorizer: authorizer,
            bearer: AdminBearer,
            dispatch: dispatch);

        var (doc, statusCode) = await ReadJsonAsync(result);
        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rebuild_admin_required");
        dispatch.Commands.Should().BeEmpty("a non-admin caller must never reach the actor command");
        authorizer.ResolvedBearers.Should().ContainSingle().Which.Should().Be(AdminBearer);
    }

    [Fact]
    public async Task Denies403_WhenNoBearerPresent_EvenWithStaticTokenHeader()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>();
        var authorizer = new FakePlatformAdminAuthorizer(elevated: true);

        var result = await InvokeRebuildAsync(
            authorizer: authorizer,
            bearer: null,
            legacyStaticTokenHeader: "configured-but-should-not-matter",
            dispatch: dispatch);

        var (_, statusCode) = await ReadJsonAsync(result);
        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        dispatch.Commands.Should().BeEmpty();
        authorizer.ResolvedBearers.Should().BeEmpty("a blank bearer is rejected without calling the authorizer");
    }

    [Fact]
    public async Task Returns503_WhenAuthorizerMissing_EvenWithStaticTokenHeader()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>();

        var result = await InvokeRebuildAsync(
            authorizer: null,
            bearer: AdminBearer,
            legacyStaticTokenHeader: "configured-but-should-not-matter",
            dispatch: dispatch);

        var (doc, statusCode) = await ReadJsonAsync(result);
        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rebuild_admin_authorizer_unavailable");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchesProvisionCommand_WhenCallerHasAevatarAdminGrant()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>();
        var authorizer = new FakePlatformAdminAuthorizer(
            elevated: true,
            role: "user",
            grantSource: PlatformAdminGrantSources.AllowedUserId);

        var result = await InvokeRebuildAsync(
            authorizer: authorizer,
            bearer: AdminBearer,
            dispatch: dispatch);

        dispatch.Commands.Should().ContainSingle();
        dispatch.Commands[0].ClientId.Should().Be(ConfiguredClientId);
        authorizer.ResolvedBearers.Should().ContainSingle().Which.Should().Be(AdminBearer);

        var (_, statusCode) = await ReadJsonAsync(result);
        statusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    private static Task<IResult> InvokeRebuildAsync(
        FakePlatformAdminAuthorizer? authorizer,
        string? bearer,
        RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand> dispatch,
        string? legacyStaticTokenHeader = null)
    {
        var http = NewHttpContext();
        if (!string.IsNullOrEmpty(bearer))
            http.Request.Headers.Authorization = "Bearer " + bearer;
        if (legacyStaticTokenHeader is not null)
            http.Request.Headers[LegacyStaticTokenHeader] = legacyStaticTokenHeader;

        return IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildCoreAsync(
            http: http,
            clientOptions: new AevatarOAuthClientOptions { ClientId = ConfiguredClientId },
            adminAuthorizer: authorizer,
            rebuildDispatch: dispatch,
            projectionRebuildDispatch: new RecordingCommandDispatch<RebuildAevatarOAuthClientProjectionCommand>(),
            loggerFactory: NullLoggerFactory.Instance,
            ct: default);
    }

    private sealed class FakePlatformAdminAuthorizer(
        bool elevated,
        string role = "admin",
        string grantSource = PlatformAdminGrantSources.NyxIdPlatformRole) : IPlatformAdminAuthorizer
    {
        public List<string> ResolvedBearers { get; } = new();

        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            ResolvedBearers.Add(bearerToken);
            return Task.FromResult(elevated
                ? new PlatformCaller(true, role, "admin@example.com", "admin-1", grantSource)
                : PlatformCaller.NotElevated);
        }
    }

    private static async Task<(JsonDocument Document, int StatusCode)> ReadJsonAsync(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        var statusCode = context.Response.StatusCode;
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (JsonDocument.Parse(text), statusCode);
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
}
