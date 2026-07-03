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
/// M3 — <c>POST /api/oauth/aevatar-client/rebuild</c> is gated on a NyxID-verified
/// platform admin/operator role (<see cref="IPlatformAdminAuthorizer"/>) instead of
/// a static token when the authorizer is registered. These tests exercise the
/// admin-auth primary path; the static-token fallback (authorizer not registered)
/// is covered by <see cref="IdentityOAuthClientRebuildEndpointTests"/>.
/// </summary>
public sealed class IdentityOAuthClientRebuildAdminAuthEndpointTests
{
    private const string OperatorClientId = "17cecaad-214b-4521-9dba-d435462e4095";
    private const string AdminBearer = "admin-bearer-token";

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
    public async Task Denies403_WhenNoBearerPresent_EvenWithStaticTokenConfigured()
    {
        // Authorizer registered → the static token is NOT a valid bypass; a
        // missing bearer is fail-closed (the authorizer is never consulted with
        // an empty token, and PlatformCaller.NotElevated is used).
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>();
        var authorizer = new FakePlatformAdminAuthorizer(elevated: true);

        var result = await InvokeRebuildAsync(
            authorizer: authorizer,
            bearer: null,
            staticToken: "configured-but-should-not-matter",
            staticTokenHeader: "configured-but-should-not-matter",
            dispatch: dispatch);

        var (_, statusCode) = await ReadJsonAsync(result);
        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        dispatch.Commands.Should().BeEmpty();
        authorizer.ResolvedBearers.Should().BeEmpty("a blank bearer is rejected without calling the IdP");
    }

    [Fact]
    public async Task DispatchesProvisionCommand_WhenCallerIsPlatformAdmin()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>();
        var authorizer = new FakePlatformAdminAuthorizer(elevated: true);

        var result = await InvokeRebuildAsync(
            authorizer: authorizer,
            bearer: AdminBearer,
            dispatch: dispatch);

        dispatch.Commands.Should().ContainSingle();
        dispatch.Commands[0].ClientId.Should().Be(OperatorClientId);
        authorizer.ResolvedBearers.Should().ContainSingle().Which.Should().Be(AdminBearer);

        var (_, statusCode) = await ReadJsonAsync(result);
        statusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    private static Task<IResult> InvokeRebuildAsync(
        FakePlatformAdminAuthorizer authorizer,
        string? bearer,
        RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand> dispatch,
        string staticToken = "",
        string? staticTokenHeader = null)
    {
        var http = NewHttpContext();
        if (!string.IsNullOrEmpty(bearer))
            http.Request.Headers.Authorization = "Bearer " + bearer;
        if (staticTokenHeader is not null)
            http.Request.Headers[AevatarOAuthAdminOptions.RebuildTokenHeader] = staticTokenHeader;

        var options = new StaticOptionsMonitor<AevatarOAuthAdminOptions>(
            new AevatarOAuthAdminOptions { RebuildToken = staticToken });

        return IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildCoreAsync(
            http: http,
            body: new IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest(
                client_id: OperatorClientId,
                client_id_issued_at_unix: 1700000000),
            adminOptions: options,
            adminAuthorizer: authorizer,
            rebuildDispatch: dispatch,
            loggerFactory: NullLoggerFactory.Instance,
            ct: default);
    }

    private sealed class FakePlatformAdminAuthorizer(bool elevated) : IPlatformAdminAuthorizer
    {
        public List<string> ResolvedBearers { get; } = new();

        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            ResolvedBearers.Add(bearerToken);
            return Task.FromResult(elevated
                ? new PlatformCaller(true, "admin", "admin@example.com", "admin-1")
                : PlatformCaller.NotElevated);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
        where T : class
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
