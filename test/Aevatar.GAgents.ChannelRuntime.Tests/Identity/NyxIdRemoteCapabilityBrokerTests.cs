using System.Net;
using System.Text;
using System.Text.Json;
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
    private const string OAuthAuthority = "https://id.example.test";
    private const string ResourceServerBaseUrl = "https://api.example.test";
    private const string RequiredLlmServiceSlug = "chrono-llm-public";
    private const string RequiredOrnnServiceSlug = "ornn-api";
    private const string RequiredSandboxServiceSlug = "chrono-sandbox";
    private const string RequiredAevatarResource = $"{ResourceServerBaseUrl}/api/v1/proxy/s/aevatar";
    private const string RequiredLlmResource = $"{ResourceServerBaseUrl}/api/v1/proxy/s/{RequiredLlmServiceSlug}";
    private const string RequiredOrnnResource = $"{ResourceServerBaseUrl}/api/v1/proxy/s/{RequiredOrnnServiceSlug}";
    private const string RequiredSandboxResource = $"{ResourceServerBaseUrl}/api/v1/proxy/s/{RequiredSandboxServiceSlug}";
    private static readonly string[] RequiredResources =
        [RequiredAevatarResource, RequiredLlmResource, RequiredOrnnResource, RequiredSandboxResource];

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
    public async Task ExchangeAuthorizationCodeAsync_PreservesFinalizedConsentWhenMappingTokenResponse()
    {
        var handler = StubHandler.Text(HttpStatusCode.OK,
            """
            {
              "binding_id": "bnd-user",
              "access_token": "access-token",
              "refresh_token": "refresh-token",
              "id_token": "id-token",
              "token_type": "Bearer",
              "expires_in": 1800,
              "scope": "openid profile proxy"
            }
            """);
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var result = await broker.ExchangeAuthorizationCodeAsync("auth-code", "verifier");

        result.BindingId.Should().Be("bnd-user");
        result.AccessToken.Should().Be("access-token");
        result.RefreshToken.Should().Be("refresh-token");
        result.IdToken.Should().Be("id-token");
        result.TokenType.Should().Be("Bearer");
        result.ExpiresIn.Should().Be(1800);
        result.Scope.Should().Be("openid profile proxy");
        handler.LastRequestUri.Should().Be($"{OAuthAuthority}/oauth/token");
        QueryHelpers.ParseQuery($"?{handler.LastRequestBody}")
            .ContainsKey("resource").Should().BeFalse();
    }

    [Fact]
    public async Task StartExternalBindingAsync_EmitsAuthorizeUrlOnlyWhenRedirectUriMatches()
    {
        var expectedRedirectUri = NyxIdRedirectUriResolver.Resolve();
        var broker = NewBroker(NewSnapshot(expectedRedirectUri));

        var challenge = await broker.StartExternalBindingAsync(SampleSubject());

        var uri = new Uri(challenge.AuthorizeUrl);
        uri.GetLeftPart(UriPartial.Path).Should().Be($"{OAuthAuthority}/oauth/authorize");
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["client_id"].Should().ContainSingle().Which.Should().Be("client-1");
        query["redirect_uri"].Should().ContainSingle().Which.Should().Be(expectedRedirectUri);
        var scope = query["scope"].Should().ContainSingle().Which;
        scope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
        scope.Should().Contain(AevatarOAuthClientScopes.OfflineAccess);
        scope.Should().NotContain("llm:proxy", "capability scopes are requested only during token exchange");
        query["resource"].Should().Equal(RequiredResources);
        query["prompt"].Should().ContainSingle().Which.Should().Be("consent");
        query.ContainsKey("binding_grant_id").Should().BeFalse();
        query["external_subject_platform"].Should().ContainSingle().Which.Should().Be("lark");
        query["external_subject_tenant"].Should().ContainSingle().Which.Should().Be("ou_tenant_x");
        query["external_subject_external_user_id"].Should().ContainSingle().Which.Should().Be("ou_user_y");
        query["state"].Should().ContainSingle();
        query["code_challenge"].Should().ContainSingle();
        query["code_challenge_method"].Should().ContainSingle().Which.Should().Be("S256");
        challenge.RenewsExistingBinding.Should().BeFalse();
    }

    [Fact]
    public async Task StartExternalBindingAsync_RenewsExistingBindingWithoutSendingLegacyGrantId()
    {
        var expectedRedirectUri = NyxIdRedirectUriResolver.Resolve();
        var bindingId = new BindingId { Value = "bnd-secret-value" };
        var broker = NewBroker(
            NewSnapshot(expectedRedirectUri),
            queryPort: new FixedBindingQueryPort(bindingId));

        var challenge = await broker.StartExternalBindingAsync(SampleSubject());

        var query = QueryHelpers.ParseQuery(new Uri(challenge.AuthorizeUrl).Query);
        query.ContainsKey("binding_grant_id").Should().BeFalse();
        query["external_subject_platform"].Should().ContainSingle().Which.Should().Be("lark");
        query["external_subject_tenant"].Should().ContainSingle().Which.Should().Be("ou_tenant_x");
        query["external_subject_external_user_id"].Should().ContainSingle().Which.Should().Be("ou_user_y");
        challenge.AuthorizeUrl.Should().NotContain(bindingId.Value);
        challenge.RenewsExistingBinding.Should().BeTrue();

        var state = query["state"].Should().ContainSingle().Which;
        var decoded = await broker.TryDecodeStateTokenAsync(state);
        decoded.Succeeded.Should().BeTrue();
        decoded.ExpectedBindingHash.Should().Be(
            NyxIdRemoteCapabilityBroker.HashBindingId(bindingId.Value));
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_MapsInPlaceBindingUpdate()
    {
        var handler = StubHandler.Text(HttpStatusCode.OK,
            """
            {
              "binding_updated": true,
              "access_token": "access-token",
              "token_type": "Bearer",
              "expires_in": 300,
              "scope": "openid proxy"
            }
            """);
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var result = await broker.ExchangeAuthorizationCodeAsync("auth-code", "verifier");

        result.BindingUpdated.Should().BeTrue();
        result.BindingId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveBindingOwnerScopeAsync_UsesOwningClientIntrospectionForLegacyBinding()
    {
        var handler = StubHandler.Text(HttpStatusCode.OK,
            """
            {
              "binding_id": "bnd-legacy",
              "client_id": "client-1",
              "nyx_subject": "owner-user-1",
              "scopes": ["openid", "proxy"],
              "created_at": "2026-07-01T00:00:00Z",
              "revoked": false
            }
            """);
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var ownerScope = await broker.ResolveBindingOwnerScopeAsync("bnd-legacy");

        ownerScope.Should().NotBeNull();
        ownerScope!.Value.Should().Be("owner-user-1");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Be(
            $"{OAuthAuthority}/oauth/bindings/bnd-legacy?client_id=client-1");
    }

    [Fact]
    public async Task BindingFlow_PreservesCompleteConsentAcrossBrokerTokenExchange()
    {
        const string bindingId = "bnd-e2e-user";
        const string optionalResource = $"{ResourceServerBaseUrl}/api/v1/proxy/s/user-selected-service";
        var accessToken = CreateAccessToken([.. RequiredResources, optionalResource]);
        var handler = new SequenceHandler(
            JsonSerializer.Serialize(new
            {
                binding_id = bindingId,
                access_token = "authorization-code-access-token",
                refresh_token = "refresh-token",
                token_type = "Bearer",
                expires_in = 300,
                scope = AevatarOAuthClientScopes.AuthorizationScope,
            }),
            JsonSerializer.Serialize(new
            {
                access_token = accessToken,
                token_type = "Bearer",
                expires_in = 300,
                scope = AevatarOAuthClientScopes.Proxy,
            }));
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var challenge = await broker.StartExternalBindingAsync(SampleSubject());
        var authorizeQuery = QueryHelpers.ParseQuery(new Uri(challenge.AuthorizeUrl).Query);
        var decodedState = await broker.TryDecodeStateTokenAsync(
            authorizeQuery["state"].Should().ContainSingle().Which);
        var codeExchange = await broker.ExchangeAuthorizationCodeAsync(
            "authorization-code",
            decodedState.PkceVerifier!);
        var handle = await broker.IssueShortLivedByBindingIdAsync(
            SampleSubject(),
            codeExchange.BindingId!,
            new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy });

        authorizeQuery["resource"].Should().Equal(RequiredResources);
        codeExchange.BindingId.Should().Be(bindingId);
        handle.AccessToken.Should().Be(accessToken);
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(request =>
            request.Uri == $"{OAuthAuthority}/oauth/token");

        var authorizationCodeForm = QueryHelpers.ParseQuery($"?{handler.Requests[0].Body}");
        authorizationCodeForm["grant_type"].Should().ContainSingle().Which.Should().Be("authorization_code");
        authorizationCodeForm.ContainsKey("resource").Should().BeFalse();

        var bindingExchangeForm = QueryHelpers.ParseQuery($"?{handler.Requests[1].Body}");
        bindingExchangeForm["grant_type"].Should().ContainSingle().Which.Should()
            .Be("urn:ietf:params:oauth:grant-type:token-exchange");
        bindingExchangeForm["subject_token"].Should().ContainSingle().Which.Should().Be(bindingId);
        bindingExchangeForm.ContainsKey("resource").Should().BeFalse();
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
                RequiredLlmServiceSlug = RequiredLlmServiceSlug,
                AdditionalRequiredServiceSlugs = [RequiredOrnnServiceSlug, RequiredSandboxServiceSlug],
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

    [Fact]
    public async Task IssueShortLivedByBindingIdAsync_RequiresEveryConfiguredResourceInIssuedToken()
    {
        var accessToken = CreateAccessToken(RequiredResources);
        var handler = StubHandler.Text(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                access_token = accessToken,
                token_type = "Bearer",
                expires_in = 300,
                scope = "proxy",
            }));
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var result = await broker.IssueShortLivedByBindingIdAsync(
            SampleSubject(),
            "bnd-user",
            new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy });

        result.AccessToken.Should().Be(accessToken);
        QueryHelpers.ParseQuery($"?{handler.LastRequestBody}")
            .ContainsKey("resource").Should().BeFalse();
    }

    [Fact]
    public async Task IssueConnectedServiceInventoryByBindingIdAsync_DoesNotRequireAevatarRuntimeResources()
    {
        var accessToken = CreateAccessToken([RequiredAevatarResource]);
        var handler = new SequenceHandler(TokenExchangeResponse(accessToken));
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var result = await ((INyxIdConnectedServiceInventoryCapabilityIssuer)broker)
            .IssueByBindingIdAsync(
                SampleSubject(),
                "bnd-inventory-only");

        result.AccessToken.Should().Be(accessToken);
        result.Scope.Should().Be(AevatarOAuthClientScopes.Proxy);
        handler.Requests.Should().ContainSingle();
        var bindingExchangeForm = QueryHelpers.ParseQuery($"?{handler.Requests[0].Body}");
        bindingExchangeForm["scope"].Should().ContainSingle().Which.Should().Be(AevatarOAuthClientScopes.Proxy);
        bindingExchangeForm.ContainsKey("resource").Should().BeFalse();
    }

    [Fact]
    public async Task IssueRemoteSkillReadByBindingIdAsync_DoesNotRequireAevatarRuntimeResources()
    {
        var accessToken = CreateAccessToken([RequiredAevatarResource]);
        var handler = new SequenceHandler(TokenExchangeResponse(accessToken));
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var result = await ((INyxIdSkillCapabilityIssuer)broker)
            .IssueByBindingIdAsync(
                SampleSubject(),
                "bnd-skill-alpha");

        result.AccessToken.Should().Be(accessToken);
        result.Scope.Should().Be(AevatarOAuthClientScopes.Proxy);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be($"{OAuthAuthority}/oauth/token");
        var bindingExchangeForm = QueryHelpers.ParseQuery($"?{handler.Requests[0].Body}");
        bindingExchangeForm["grant_type"].Should().ContainSingle().Which.Should()
            .Be(NyxIdRemoteCapabilityBroker.TokenExchangeGrantType);
        bindingExchangeForm["subject_token"].Should().ContainSingle().Which.Should().Be("bnd-skill-alpha");
        bindingExchangeForm["scope"].Should().ContainSingle().Which.Should().Be(AevatarOAuthClientScopes.Proxy);
        bindingExchangeForm.ContainsKey("resource").Should().BeFalse();
    }

    [Fact]
    public async Task IssueShortLivedByBindingIdAsync_AcceptsAllServicesGrantFromAuthoritativeCatalog()
    {
        const string optionalResource = $"{ResourceServerBaseUrl}/api/v1/proxy/s/user-selected-service";
        var accessToken = CreateAccessToken(new { sub = "owner-user", scope = "proxy" });
        var handler = new SequenceHandler(
            TokenExchangeResponse(accessToken),
            UserServiceCatalogResponse(
                ("svc-aevatar", RequiredAevatarResource),
                ("svc-llm", RequiredLlmResource),
                ("svc-ornn", RequiredOrnnResource),
                ("svc-sandbox", RequiredSandboxResource),
                ("svc-optional", optionalResource)));
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var result = await broker.IssueShortLivedByBindingIdAsync(
            SampleSubject(),
            "bnd-all-services",
            new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy });

        result.AccessToken.Should().Be(accessToken);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Uri.Should().Be($"{ResourceServerBaseUrl}/api/v1/user-services");
        handler.Requests[1].Authorization.Should().Be($"Bearer {accessToken}");
    }

    [Fact]
    public async Task IssueShortLivedByBindingIdAsync_PreservesExplicitOptionalServiceSelection()
    {
        const string optionalResource = $"{ResourceServerBaseUrl}/api/v1/proxy/s/user-selected-service";
        var allowedServiceIds = new[]
        {
            "svc-aevatar",
            "svc-llm",
            "svc-ornn",
            "svc-sandbox",
            "svc-optional",
        };
        var accessToken = CreateAccessToken(new
        {
            resources = Array.Empty<string>(),
            allowed_service_ids = allowedServiceIds,
            allow_all_services = false,
        });
        var handler = new SequenceHandler(
            TokenExchangeResponse(accessToken),
            UserServiceCatalogResponse(
                ("svc-aevatar", RequiredAevatarResource),
                ("svc-llm", RequiredLlmResource),
                ("svc-ornn", RequiredOrnnResource),
                ("svc-sandbox", RequiredSandboxResource),
                ("svc-optional", optionalResource),
                ("svc-not-selected", $"{ResourceServerBaseUrl}/api/v1/proxy/s/not-selected")));
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var result = await broker.IssueShortLivedByBindingIdAsync(
            SampleSubject(),
            "bnd-explicit-services",
            new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy });

        result.AccessToken.Should().Be(accessToken);
        var bindingExchangeForm = QueryHelpers.ParseQuery($"?{handler.Requests[0].Body}");
        bindingExchangeForm.ContainsKey("resource").Should().BeFalse();
    }

    [Fact]
    public async Task IssueShortLivedByBindingIdAsync_RejectsUnselectedRequiredServiceFoundInCatalog()
    {
        var accessToken = CreateAccessToken(new
        {
            resources = Array.Empty<string>(),
            allowed_service_ids = new[] { "svc-aevatar", "svc-llm", "svc-sandbox" },
            allow_all_services = false,
        });
        var handler = new SequenceHandler(
            TokenExchangeResponse(accessToken),
            UserServiceCatalogResponse(
                ("svc-aevatar", RequiredAevatarResource),
                ("svc-llm", RequiredLlmResource),
                ("svc-ornn", RequiredOrnnResource),
                ("svc-sandbox", RequiredSandboxResource)));
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: handler);

        var act = () => broker.IssueShortLivedByBindingIdAsync(
            SampleSubject(),
            "bnd-missing-ornn",
            new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy });

        var exception = await act.Should().ThrowAsync<BindingServiceAccessMismatchException>();
        exception.Which.RequiredResources.Should().Equal(RequiredOrnnResource);
    }

    [Fact]
    public async Task IssueShortLivedByBindingIdAsync_RejectsBindingWithoutOrnnResource()
    {
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: StubHandler.Text(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    access_token = CreateAccessToken(
                        [RequiredAevatarResource, RequiredLlmResource, RequiredSandboxResource]),
                    token_type = "Bearer",
                    expires_in = 300,
                    scope = "proxy",
                })));

        var act = () => broker.IssueShortLivedByBindingIdAsync(
            SampleSubject(),
            "bnd-user",
            new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy });

        var exception = await act.Should().ThrowAsync<BindingServiceAccessMismatchException>();
        exception.Which.RequiredResources.Should().Equal(RequiredOrnnResource);
    }

    [Fact]
    public async Task IssueShortLivedByBindingIdAsync_MapsInvalidTargetToServiceAccessMismatch()
    {
        var broker = NewBroker(
            NewSnapshot(NyxIdRedirectUriResolver.Resolve()),
            httpHandler: StubHandler.Text(HttpStatusCode.BadRequest, """{"error":"invalid_target"}"""));

        var act = () => broker.IssueShortLivedByBindingIdAsync(
            SampleSubject(),
            "bnd-user",
            new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy });

        var exception = await act.Should().ThrowAsync<BindingServiceAccessMismatchException>();
        exception.Which.RequiredResources.Should().Equal(RequiredResources);
    }

    private static NyxIdRemoteCapabilityBroker NewBroker(
        AevatarOAuthClientSnapshot snapshot,
        NyxIdBrokerOptions? options = null,
        HttpMessageHandler? httpHandler = null,
        IExternalIdentityBindingQueryPort? queryPort = null)
    {
        var provider = new FakeOAuthClientProvider(snapshot);
        options ??= new NyxIdBrokerOptions
        {
            ResourceServerBaseUrl = $" {ResourceServerBaseUrl}/// ",
            RequiredLlmServiceSlug = RequiredLlmServiceSlug,
            AdditionalRequiredServiceSlugs = [RequiredOrnnServiceSlug, RequiredSandboxServiceSlug],
        };
        if (string.IsNullOrWhiteSpace(options.ResourceServerBaseUrl))
            options.ResourceServerBaseUrl = $" {ResourceServerBaseUrl}/// ";
        return new NyxIdRemoteCapabilityBroker(
            new FakeHttpClientFactory(httpHandler),
            provider,
            Options.Create(options),
            new StateTokenCodec(provider),
            queryPort ?? new EmptyBindingQueryPort(),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-04-30T10:00:00Z")),
            NullLogger<NyxIdRemoteCapabilityBroker>.Instance);
    }

    private static AevatarOAuthClientSnapshot NewSnapshot(string? redirectUri) => new(
        ClientId: "client-1",
        ClientIdIssuedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"),
        HmacKid: "v1",
        HmacKey: HmacKey,
        HmacKeyRotatedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"),
        NyxIdAuthority: OAuthAuthority,
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

    private static string CreateAccessToken(IReadOnlyList<string> resources) =>
        CreateAccessToken(new { resources });

    private static string CreateAccessToken(object claims)
    {
        static string Encode(object value) => Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"{Encode(new { alg = "none" })}.{Encode(claims)}.signature";
    }

    private static string TokenExchangeResponse(string accessToken) =>
        JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = 300,
            scope = "proxy",
        });

    private static string UserServiceCatalogResponse(params (string Id, string ResourceUri)[] services) =>
        JsonSerializer.Serialize(new
        {
            services = services.Select(static service => new
            {
                id = service.Id,
                resource_uri = service.ResourceUri,
            }),
        });

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

    private sealed class FixedBindingQueryPort(BindingId bindingId) : IExternalIdentityBindingQueryPort
    {
        public Task<BindingId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) => Task.FromResult<BindingId?>(bindingId);
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

        public string? LastRequestBody { get; private set; }
        public string? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        private StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public static StubHandler Text(HttpStatusCode statusCode, string body) => new(statusCode, body);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body),
            };
        }
    }

    private sealed class SequenceHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> _responseBodies = new(responseBodies);

        public List<(string? Uri, string? Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.RequestUri?.ToString(),
                body,
                request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBodies.Dequeue()),
            };
        }
    }
}
