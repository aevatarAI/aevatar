using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Capabilities.Tests;

public sealed class ResponsesCallerScopeResolverTests
{
    [Fact]
    public void Constructor_ShouldRejectNullResolver()
    {
        using var fixture = new IdentityAssertionFixture();

        ((Action)(() => new NyxIdResponsesCallerScopeResolver(null!, fixture.CreateValidator())))
            .Should().Throw<ArgumentNullException>()
            .WithMessage("*currentUserResolver*");
    }

    [Fact]
    public void Constructor_ShouldRejectNullIdentityAssertionValidator()
    {
        ((Action)(() => new NyxIdResponsesCallerScopeResolver(new StubUserResolver(returnUserId: "user-1"), null!)))
            .Should().Throw<ArgumentNullException>()
            .WithMessage("*identityAssertionValidator*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenAccessTokenMissing()
    {
        using var fixture = new IdentityAssertionFixture();
        var resolver = new NyxIdResponsesCallerScopeResolver(
            new StubUserResolver(returnUserId: "user-1"),
            fixture.CreateValidator());

        var act = () => resolver.ResolveAsync(CreateContext(""));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>()
            .WithMessage("*access token is required*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenAccessTokenWhitespace()
    {
        using var fixture = new IdentityAssertionFixture();
        var resolver = new NyxIdResponsesCallerScopeResolver(
            new StubUserResolver(returnUserId: "user-1"),
            fixture.CreateValidator());

        var act = () => resolver.ResolveAsync(CreateContext("   "));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenUpstreamReturnsNull()
    {
        using var fixture = new IdentityAssertionFixture();
        var resolver = new NyxIdResponsesCallerScopeResolver(
            new StubUserResolver(returnUserId: null),
            fixture.CreateValidator());

        var act = () => resolver.ResolveAsync(CreateContext("some-token"));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>()
            .WithMessage("*Could not resolve current NyxID user id*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenUpstreamReturnsBlank()
    {
        using var fixture = new IdentityAssertionFixture();
        var resolver = new NyxIdResponsesCallerScopeResolver(
            new StubUserResolver(returnUserId: "   "),
            fixture.CreateValidator());

        var act = () => resolver.ResolveAsync(CreateContext("some-token"));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnTrimmedScope_WithApiKeyOrigin()
    {
        using var fixture = new IdentityAssertionFixture();
        var resolver = new NyxIdResponsesCallerScopeResolver(
            new StubUserResolver(returnUserId: "  alice-1  "),
            fixture.CreateValidator());

        var scope = await resolver.ResolveAsync(CreateContext("token"));

        scope.ScopeId.Should().Be("alice-1");
        scope.OwnerSubject.Should().Be("alice-1");
        scope.OriginKind.Should().Be(LlmSessionOriginKind.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_WithValidIdentityAssertion_ShouldResolveScopeWithoutCurrentUserLookup()
    {
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var scope = await resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user"),
            delegationToken: "delegation-token"));

        scope.ScopeId.Should().Be("identity-user");
        scope.OwnerSubject.Should().Be("identity-user");
        scope.OriginKind.Should().Be(LlmSessionOriginKind.ApiKey);
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithReplayedIdentityAssertion_ShouldFailClosedOnSecondUse()
    {
        // The replay guard makes each identity assertion single-use within its lifetime: the same
        // signed jti presented twice against one validator is rejected the second time.
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var validator = fixture.CreateValidator();
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, validator);
        var replayedToken = fixture.CreateToken(subject: "identity-user", jti: "replay-jti");

        var first = await resolver.ResolveAsync(CreateContext("bearer-token", identityToken: replayedToken));
        first.ScopeId.Should().Be("identity-user");

        var replay = () => resolver.ResolveAsync(CreateContext("bearer-token", identityToken: replayedToken));

        await replay.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        // Fail-closed: a rejected replay must NOT fall through to the current-user lookup.
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithAssertionPastRawExpiryButInsideSkew_ShouldRejectSecondUse()
    {
        using var fixture = new IdentityAssertionFixture(clockSkewSeconds: 60);
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var validator = fixture.CreateValidator();
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, validator);
        var issuedAt = DateTime.UtcNow.AddSeconds(-60);
        var token = fixture.CreateToken(
            subject: "identity-user",
            jti: "inside-skew-jti",
            issuedAtUtc: issuedAt,
            notBeforeUtc: issuedAt.AddSeconds(-5),
            expiresAtUtc: DateTime.UtcNow.AddSeconds(-5));

        var first = await resolver.ResolveAsync(CreateContext("bearer-token", identityToken: token));
        var replay = () => resolver.ResolveAsync(CreateContext("bearer-token", identityToken: token));

        first.ScopeId.Should().Be("identity-user");
        await replay.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateAsync_ShouldRetainReplayEntryThroughAcceptedClockSkewBoundary()
    {
        const int clockSkewSeconds = 75;
        using var fixture = new IdentityAssertionFixture(clockSkewSeconds);
        var replayGuard = new RecordingIdentityAssertionReplayGuard();
        var issuedAt = DateTime.UtcNow;
        var token = fixture.CreateToken(
            subject: "identity-user",
            jti: "retention-boundary-jti",
            issuedAtUtc: issuedAt,
            expiresAtUtc: issuedAt.AddSeconds(60));
        var rawExpiresUtc = new DateTimeOffset(
            DateTime.SpecifyKind(new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo, DateTimeKind.Utc));

        var result = await fixture.CreateValidator(replayGuard).ValidateAsync(token);

        result.Succeeded.Should().BeTrue();
        replayGuard.AcceptedUntilUtc.Should().Be(rawExpiresUtc.AddSeconds(clockSkewSeconds));
    }

    [Fact]
    public async Task ValidateAsync_WithNegativeClockSkew_ShouldRetainOnlyThroughRawExpiry()
    {
        using var fixture = new IdentityAssertionFixture(clockSkewSeconds: -30);
        var replayGuard = new RecordingIdentityAssertionReplayGuard();
        var issuedAt = DateTime.UtcNow;
        var token = fixture.CreateToken(
            subject: "identity-user",
            jti: "negative-skew-jti",
            issuedAtUtc: issuedAt,
            expiresAtUtc: issuedAt.AddSeconds(60));
        var rawExpiresUtc = new DateTimeOffset(
            DateTime.SpecifyKind(new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo, DateTimeKind.Utc));

        var result = await fixture.CreateValidator(replayGuard).ValidateAsync(token);

        result.Succeeded.Should().BeTrue();
        replayGuard.AcceptedUntilUtc.Should().Be(rawExpiresUtc);
    }

    [Fact]
    public async Task ResolveAsync_WithFreshJtiAfterPriorUse_ShouldStillResolve()
    {
        // Positive control for the replay guard: a distinct jti (a re-minted assertion) still
        // resolves — only an identical jti is treated as a replay.
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var validator = fixture.CreateValidator();
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, validator);

        var first = await resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user", jti: "jti-a")));
        var second = await resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user", jti: "jti-b")));

        first.ScopeId.Should().Be("identity-user");
        second.ScopeId.Should().Be("identity-user");
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithMalformedIdentityAssertion_ShouldFailClosedWithoutCurrentUserLookup()
    {
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var act = () => resolver.ResolveAsync(CreateContext("bearer-token", identityToken: "not-a-jwt"));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>()
            .WithMessage("*identity assertion is invalid*");
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithExpiredIdentityAssertion_ShouldFailClosed()
    {
        using var fixture = new IdentityAssertionFixture(clockSkewSeconds: 0);
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var act = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(
                subject: "identity-user",
                notBeforeUtc: DateTime.UtcNow.AddMinutes(-10),
                expiresAtUtc: DateTime.UtcNow.AddMinutes(-5))));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithAssertionLifetimeLongerThanMaximum_ShouldFailClosed()
    {
        using var fixture = new IdentityAssertionFixture(clockSkewSeconds: 0);
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());
        var issuedAt = DateTime.UtcNow;

        var act = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(
                subject: "identity-user",
                issuedAtUtc: issuedAt,
                expiresAtUtc: issuedAt.AddSeconds(61))));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithMissingIssuedAt_ShouldFailClosed()
    {
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var act = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user", includeIssuedAt: false)));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithWrongAudienceOrIssuer_ShouldFailClosed()
    {
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var wrongAudience = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user", audience: "wrong-audience")));
        var wrongIssuer = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user", issuer: "https://wrong-issuer.example")));

        await wrongAudience.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        await wrongIssuer.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithMissingSubOrJti_ShouldFailClosed()
    {
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var missingSub = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: null)));
        var missingJti = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user", jti: null)));

        await missingSub.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        await missingJti.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithWrongServiceId_ShouldFailClosed_WhenExpectedServiceIdConfigured()
    {
        using var fixture = new IdentityAssertionFixture(expectedServiceId: "aevatar-service");
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var act = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user", serviceId: "other-service")));

        await act.Should().ThrowAsync<ResponsesCallerScopeUnavailableException>();
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithIssuerDerivedFromNyxIdAuthority_ShouldResolveScope_WithConfiguredAudience()
    {
        // Production shape (regression guard for the IOptions<NyxIdToolOptions> bug that
        // surfaced as "NyxID identity assertion issuer is not configured."): issuer is derived
        // from the NyxID authority on NyxIdToolOptions while audience validation remains required.
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(
            currentUserResolver,
            fixture.CreateValidatorWithAuthorityFallback());

        var scope = await resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user")));

        scope.ScopeId.Should().Be("identity-user");
        scope.OwnerSubject.Should().Be("identity-user");
        scope.OriginKind.Should().Be(LlmSessionOriginKind.ApiKey);
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithDefaultIssuer_ShouldResolveScope_WhenIssuerConfigOmitted()
    {
        // Directly covers the in-code DefaultIssuer: no Issuer in config and no NyxIdToolOptions,
        // yet an assertion whose iss == DefaultIssuer still validates. Guards against the
        // "issuer is not configured" production failure recurring when the config section is absent.
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(
            currentUserResolver,
            fixture.CreateValidatorWithDefaultIssuer());

        var scope = await resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(
                subject: "identity-user",
                issuer: ResponsesNyxIdIdentityAssertionOptions.DefaultIssuer)));

        scope.ScopeId.Should().Be("identity-user");
        scope.OwnerSubject.Should().Be("identity-user");
        currentUserResolver.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithMissingIdentityAssertion_ShouldFallbackToCurrentUserLookup()
    {
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var scope = await resolver.ResolveAsync(CreateContext("bearer-token"));

        scope.ScopeId.Should().Be("fallback-user");
        scope.OwnerSubject.Should().Be("fallback-user");
        currentUserResolver.LastAccessToken.Should().Be("bearer-token");
        currentUserResolver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_WithDelegationTokenOnly_ShouldNotResolveCallerIdentityFromDelegationToken()
    {
        using var fixture = new IdentityAssertionFixture();
        var currentUserResolver = new StubUserResolver(returnUserId: "fallback-user");
        var resolver = new NyxIdResponsesCallerScopeResolver(currentUserResolver, fixture.CreateValidator());

        var scope = await resolver.ResolveAsync(CreateContext("bearer-token", delegationToken: "delegation-token"));

        scope.ScopeId.Should().Be("fallback-user");
        currentUserResolver.LastAccessToken.Should().Be("bearer-token");
        currentUserResolver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_ShouldPropagateCancellation_WhenUsingMeFallback()
    {
        using var fixture = new IdentityAssertionFixture();
        var resolver = new NyxIdResponsesCallerScopeResolver(
            new StubUserResolver(returnUserId: "alice"),
            fixture.CreateValidator());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => resolver.ResolveAsync(CreateContext("token"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldPropagateCancellation_WhenUsingIdentityAssertion()
    {
        using var fixture = new IdentityAssertionFixture();
        var resolver = new NyxIdResponsesCallerScopeResolver(
            new StubUserResolver(returnUserId: "alice"),
            fixture.CreateValidator());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => resolver.ResolveAsync(CreateContext(
            "bearer-token",
            identityToken: fixture.CreateToken(subject: "identity-user")), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ResponsesCallerScopeResolutionContext CreateContext(
        string bearerToken,
        string? identityToken = null,
        string? delegationToken = null) =>
        new(bearerToken, identityToken, delegationToken);

    private sealed class StubUserResolver : INyxIdCurrentUserResolver
    {
        private readonly string? _returnUserId;

        public StubUserResolver(string? returnUserId) => _returnUserId = returnUserId;

        public string? LastAccessToken { get; private set; }

        public int CallCount { get; private set; }

        public Task<string?> ResolveCurrentUserIdAsync(string nyxIdAccessToken, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            LastAccessToken = nyxIdAccessToken;
            return Task.FromResult(_returnUserId);
        }
    }

    private sealed class IdentityAssertionFixture : IDisposable
    {
        private const string KeyId = "test-key";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RsaSecurityKey _signingKey;
        private readonly string _jwksJson;
        private readonly int _clockSkewSeconds;
        private readonly string? _expectedServiceId;

        public IdentityAssertionFixture(int clockSkewSeconds = 60, string? expectedServiceId = null)
        {
            _signingKey = new RsaSecurityKey(_rsa)
            {
                KeyId = KeyId,
            };
            _jwksJson = JsonSerializer.Serialize(new
            {
                keys = new[] { JsonWebKeyConverter.ConvertFromSecurityKey(_signingKey) },
            });
            _clockSkewSeconds = clockSkewSeconds;
            _expectedServiceId = expectedServiceId;
        }

        public string Issuer { get; } = "https://nyxid.example";

        public string Audience { get; } = "aevatar/responses";

        public NyxIdIdentityAssertionValidator CreateValidator(
            IIdentityAssertionReplayGuard? replayGuard = null)
        {
            var options = Options.Create(new ResponsesNyxIdIdentityAssertionOptions
            {
                Issuer = Issuer,
                ExpectedAudience = Audience,
                JwksUri = "https://nyxid.example/jwks",
                ExpectedServiceId = _expectedServiceId,
                ClockSkewSeconds = _clockSkewSeconds,
            });
            return new NyxIdIdentityAssertionValidator(
                new StaticHttpClientFactory(_jwksJson),
                options,
                replayGuard: replayGuard);
        }

        public NyxIdIdentityAssertionValidator CreateValidatorWithAuthorityFallback()
        {
            // Issuer explicitly cleared (overriding the in-code DefaultIssuer) so issuer falls
            // back to the NyxID authority carried by NyxIdToolOptions.
            var options = Options.Create(new ResponsesNyxIdIdentityAssertionOptions
            {
                Issuer = null,
                ExpectedAudience = Audience,
                JwksUri = "https://nyxid.example/jwks",
                ClockSkewSeconds = _clockSkewSeconds,
            });
            return new NyxIdIdentityAssertionValidator(
                new StaticHttpClientFactory(_jwksJson),
                options,
                new NyxIdToolOptions { BaseUrl = Issuer });
        }

        public NyxIdIdentityAssertionValidator CreateValidatorWithDefaultIssuer()
        {
            // Issuer omitted from config -> falls back to the in-code DefaultIssuer.
            var options = Options.Create(new ResponsesNyxIdIdentityAssertionOptions
            {
                ExpectedAudience = Audience,
                JwksUri = "https://nyxid.example/jwks",
                ClockSkewSeconds = _clockSkewSeconds,
            });
            return new NyxIdIdentityAssertionValidator(
                new StaticHttpClientFactory(_jwksJson),
                options);
        }

        public string CreateToken(
            string? subject,
            string? issuer = null,
            string? audience = null,
            string? jti = "jti-1",
            string? serviceId = null,
            DateTime? issuedAtUtc = null,
            DateTime? notBeforeUtc = null,
            DateTime? expiresAtUtc = null,
            bool includeIssuedAt = true)
        {
            var claims = new List<Claim>();
            if (!string.IsNullOrWhiteSpace(subject))
                claims.Add(new Claim(JwtRegisteredClaimNames.Sub, subject));
            if (!string.IsNullOrWhiteSpace(jti))
                claims.Add(new Claim(JwtRegisteredClaimNames.Jti, jti));
            if (!string.IsNullOrWhiteSpace(serviceId))
                claims.Add(new Claim("nyx_service_id", serviceId));

            var issuedAt = issuedAtUtc ?? DateTime.UtcNow;
            if (includeIssuedAt)
            {
                claims.Add(new Claim(
                    JwtRegisteredClaimNames.Iat,
                    new DateTimeOffset(issuedAt).ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64));
            }

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = issuer ?? Issuer,
                Audience = audience ?? Audience,
                Subject = new ClaimsIdentity(claims),
                NotBefore = notBeforeUtc ?? issuedAt.AddSeconds(-5),
                Expires = expiresAtUtc ?? issuedAt.AddSeconds(60),
                SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
            };

            return new JwtSecurityTokenHandler
            {
                SetDefaultTimesOnTokenCreation = false,
            }.CreateEncodedJwt(descriptor);
        }

        public void Dispose() => _rsa.Dispose();
    }

    private sealed class RecordingIdentityAssertionReplayGuard : IIdentityAssertionReplayGuard
    {
        public DateTimeOffset? AcceptedUntilUtc { get; private set; }

        public ValueTask<bool> TryConsumeAsync(
            string jti,
            DateTimeOffset acceptedUntilUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcceptedUntilUtc = acceptedUntilUtc;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly string _jwksJson;

        public StaticHttpClientFactory(string jwksJson) => _jwksJson = jwksJson;

        public HttpClient CreateClient(string name) =>
            new(new StaticJwksHandler(_jwksJson))
            {
                BaseAddress = new Uri("https://nyxid.example"),
            };
    }

    private sealed class StaticJwksHandler : HttpMessageHandler
    {
        private readonly string _jwksJson;

        public StaticJwksHandler(string jwksJson) => _jwksJson = jwksJson;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jwksJson),
            });
        }
    }
}
