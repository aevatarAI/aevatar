using System.Text;
using System.Text.Json;
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
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildAsync"/>.
/// </summary>
public sealed class IdentityOAuthClientRebuildEndpointTests
{
    private const string AdminToken = "test-admin-token-very-secret";
    private const string OperatorClientId = "17cecaad-214b-4521-9dba-d435462e4095";

    [Fact]
    public async Task Returns503_WhenAdminTokenNotConfigured()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: string.Empty,
            adminTokenHeader: AdminToken,
            body: SampleBody(),
            dispatch: dispatch);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rebuild_not_configured");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns401_WhenAdminTokenHeaderMissing()
    {
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: null,
            body: SampleBody(),
            dispatch: new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
                static _ => OAuthClientReceipt()));

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Returns401_WhenAdminTokenHeaderMismatch()
    {
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: "wrong-token",
            body: SampleBody(),
            dispatch: new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
                static _ => OAuthClientReceipt()));

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Returns400_WhenClientIdMissing()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
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
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
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
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
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

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("rebuild_pending");
        doc.RootElement.GetProperty("status_url").GetString().Should().Be("/api/oauth/aevatar-client/status");
    }

    [Fact]
    public async Task Returns503_WhenDispatchThrows()
    {
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
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
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
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

    // Refactor (iter27/cluster-028-identity-oauth-endpoint):
    //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
    //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
    private static Task<IResult> InvokeRebuildAsync(
        string adminTokenConfigured,
        string? adminTokenHeader,
        IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest body,
        ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch,
        CancellationToken ct = default)
    {
        var http = NewHttpContext();
        if (adminTokenHeader is not null)
            http.Request.Headers[AevatarOAuthAdminOptions.RebuildTokenHeader] = adminTokenHeader;

        var options = new StaticOptionsMonitor<AevatarOAuthAdminOptions>(
            new AevatarOAuthAdminOptions { RebuildToken = adminTokenConfigured });

        return IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildCoreAsync(
            http: http,
            body: body,
            adminOptions: options,
            rebuildDispatch: dispatch,
            loggerFactory: NullLoggerFactory.Instance,
            ct: ct);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
        where T : class
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
