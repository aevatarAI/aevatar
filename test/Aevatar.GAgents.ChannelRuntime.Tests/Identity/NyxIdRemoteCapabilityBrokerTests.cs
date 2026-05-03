using System.Net;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

[Collection(NyxIdRedirectUriEnvCollection.Name)]
public sealed class NyxIdRemoteCapabilityBrokerTests : IDisposable
{
    private static readonly byte[] HmacKey =
        Convert.FromHexString("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    private readonly string? _savedOverride;

    public NyxIdRemoteCapabilityBrokerTests()
    {
        _savedOverride = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar);
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, _savedOverride);
    }

    [Fact]
    public async Task StartExternalBindingAsync_RejectsSnapshotWithMissingRedirectUri()
    {
        var broker = NewBroker(NewSnapshot(redirectUri: null));

        var act = () => broker.StartExternalBindingAsync(SampleSubject());

        await act.Should()
            .ThrowAsync<AevatarOAuthClientNotProvisionedException>()
            .WithMessage("*redirect_uri*");
    }

    [Fact]
    public async Task StartExternalBindingAsync_RejectsSnapshotWithMismatchedRedirectUri()
    {
        var broker = NewBroker(NewSnapshot("https://old.example.com/api/oauth/nyxid-callback"));

        var act = () => broker.StartExternalBindingAsync(SampleSubject());

        await act.Should()
            .ThrowAsync<AevatarOAuthClientNotProvisionedException>()
            .WithMessage("*redirect_uri*");
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_RejectsSnapshotWithMissingRedirectUri()
    {
        // The token-exchange path has the same EnsureRedirectUriCurrent
        // guard as the authorize path. A code in flight is the higher-
        // impact failure mode — the user already clicked the broker URL
        // and NyxID has issued the code. If the code hits the broker with
        // a stale snapshot, redirect_uri at /oauth/token would diverge
        // from what NyxID recorded at /authorize and the exchange would
        // fail with `invalid_grant`. Pin the early refusal so we never
        // burn an authorization code against a known-stale snapshot.
        var broker = NewBroker(NewSnapshot(redirectUri: null));

        var act = () => broker.ExchangeAuthorizationCodeAsync("auth-code", "verifier");

        await act.Should()
            .ThrowAsync<AevatarOAuthClientNotProvisionedException>()
            .WithMessage("*redirect_uri*");
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_RejectsSnapshotWithMismatchedRedirectUri()
    {
        var broker = NewBroker(NewSnapshot("https://old.example.com/api/oauth/nyxid-callback"));

        var act = () => broker.ExchangeAuthorizationCodeAsync("auth-code", "verifier");

        await act.Should()
            .ThrowAsync<AevatarOAuthClientNotProvisionedException>()
            .WithMessage("*redirect_uri*");
    }

    [Fact]
    public async Task StartExternalBindingAsync_EmitsAuthorizeUrlOnlyWhenRedirectUriMatches()
    {
        var expectedRedirectUri = NyxIdRedirectUriResolver.Resolve();
        var broker = NewBroker(NewSnapshot(expectedRedirectUri));

        var challenge = await broker.StartExternalBindingAsync(SampleSubject());

        var uri = new Uri(challenge.AuthorizeUrl);
        uri.GetLeftPart(UriPartial.Path).Should().Be("https://nyxid.test/oauth/authorize");
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["client_id"].Should().ContainSingle().Which.Should().Be("client-1");
        query["redirect_uri"].Should().ContainSingle().Which.Should().Be(expectedRedirectUri);
        query["scope"].Should().ContainSingle().Which.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
        query["state"].Should().ContainSingle();
        query["code_challenge"].Should().ContainSingle();
        query["code_challenge_method"].Should().ContainSingle().Which.Should().Be("S256");
    }

    [Fact]
    public async Task StartExternalBindingAsync_UsesCanonicalScope_WhenOptionsScopeAddsExtraScopes()
    {
        var expectedRedirectUri = NyxIdRedirectUriResolver.Resolve();
        var broker = NewBroker(
            NewSnapshot(expectedRedirectUri),
            options: new NyxIdBrokerOptions
            {
                Scope = $"{AevatarOAuthClientScopes.AuthorizationScope} email",
            });

        var challenge = await broker.StartExternalBindingAsync(SampleSubject());

        var query = QueryHelpers.ParseQuery(new Uri(challenge.AuthorizeUrl).Query);
        query["scope"].Should().ContainSingle().Which.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
    }

    [Fact]
    public async Task IssueShortLivedByBindingIdAsync_ThrowsScopeMismatch_WhenNyxIdReturnsInvalidScope()
    {
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: StubHandler.Text(HttpStatusCode.BadRequest, """{"error":"invalid_scope"}"""));

        var act = () => broker.IssueShortLivedByBindingIdAsync(
            SampleSubject(),
            "bnd-user",
            new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy });

        await act.Should().ThrowAsync<BindingScopeMismatchException>();
    }

    private static NyxIdRemoteCapabilityBroker NewBroker(
        AevatarOAuthClientSnapshot snapshot,
        NyxIdBrokerOptions? options = null,
        HttpMessageHandler? httpHandler = null)
    {
        var provider = new FakeOAuthClientProvider(snapshot);
        return new NyxIdRemoteCapabilityBroker(
            new FakeHttpClientFactory(httpHandler),
            provider,
            Options.Create(options ?? new NyxIdBrokerOptions()),
            new StateTokenCodec(provider),
            new EmptyBindingQueryPort(),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-04-30T10:00:00Z")),
            NullLogger<NyxIdRemoteCapabilityBroker>.Instance);
    }

    private static AevatarOAuthClientSnapshot NewSnapshot(string? redirectUri) => new(
        ClientId: "client-1",
        ClientIdIssuedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"),
        HmacKid: "v1",
        HmacKey: HmacKey,
        HmacKeyRotatedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"),
        NyxIdAuthority: "https://nyxid.test",
        BrokerCapabilityObserved: true,
        BrokerCapabilityObservedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"),
        RedirectUri: redirectUri,
        OauthScope: AevatarOAuthClientScopes.AuthorizationScope);

    private static ExternalSubjectRef SampleSubject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    private sealed class FakeOAuthClientProvider : IAevatarOAuthClientProvider
    {
        private readonly AevatarOAuthClientSnapshot _snapshot;

        public FakeOAuthClientProvider(AevatarOAuthClientSnapshot snapshot) => _snapshot = snapshot;

        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(_snapshot);
    }

    private sealed class EmptyBindingQueryPort : IExternalIdentityBindingQueryPort
    {
        public Task<BindingId?> ResolveAsync(ExternalSubjectRef externalSubject, CancellationToken ct = default) =>
            Task.FromResult<BindingId?>(null);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler? _handler;

        public FakeHttpClientFactory(HttpMessageHandler? handler = null) => _handler = handler;

        public HttpClient CreateClient(string name) => _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        private StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public static StubHandler Text(HttpStatusCode statusCode, string body) => new(statusCode, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
