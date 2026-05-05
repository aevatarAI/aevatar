using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildAsync"/>.
/// Pins issue #549 operator-rebuild path: ops calls this endpoint with a
/// freshly-created (NyxID admin) client_id to heal a wedged cluster
/// without DB access. The endpoint must (a) refuse fail-secure when no
/// admin token is configured, (b) reject without a matching token, (c)
/// validate body fields, (d) dispatch ProvisionAevatarOAuthClientCommand
/// with the canonical redirect_uri + oauth_scope (operator cannot override
/// — see PR #570 review), and (e) wait for the readmodel to reflect the
/// pin before declaring success.
/// </summary>
public sealed class IdentityOAuthClientRebuildEndpointTests
{
    private const string AdminToken = "test-admin-token-very-secret";
    private const string OperatorClientId = "17cecaad-214b-4521-9dba-d435462e4095";

    [Fact]
    public async Task Returns503_WhenAdminTokenNotConfigured()
    {
        var (provider, runtime) = NewProviderReflectingDispatch();
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: string.Empty,
            adminTokenHeader: AdminToken,
            body: SampleBody(),
            provider: provider,
            actorRuntime: runtime);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rebuild_not_configured");
    }

    [Fact]
    public async Task Returns401_WhenAdminTokenHeaderMissing()
    {
        var (provider, runtime) = NewProviderReflectingDispatch();
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: null,
            body: SampleBody(),
            provider: provider,
            actorRuntime: runtime);

        // Results.Unauthorized() renders to status 401.
        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Returns401_WhenAdminTokenHeaderMismatch()
    {
        var (provider, runtime) = NewProviderReflectingDispatch();
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: "wrong-token",
            body: SampleBody(),
            provider: provider,
            actorRuntime: runtime);

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Returns400_WhenClientIdMissing()
    {
        var (provider, runtime) = NewProviderReflectingDispatch();
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
            body: new IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest(
                client_id: null,
                client_id_issued_at_unix: null),
            provider: provider,
            actorRuntime: runtime);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("client_id_required");
    }

    [Fact]
    public async Task Returns400_WhenIssuedAtUnixOutOfRange()
    {
        // Pin codex P1: AevatarOAuthClientProjectionProvider.GetAsync
        // calls DateTimeOffset.FromUnixTimeSeconds on the persisted value
        // and throws ArgumentOutOfRangeException for values like
        // long.MaxValue. The endpoint must surface the bad input as 400
        // here so the read path does not crash on the next status poll.
        var (provider, runtime) = NewProviderReflectingDispatch();
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
            body: new IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest(
                client_id: OperatorClientId,
                client_id_issued_at_unix: long.MaxValue),
            provider: provider,
            actorRuntime: runtime);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("client_id_issued_at_unix_invalid");
        runtime.Captured.Should().BeEmpty(
            "rejected request must not dispatch the actor command");
    }

    [Fact]
    public async Task DispatchesProvisionCommand_WithCanonicalSnapshot()
    {
        var (provider, runtime) = NewProviderReflectingDispatch();
        var result = await InvokeRebuildAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
            body: new IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest(
                client_id: OperatorClientId,
                client_id_issued_at_unix: 1700000000),
            provider: provider,
            actorRuntime: runtime);

        runtime.Captured.Should().HaveCount(1);
        var envelope = runtime.Captured[0];
        envelope.Route.Direct.TargetActorId.Should().Be(AevatarOAuthClientGAgent.WellKnownId);
        var cmd = envelope.Payload.Unpack<ProvisionAevatarOAuthClientCommand>();
        cmd.ClientId.Should().Be(OperatorClientId);
        cmd.ClientIdIssuedAtUnix.Should().Be(1700000000);
        // Endpoint always uses the resolver / canonical scope — operator
        // cannot override, otherwise the next bootstrap pass would observe
        // drift and re-DCR the pinned client (PR #570 review consensus).
        cmd.RedirectUri.Should().Be(NyxIdRedirectUriResolver.Resolve());
        cmd.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
        cmd.NyxidAuthority.Should().NotBeNullOrWhiteSpace();

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("rebuilt");
        doc.RootElement.GetProperty("client_id").GetString().Should().Be(OperatorClientId);
    }

    [Fact]
    public async Task Returns202_WhenReadmodelDoesNotReflectRebuildBeforeTimeout()
    {
        // Provider always returns the OLD snapshot — readmodel never
        // catches up. Endpoint must report rebuild_pending_propagation
        // instead of waiting forever. Production budget is 15s; the test
        // tightens it via the CoreAsync seam so the assertion runs in
        // sub-second wall time.
        var provider = Substitute.For<IAevatarOAuthClientProvider>();
        provider.GetAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StaleSnapshot()));
        var runtime = new RecordingActorRuntime();
        var result = await InvokeRebuildCoreAsync(
            adminTokenConfigured: AdminToken,
            adminTokenHeader: AdminToken,
            body: SampleBody(),
            provider: provider,
            actorRuntime: runtime,
            observationTimeout: TimeSpan.FromMilliseconds(150),
            observationPollDelay: TimeSpan.FromMilliseconds(20));

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("rebuild_pending_propagation");
        // Pin mimo P1: even the timeout path must have dispatched the
        // command — otherwise a regression that drops the dispatch could
        // pass with a stale provider and never trigger this assertion.
        runtime.Captured.Should().HaveCount(1,
            "timeout path must still have dispatched the provision command before the wait loop began");
    }

    // ─── Test plumbing ───

    private static IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest SampleBody() =>
        new(
            client_id: OperatorClientId,
            client_id_issued_at_unix: 1700000000);

    private static AevatarOAuthClientSnapshot SuccessSnapshotFor(
        string clientId,
        string redirectUri,
        string oauthScope) =>
        new(
            ClientId: clientId,
            ClientIdIssuedAt: DateTimeOffset.FromUnixTimeSeconds(1700000000),
            HmacKid: AevatarOAuthClientGAgent.InitialHmacKid,
            HmacKey: new byte[32],
            HmacKeyRotatedAt: DateTimeOffset.UtcNow,
            NyxIdAuthority: NyxIdAuthorityResolver.Resolve(),
            BrokerCapabilityObserved: true,
            BrokerCapabilityObservedAt: DateTimeOffset.UtcNow,
            PreviousHmacKid: null,
            PreviousHmacKey: null,
            PreviousHmacDemotedAt: null,
            RedirectUri: redirectUri,
            OauthScope: oauthScope);

    private static AevatarOAuthClientSnapshot StaleSnapshot() =>
        new(
            ClientId: "stale-old-client",
            ClientIdIssuedAt: DateTimeOffset.FromUnixTimeSeconds(1600000000),
            HmacKid: AevatarOAuthClientGAgent.InitialHmacKid,
            HmacKey: new byte[32],
            HmacKeyRotatedAt: DateTimeOffset.UtcNow,
            NyxIdAuthority: NyxIdAuthorityResolver.Resolve(),
            BrokerCapabilityObserved: false,
            BrokerCapabilityObservedAt: null,
            PreviousHmacKid: null,
            PreviousHmacKey: null,
            PreviousHmacDemotedAt: null,
            RedirectUri: "https://stale.example.com/callback",
            OauthScope: "openid");

    private static (IAevatarOAuthClientProvider Provider, RecordingActorRuntime Runtime) NewProviderReflectingDispatch()
    {
        var runtime = new RecordingActorRuntime();
        var provider = Substitute.For<IAevatarOAuthClientProvider>();
        provider.GetAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (runtime.Captured.Count == 0)
                    return Task.FromResult(StaleSnapshot());
                var cmd = runtime.Captured[^1].Payload.Unpack<ProvisionAevatarOAuthClientCommand>();
                return Task.FromResult(SuccessSnapshotFor(cmd.ClientId, cmd.RedirectUri, cmd.OauthScope));
            });
        return (provider, runtime);
    }

    private static AevatarOAuthClientProjectionPort NewProjectionPort()
    {
        var activationService = Substitute.For<IProjectionScopeActivationService<AevatarOAuthClientMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<AevatarOAuthClientMaterializationRuntimeLease?>(
                new AevatarOAuthClientMaterializationRuntimeLease(
                    new AevatarOAuthClientMaterializationContext
                    {
                        RootActorId = AevatarOAuthClientGAgent.WellKnownId,
                        ProjectionKind = AevatarOAuthClientProjectionPort.ProjectionKind,
                    }))!);
        return new AevatarOAuthClientProjectionPort(activationService);
    }

    /// <summary>
    /// Wraps NSubstitute-built IActorRuntime so test assertions can read the
    /// captured envelope without re-querying NSubstitute call queues.
    /// </summary>
    private sealed class RecordingActorRuntime
    {
        public List<EventEnvelope> Captured { get; } = new();
        public IActorRuntime Runtime { get; }

        public RecordingActorRuntime()
        {
            var actor = Substitute.For<IActor>();
            actor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    Captured.Add(callInfo.Arg<EventEnvelope>());
                    return Task.CompletedTask;
                });
            Runtime = Substitute.For<IActorRuntime>();
            Runtime.CreateAsync<AevatarOAuthClientGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IActor>(actor));
        }
    }

    private static Task<IResult> InvokeRebuildAsync(
        string adminTokenConfigured,
        string? adminTokenHeader,
        IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest body,
        IAevatarOAuthClientProvider provider,
        RecordingActorRuntime actorRuntime,
        CancellationToken ct = default) =>
        InvokeRebuildCoreAsync(
            adminTokenConfigured,
            adminTokenHeader,
            body,
            provider,
            actorRuntime,
            // Default budget is generous: happy-path tests exit on the
            // first provider poll; only the 202 test cares about timeout.
            observationTimeout: TimeSpan.FromSeconds(2),
            observationPollDelay: TimeSpan.FromMilliseconds(20),
            ct);

    private static async Task<IResult> InvokeRebuildCoreAsync(
        string adminTokenConfigured,
        string? adminTokenHeader,
        IdentityOAuthEndpoints.RebuildAevatarOAuthClientRequest body,
        IAevatarOAuthClientProvider provider,
        RecordingActorRuntime actorRuntime,
        TimeSpan observationTimeout,
        TimeSpan observationPollDelay,
        CancellationToken ct = default)
    {
        var http = NewHttpContext();
        if (adminTokenHeader is not null)
            http.Request.Headers[AevatarOAuthAdminOptions.RebuildTokenHeader] = adminTokenHeader;

        var options = Options.Create(new AevatarOAuthAdminOptions { RebuildToken = adminTokenConfigured });
        var projectionPort = NewProjectionPort();

        return await IdentityOAuthEndpoints.HandleAevatarOAuthClientRebuildCoreAsync(
            http: http,
            body: body,
            adminOptions: options,
            provider: provider,
            projectionPort: projectionPort,
            actorRuntime: actorRuntime.Runtime,
            loggerFactory: NullLoggerFactory.Instance,
            observationTimeout: observationTimeout,
            observationPollDelay: observationPollDelay,
            ct: ct);
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
}
