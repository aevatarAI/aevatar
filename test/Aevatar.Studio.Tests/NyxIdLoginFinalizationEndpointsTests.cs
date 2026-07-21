using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdLoginFinalizationEndpointsTests
{
    private static readonly DateTimeOffset CatalogNow = DateTimeOffset.Parse("2026-07-21T00:05:00Z");

    [Fact]
    public async Task AuthorizationCatalogRefresh_ShouldReturnReadyOnlyAfterReplicaObservation()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23));
        var catalog = new RecordingCatalogQueryPort(CatalogSnapshot(23));
        var http = NewHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "nyx-owner-alpha")],
            "test"));
        http.Request.Headers.Authorization = "Bearer bearer-secret";

        var result = await NyxIdLoginFinalizationEndpoints.HandleAuthorizationCatalogRefreshAsync(
            http,
            lifecycle,
            Visibility(catalog));
        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdAuthorizationCatalogRefreshResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload.Should().Be(new NyxIdAuthorizationCatalogRefreshResponse(
            true,
            RefreshStatus: "observed",
            RefreshFailureCode: string.Empty,
            VisibilityStatus: "ready",
            VisibilityFailureCode: string.Empty,
            RequiredStateVersion: 23,
            VisibleStateVersion: 23));
        lifecycle.Requests.Should().ContainSingle().Which.Should().Be(("nyx-owner-alpha", "bearer-secret"));
        catalog.QueryCount.Should().Be(1);
        JsonSerializer.Serialize(payload).Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task AuthorizationCatalogRefresh_WhenCommittedVersionIsNotVisible_ShouldReturnAcceptedPending()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23));
        var catalog = new RecordingCatalogQueryPort(CatalogSnapshot(22));
        var http = NewHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "nyx-owner-alpha")],
            "test"));
        http.Request.Headers.Authorization = "Bearer bearer-secret";

        var result = await NyxIdLoginFinalizationEndpoints.HandleAuthorizationCatalogRefreshAsync(
            http,
            lifecycle,
            Visibility(catalog));
        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdAuthorizationCatalogRefreshResponse>(result);

        statusCode.Should().Be(StatusCodes.Status202Accepted);
        payload.Should().Be(new NyxIdAuthorizationCatalogRefreshResponse(
            false,
            RefreshStatus: "observed",
            RefreshFailureCode: string.Empty,
            VisibilityStatus: "projection_pending",
            VisibilityFailureCode: "nyxid_catalog_projection_pending",
            RequiredStateVersion: 23,
            VisibleStateVersion: 22));
        catalog.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task AuthorizationCatalogRefresh_WhenNewerInvalidationIsVisible_ShouldNotReportProjectionPending()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23));
        var catalog = new RecordingCatalogQueryPort(CatalogSnapshot(24, invalidated: true));
        var http = NewHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "nyx-owner-alpha")],
            "test"));
        http.Request.Headers.Authorization = "Bearer bearer-secret";

        var result = await NyxIdLoginFinalizationEndpoints.HandleAuthorizationCatalogRefreshAsync(
            http,
            lifecycle,
            Visibility(catalog));
        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdAuthorizationCatalogRefreshResponse>(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        payload.Should().Be(new NyxIdAuthorizationCatalogRefreshResponse(
            false,
            RefreshStatus: "observed",
            RefreshFailureCode: string.Empty,
            VisibilityStatus: "invalidated",
            VisibilityFailureCode: "nyxid_catalog_snapshot_invalidated",
            RequiredStateVersion: 23,
            VisibleStateVersion: 24));
    }

    [Fact]
    public async Task AuthorizationCatalogRefresh_WhenVisibleSnapshotIsStale_ShouldReturnNotReady()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23));
        var catalog = new RecordingCatalogQueryPort(CatalogSnapshot(
            23,
            freshUntilUtc: CatalogNow));
        var http = NewHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "nyx-owner-alpha")],
            "test"));
        http.Request.Headers.Authorization = "Bearer bearer-secret";

        var result = await NyxIdLoginFinalizationEndpoints.HandleAuthorizationCatalogRefreshAsync(
            http,
            lifecycle,
            Visibility(catalog));
        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdAuthorizationCatalogRefreshResponse>(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        payload.Should().Be(new NyxIdAuthorizationCatalogRefreshResponse(
            false,
            RefreshStatus: "observed",
            RefreshFailureCode: string.Empty,
            VisibilityStatus: "stale",
            VisibilityFailureCode: "nyxid_catalog_snapshot_stale",
            RequiredStateVersion: 23,
            VisibleStateVersion: 23));
    }

    [Fact]
    public async Task AuthorizationCatalogRefresh_WhenVisibilityQueryFails_ShouldReturnSanitizedUnavailable()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23));
        var catalog = new RecordingCatalogQueryPort(null)
        {
            Exception = new InvalidOperationException("private-store-detail"),
        };
        var http = NewHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "nyx-owner-alpha")],
            "test"));
        http.Request.Headers.Authorization = "Bearer bearer-secret";

        var result = await NyxIdLoginFinalizationEndpoints.HandleAuthorizationCatalogRefreshAsync(
            http,
            lifecycle,
            Visibility(catalog));
        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdAuthorizationCatalogRefreshResponse>(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        payload.Should().Be(new NyxIdAuthorizationCatalogRefreshResponse(
            false,
            RefreshStatus: "observed",
            RefreshFailureCode: string.Empty,
            VisibilityStatus: "unavailable",
            VisibilityFailureCode: "nyxid_catalog_visibility_unavailable",
            RequiredStateVersion: 23,
            VisibleStateVersion: 0));
        JsonSerializer.Serialize(payload).Should().NotContain("private-store-detail");
    }

    [Fact]
    public async Task AuthorizationCatalogRefresh_WhenAccessIsDenied_ShouldFailClosed()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(new NyxIdAuthorizationCatalogRefreshResult(
            NyxIdAuthorizationCatalogRefreshStatus.AccessDenied,
            "nyxid_catalog_access_denied"));
        var http = NewHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "nyx-owner-alpha")],
            "test"));
        http.Request.Headers.Authorization = "Bearer bearer-secret";

        var result = await NyxIdLoginFinalizationEndpoints.HandleAuthorizationCatalogRefreshAsync(
            http,
            lifecycle,
            Visibility(new RecordingCatalogQueryPort(null)));
        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdAuthorizationCatalogRefreshResponse>(result);

        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        payload.Should().Be(new NyxIdAuthorizationCatalogRefreshResponse(
            false,
            RefreshStatus: "access_denied",
            RefreshFailureCode: "nyxid_catalog_access_denied",
            VisibilityStatus: "not_evaluated",
            VisibilityFailureCode: string.Empty));
    }

    [Fact]
    public async Task AuthorizationCatalogRefresh_WhenPublishedScopePlanIsUnstable_ShouldExposeStableFailure()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(new NyxIdAuthorizationCatalogRefreshResult(
            NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
            "nyxid_scope_plan_catalog_mismatch"));
        var http = NewHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "nyx-owner-alpha")],
            "test"));
        http.Request.Headers.Authorization = "Bearer bearer-secret";

        var result = await NyxIdLoginFinalizationEndpoints.HandleAuthorizationCatalogRefreshAsync(
            http,
            lifecycle,
            Visibility(new RecordingCatalogQueryPort(null)));
        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdAuthorizationCatalogRefreshResponse>(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        payload.Should().Be(new NyxIdAuthorizationCatalogRefreshResponse(
            false,
            RefreshStatus: "catalog_unstable",
            RefreshFailureCode: "nyxid_scope_plan_catalog_mismatch",
            VisibilityStatus: "not_evaluated",
            VisibilityFailureCode: string.Empty));
        JsonSerializer.Serialize(payload).Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task AuthorizationCatalogRefresh_WhenRefreshIsSuperseded_ShouldExposeSupersededStatus()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(new NyxIdAuthorizationCatalogRefreshResult(
            NyxIdAuthorizationCatalogRefreshStatus.Superseded,
            "nyxid_catalog_refresh_superseded"));
        var http = NewHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "nyx-owner-alpha")],
            "test"));
        http.Request.Headers.Authorization = "Bearer bearer-secret";

        var result = await NyxIdLoginFinalizationEndpoints.HandleAuthorizationCatalogRefreshAsync(
            http,
            lifecycle,
            Visibility(new RecordingCatalogQueryPort(null)));
        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdAuthorizationCatalogRefreshResponse>(result);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        payload.Should().Be(new NyxIdAuthorizationCatalogRefreshResponse(
            false,
            RefreshStatus: "superseded",
            RefreshFailureCode: "nyxid_catalog_refresh_superseded",
            VisibilityStatus: "not_evaluated",
            VisibilityFailureCode: string.Empty));
    }

    [Fact]
    public async Task Finalize_ShouldRefreshCatalogForVerifiedNyxIdOwner()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23));
        var catalog = new RecordingCatalogQueryPort(CatalogSnapshot(23));
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
            },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
                "binding-alpha",
                CreateIdToken(new { uid = "nyx-owner-alpha" }),
                "bearer-alpha")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance,
            catalogRefreshLifecycle: lifecycle,
            catalogVisibilityPort: Visibility(catalog));

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.AuthorizationCatalogReady.Should().BeTrue();
        payload.AuthorizationCatalogRefreshStatus.Should().Be("observed");
        payload.AuthorizationCatalogRefreshFailureCode.Should().BeEmpty();
        payload.AuthorizationCatalogVisibilityStatus.Should().Be("ready");
        payload.AuthorizationCatalogVisibilityFailureCode.Should().BeEmpty();
        payload.AuthorizationCatalogRequiredStateVersion.Should().Be(23);
        payload.AuthorizationCatalogVisibleStateVersion.Should().Be(23);
        lifecycle.Requests.Should().ContainSingle().Which.Should().Be(("nyx-owner-alpha", "bearer-alpha"));
        catalog.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task Finalize_WhenCommittedCatalogVersionIsNotVisible_ShouldExposeProjectionPending()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23));
        var catalog = new RecordingCatalogQueryPort(CatalogSnapshot(22));
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
            },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
                "binding-alpha",
                CreateIdToken(new { uid = "nyx-owner-alpha" }),
                "bearer-alpha")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance,
            catalogRefreshLifecycle: lifecycle,
            catalogVisibilityPort: Visibility(catalog));

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.AuthorizationCatalogReady.Should().BeFalse();
        payload.AuthorizationCatalogRefreshStatus.Should().Be("observed");
        payload.AuthorizationCatalogRefreshFailureCode.Should().BeEmpty();
        payload.AuthorizationCatalogVisibilityStatus.Should().Be("projection_pending");
        payload.AuthorizationCatalogVisibilityFailureCode.Should().Be("nyxid_catalog_projection_pending");
        payload.AuthorizationCatalogRequiredStateVersion.Should().Be(23);
        payload.AuthorizationCatalogVisibleStateVersion.Should().Be(22);
        catalog.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task Finalize_WhenCatalogObservationTimesOut_ShouldExposePendingReadiness()
    {
        var lifecycle = new RecordingCatalogRefreshLifecycle(new NyxIdAuthorizationCatalogRefreshResult(
            NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut,
            "nyxid_catalog_observation_timeout"));
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
            },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
                "binding-alpha",
                CreateIdToken(new { uid = "nyx-owner-alpha" }),
                "bearer-alpha")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance,
            catalogRefreshLifecycle: lifecycle);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.AuthorizationCatalogReady.Should().BeFalse();
        payload.AuthorizationCatalogRefreshStatus.Should().Be("observation_timed_out");
        payload.AuthorizationCatalogRefreshFailureCode.Should().Be("nyxid_catalog_observation_timeout");
        payload.AuthorizationCatalogVisibilityStatus.Should().Be("not_evaluated");
        payload.AuthorizationCatalogVisibilityFailureCode.Should().BeEmpty();
    }

    [Fact]
    public async Task Finalize_WhenCatalogRefreshThrows_ShouldKeepSuccessfulTokenAndBindingResponse()
    {
        var lifecycle = new ThrowingCatalogRefreshLifecycle(
            new InvalidOperationException("refresh-private-detail"));
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
            },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
                "binding-alpha",
                CreateIdToken(new { uid = "nyx-owner-alpha" }),
                "bearer-alpha")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance,
            catalogRefreshLifecycle: lifecycle);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.Tokens.AccessToken.Should().Be("bearer-alpha");
        payload.BindingDispatchAccepted.Should().BeTrue();
        payload.AuthorizationCatalogReady.Should().BeFalse();
        payload.AuthorizationCatalogRefreshStatus.Should().Be("failed");
        payload.AuthorizationCatalogRefreshFailureCode.Should().Be("nyxid_catalog_refresh_failed");
        payload.AuthorizationCatalogVisibilityStatus.Should().Be("not_evaluated");
        payload.AuthorizationCatalogVisibilityFailureCode.Should().BeEmpty();
        JsonSerializer.Serialize(payload).Should().NotContain("refresh-private-detail");
    }

    [Fact]
    public async Task Finalize_WhenCallerCancelsCatalogRefresh_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var lifecycle = new ThrowingCatalogRefreshLifecycle(new OperationCanceledException(cts.Token));

        var action = () => NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
            },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
                "binding-alpha",
                CreateIdToken(new { uid = "nyx-owner-alpha" }),
                "bearer-alpha")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance,
            catalogRefreshLifecycle: lifecycle,
            ct: cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Config_ShouldReturnBrokerOAuthClientUsedByFinalizeExchange()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleConfigAsync(
            new StubAevatarOAuthClientProvider(new AevatarOAuthClientSnapshot(
                ClientId: "broker-client-1",
                ClientIdIssuedAt: DateTimeOffset.UnixEpoch,
                HmacKid: "kid",
                HmacKey: [1, 2, 3],
                HmacKeyRotatedAt: DateTimeOffset.UnixEpoch,
                NyxIdAuthority: "https://id.example.test/",
                BrokerCapabilityObserved: true,
                BrokerCapabilityObservedAt: DateTimeOffset.UnixEpoch,
                OauthScope: "openid broker proxy")));

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginConfigurationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload.Should().BeEquivalentTo(new NyxIdLoginConfigurationResponse(
            "https://id.example.test",
            "broker-client-1",
            "openid broker proxy"));
    }

    [Fact]
    public async Task Config_ShouldUseAuthorizationScope_WhenSnapshotScopeIsMissing()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleConfigAsync(
            new StubAevatarOAuthClientProvider(new AevatarOAuthClientSnapshot(
                ClientId: "broker-client-1",
                ClientIdIssuedAt: DateTimeOffset.UnixEpoch,
                HmacKid: "kid",
                HmacKey: [1, 2, 3],
                HmacKeyRotatedAt: DateTimeOffset.UnixEpoch,
                NyxIdAuthority: "https://nyx.example/",
                BrokerCapabilityObserved: true,
                BrokerCapabilityObservedAt: DateTimeOffset.UnixEpoch)));

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginConfigurationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.Scope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
    }

    [Fact]
    public async Task Config_ShouldReturnUnavailable_WhenBrokerOAuthClientIsNotProvisioned()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleConfigAsync(
            new NotProvisionedAevatarOAuthClientProvider());

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Finalize_ShouldCommitOwnerBindingFromAuthorizationCodeExchange()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-owner-1",
            IdToken: CreateIdToken(new { uid = "owner-user-1", email = "owner@example.com", name = "Owner" }),
            AccessToken: "access-token")
        {
            RefreshToken = "refresh-token",
            TokenType = "Bearer",
            ExpiresIn = 1800,
            Scope = "openid profile proxy",
        });
        var queryPort = new FakeExternalIdentityBindingQueryPort();
        var dispatch = new RecordingBindingDispatch();

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
            },
            broker,
            new UsableCapabilityBroker(),
            queryPort,
            dispatch,
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        broker.Exchanges.Should().ContainSingle().Which.Should().Be(("auth-code", "pkce-verifier", "http://localhost/auth/callback"));
        statusCode.Should().Be(StatusCodes.Status200OK);
        payload.Should().NotBeNull();
        payload!.BindingDispatchAccepted.Should().BeTrue();
        payload.Tokens.AccessToken.Should().Be("access-token");
        payload.Tokens.RefreshToken.Should().Be("refresh-token");
        payload.Tokens.ExpiresIn.Should().Be(1800);
        payload.User.Sub.Should().Be("owner-user-1");
        payload.User.Email.Should().Be("owner@example.com");
        dispatch.Commands.Should().ContainSingle().Which.Should().BeEquivalentTo(new CommitBindingCommand
        {
            ExternalSubject = new ExternalSubjectRef
            {
                Platform = OwnerScope.NyxIdPlatform,
                Tenant = string.Empty,
                ExternalUserId = "owner-user-1",
            },
            BindingId = "bnd-owner-1",
            OwnerScopeId = "owner-user-1",
        });
    }

    [Fact]
    public async Task Finalize_ShouldBeIdempotent_WhenOwnerBindingAlreadyExists()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-new",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));
        var queryPort = new FakeExternalIdentityBindingQueryPort();
        queryPort.Bindings[SubjectKey(OwnerSubject("owner-user-1"))] = "bnd-existing";
        var dispatch = new RecordingBindingDispatch();

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new UsableCapabilityBroker(),
            queryPort,
            dispatch,
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.BindingDispatchAccepted.Should().BeFalse();
        dispatch.Commands.Should().BeEmpty();
        broker.RevokedBindingIds.Should().ContainSingle().Which.Should().Be("bnd-new");
    }

    [Fact]
    public async Task Finalize_ShouldReplaceUsableBinding_ForExplicitServiceAccessReview()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-reviewed",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));
        var queryPort = new FakeExternalIdentityBindingQueryPort();
        queryPort.Bindings[SubjectKey(OwnerSubject("owner-user-1"))] = "bnd-existing";
        var replaceDispatch = new RecordingBindingReplaceDispatch();

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
                ServiceAccessReview = true,
            },
            broker,
            new UsableCapabilityBroker(),
            queryPort,
            new RecordingBindingDispatch(),
            replaceDispatch,
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.BindingDispatchAccepted.Should().BeTrue();
        broker.RevokedBindingIds.Should().BeEmpty();
        replaceDispatch.Commands.Should().ContainSingle().Which.Should().BeEquivalentTo(new ReplaceBindingCommand
        {
            ExternalSubject = OwnerSubject("owner-user-1"),
            BindingId = "bnd-reviewed",
            ExpectedPreviousBindingId = "bnd-existing",
            OwnerScopeId = "owner-user-1",
            Reason = "studio_service_access_review",
        });
    }

    [Fact]
    public async Task Finalize_ShouldReplaceBinding_WhenExistingBindingIsRevoked()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-new",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));
        var queryPort = new FakeExternalIdentityBindingQueryPort();
        queryPort.Bindings[SubjectKey(OwnerSubject("owner-user-1"))] = "bnd-existing";
        var dispatch = new RecordingBindingDispatch();
        var replaceDispatch = new RecordingBindingReplaceDispatch();

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new RevokedCapabilityBroker(),
            queryPort,
            dispatch,
            replaceDispatch,
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.BindingDispatchAccepted.Should().BeTrue();
        broker.RevokedBindingIds.Should().BeEmpty();
        replaceDispatch.Commands.Should().ContainSingle().Which.Should().BeEquivalentTo(new ReplaceBindingCommand
        {
            ExternalSubject = OwnerSubject("owner-user-1"),
            BindingId = "bnd-new",
            ExpectedPreviousBindingId = "bnd-existing",
            OwnerScopeId = "owner-user-1",
            Reason = "nyxid_login_recovery",
        });
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Finalize_ShouldReturnUnavailable_WhenExistingBindingProbeFails()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-new",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));
        var queryPort = new FakeExternalIdentityBindingQueryPort();
        queryPort.Bindings[SubjectKey(OwnerSubject("owner-user-1"))] = "bnd-existing";
        var dispatch = new RecordingBindingDispatch();
        var replaceDispatch = new RecordingBindingReplaceDispatch();

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new FailingCapabilityBroker(),
            queryPort,
            dispatch,
            replaceDispatch,
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        broker.RevokedBindingIds.Should().ContainSingle().Which.Should().Be("bnd-new");
        replaceDispatch.Commands.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Finalize_ShouldReplaceBinding_WhenExistingBindingLacksRequiredService()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-new",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));
        var queryPort = new FakeExternalIdentityBindingQueryPort();
        queryPort.Bindings[SubjectKey(OwnerSubject("owner-user-1"))] = "bnd-existing";
        var replaceDispatch = new RecordingBindingReplaceDispatch();

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new ServiceAccessMismatchCapabilityBroker(),
            queryPort,
            new RecordingBindingDispatch(),
            replaceDispatch,
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<NyxIdLoginFinalizationResponse>(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        payload!.BindingDispatchAccepted.Should().BeTrue();
        replaceDispatch.Commands.Should().ContainSingle().Which.Should().BeEquivalentTo(new ReplaceBindingCommand
        {
            ExternalSubject = OwnerSubject("owner-user-1"),
            BindingId = "bnd-new",
            ExpectedPreviousBindingId = "bnd-existing",
            OwnerScopeId = "owner-user-1",
            Reason = "nyxid_login_recovery",
        });
    }

    [Fact]
    public async Task Finalize_ShouldReturnConflict_WhenRequiredServiceWasNotGranted()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(null, null, null))
        {
            ExchangeError = new NyxIdRequiredServiceAccessException(
                ["https://api.example.test/api/v1/proxy/s/aevatar"]),
        };

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<LoginErrorResponse>(result);

        statusCode.Should().Be(StatusCodes.Status409Conflict);
        payload.Should().Be(new LoginErrorResponse(
            "required_service_access_missing",
            "Return to login and allow access to the Aevatar and default LLM services in NyxID."));
    }

    [Fact]
    public async Task Finalize_ShouldRejectAndRevokeNewBinding_WhenIssuedGrantLacksRequiredService()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-insufficient",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest
            {
                Code = "auth-code",
                CodeVerifier = "pkce-verifier",
                RedirectUri = "http://localhost/auth/callback",
            },
            broker,
            new IssuedBindingServiceAccessMismatchCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var (statusCode, payload) = await ExecuteJsonAsync<LoginErrorResponse>(result);

        statusCode.Should().Be(StatusCodes.Status409Conflict);
        payload.Should().Be(new LoginErrorResponse(
            "required_service_access_missing",
            "Return to NyxID and keep every service marked as required by Aevatar selected."));
        broker.RevokedBindingIds.Should().Equal("bnd-insufficient");
    }

    [Fact]
    public async Task Finalize_ShouldRejectMissingCode()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { uid = "owner" }), "access")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Finalize_ShouldRejectMissingCodeVerifier()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { uid = "owner" }), "access")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Finalize_ShouldRejectMissingRedirectUri()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { uid = "owner" }), "access")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Finalize_ShouldReturnConflict_WhenExchangeDoesNotReturnBindingId()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(null, CreateIdToken(new { uid = "owner" }), "access")),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Finalize_ShouldReturnBadGateway_WhenExchangeDoesNotReturnAccessToken()
    {
        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { uid = "owner" }), null)),
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task Finalize_ShouldReturnBadGatewayAndRevokeBinding_WhenSubjectIsMissing()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult("bnd", CreateIdToken(new { email = "owner@example.com" }), "access"));

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RecordingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        broker.RevokedBindingIds.Should().ContainSingle().Which.Should().Be("bnd");
    }

    [Fact]
    public async Task Finalize_ShouldReturnUnavailable_WhenBindingDispatchFails()
    {
        var broker = new RecordingBrokerCallback(new BrokerAuthorizationCodeResult(
            BindingId: "bnd-owner-1",
            IdToken: CreateIdToken(new { uid = "owner-user-1" }),
            AccessToken: "access-token"));

        var result = await NyxIdLoginFinalizationEndpoints.HandleFinalizeAsync(
            new NyxIdLoginFinalizationRequest { Code = "auth-code", CodeVerifier = "pkce-verifier", RedirectUri = "http://localhost/auth/callback" },
            broker,
            new UsableCapabilityBroker(),
            new FakeExternalIdentityBindingQueryPort(),
            new RejectingBindingDispatch(),
            new RecordingBindingReplaceDispatch(),
            NullLoggerFactory.Instance);

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        broker.RevokedBindingIds.Should().ContainSingle().Which.Should().Be("bnd-owner-1");
    }

    private static string CreateIdToken(object payload)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpContext NewHttpContext()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int StatusCode, T? Payload)> ExecuteJsonAsync<T>(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (context.Response.StatusCode, JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed record LoginErrorResponse(string Error, string Detail);

    private sealed class RecordingCatalogRefreshLifecycle(
        NyxIdAuthorizationCatalogRefreshResult? result = null) : INyxIdAuthorizationCatalogRefreshPort
    {
        public List<(string OwnerSubject, string BearerToken)> Requests { get; } = [];

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
            string verifiedOwnerSubject,
            string bearerToken,
            CancellationToken ct = default)
        {
            Requests.Add((verifiedOwnerSubject, bearerToken));
            return Task.FromResult(result ?? NyxIdAuthorizationCatalogRefreshResult.ObservedAt(1));
        }

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
            AuthorizationOwnerIdentity owner,
            string bearerToken,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingCatalogQueryPort(NyxIdAuthorizationCatalogSnapshot? snapshot)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public Exception? Exception { get; init; }
        public int QueryCount { get; private set; }

        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            QueryCount++;
            return Exception == null
                ? Task.FromResult(snapshot)
                : Task.FromException<NyxIdAuthorizationCatalogSnapshot?>(Exception);
        }
    }

    private static INyxIdAuthorizationCatalogVisibilityPort Visibility(
        INyxIdAuthorizationCatalogQueryPort queryPort) =>
        new NyxIdAuthorizationCatalogVisibilityService(
            queryPort,
            new FixedTimeProvider(CatalogNow),
            NullLogger<NyxIdAuthorizationCatalogVisibilityService>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static NyxIdAuthorizationCatalogSnapshot CatalogSnapshot(
        long stateVersion,
        bool invalidated = false,
        DateTimeOffset? freshUntilUtc = null) => new(
        new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = "nyx-owner-alpha",
        },
        stateVersion,
        CatalogNow.AddMinutes(-1),
        freshUntilUtc ?? CatalogNow.AddMinutes(10),
        "scope-plan-contract/v1",
        "scope-plan-policy/v1",
        CatalogNow.AddMinutes(-1),
        "catalog-digest-alpha",
        [],
        Invalidated: invalidated,
        Activated: true);

    private sealed class ThrowingCatalogRefreshLifecycle(Exception exception) : INyxIdAuthorizationCatalogRefreshPort
    {
        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
            string verifiedOwnerSubject,
            string bearerToken,
            CancellationToken ct = default) =>
            Task.FromException<NyxIdAuthorizationCatalogRefreshResult>(exception);

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
            AuthorizationOwnerIdentity owner,
            string bearerToken,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ExternalSubjectRef OwnerSubject(string externalUserId) =>
        new()
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = externalUserId,
        };

    private static string SubjectKey(ExternalSubjectRef subject) =>
        $"{subject.Platform}:{subject.Tenant}:{subject.ExternalUserId}";

    private sealed class StubAevatarOAuthClientProvider(AevatarOAuthClientSnapshot snapshot) : IAevatarOAuthClientProvider
    {
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class NotProvisionedAevatarOAuthClientProvider : IAevatarOAuthClientProvider
    {
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            throw new AevatarOAuthClientNotProvisionedException();
    }

    private sealed class RecordingBrokerCallback(BrokerAuthorizationCodeResult result) : INyxIdBrokerCallbackClient
    {
        public Exception? ExchangeError { get; init; }
        public List<string> RevokedBindingIds { get; } = [];
        public List<(string Code, string CodeVerifier, string RedirectUri)> Exchanges { get; } = [];

        public Task<CallbackStateDecode> TryDecodeStateTokenAsync(string stateToken, CancellationToken ct = default) =>
            Task.FromResult(CallbackStateDecode.Failed("not_supported"));

        public Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
            string authorizationCode,
            string codeVerifier,
            CancellationToken ct = default) =>
            Task.FromResult(result);

        public Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
            string authorizationCode,
            string codeVerifier,
            string redirectUri,
            CancellationToken ct = default)
        {
            Exchanges.Add((authorizationCode, codeVerifier, redirectUri));
            if (ExchangeError is not null)
                return Task.FromException<BrokerAuthorizationCodeResult>(ExchangeError);
            return Task.FromResult(result);
        }

        public Task RevokeBindingByIdAsync(string bindingId, CancellationToken ct = default)
        {
            RevokedBindingIds.Add(bindingId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExternalIdentityBindingQueryPort : IExternalIdentityBindingQueryPort
    {
        public Dictionary<string, string> Bindings { get; } = new(StringComparer.Ordinal);

        public Task<BindingId?> ResolveAsync(ExternalSubjectRef externalSubject, CancellationToken ct = default)
        {
            return Task.FromResult(Bindings.TryGetValue(SubjectKey(externalSubject), out var bindingId)
                ? new BindingId { Value = bindingId }
                : null);
        }
    }

    private abstract class StubCapabilityBroker : INyxIdCapabilityBroker
    {
        public Task<BindingChallenge> StartExternalBindingAsync(ExternalSubjectRef externalSubject, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeBindingAsync(ExternalSubjectRef externalSubject, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public abstract Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default);

        public virtual Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            Task.FromResult(new CapabilityHandle
            {
                AccessToken = "issued-binding-probe-token",
                Scope = scope.Value,
                ExpiresAtUnix = 3600,
            });
    }

    private class UsableCapabilityBroker : StubCapabilityBroker
    {
        public override Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            Task.FromResult(new CapabilityHandle
            {
                AccessToken = "probe-token",
                Scope = scope.Value,
                ExpiresAtUnix = 3600,
            });
    }

    private sealed class RevokedCapabilityBroker : StubCapabilityBroker
    {
        public override Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            throw new BindingRevokedException(externalSubject);
    }

    private sealed class ServiceAccessMismatchCapabilityBroker : StubCapabilityBroker
    {
        public override Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            throw new BindingServiceAccessMismatchException(
                externalSubject,
                ["https://api.example.test/api/v1/proxy/s/aevatar"]);
    }

    private sealed class IssuedBindingServiceAccessMismatchCapabilityBroker : UsableCapabilityBroker
    {
        public override Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            throw new BindingServiceAccessMismatchException(
                externalSubject,
                ["https://api.example.test/api/v1/proxy/s/aevatar"]);
    }

    private sealed class FailingCapabilityBroker : StubCapabilityBroker
    {
        public override Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            throw new HttpRequestException("nyxid unavailable");
    }

    private sealed class RecordingBindingDispatch
        : ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    {
        public List<CommitBindingCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
            CommitBindingCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Success(
                new ChannelIdentityOAuthAcceptedReceipt("actor", "command", "command")));
        }
    }

    private sealed class RecordingBindingReplaceDispatch
        : ICommandDispatchService<ReplaceBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    {
        public List<ReplaceBindingCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
            ReplaceBindingCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Success(
                new ChannelIdentityOAuthAcceptedReceipt("actor", "command", "command")));
        }
    }

    private sealed class RejectingBindingDispatch
        : ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    {
        public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
            CommitBindingCommand command,
            CancellationToken ct = default) =>
            Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Failure(
                ChannelIdentityOAuthDispatchError.InvalidTarget));
    }
}
