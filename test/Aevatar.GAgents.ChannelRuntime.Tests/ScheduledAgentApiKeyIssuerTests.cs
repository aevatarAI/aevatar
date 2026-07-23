using Aevatar.GAgents.Scheduled;
using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Google.Protobuf.WellKnownTypes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ScheduledAgentApiKeyIssuerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public async Task IssueAsync_WithValidatedPlan_CopiesExactServiceAndNodeOrderAndMultiplicity()
    {
        var handler = new RoutingJsonHandler("""{"id":"key-1","full_key":"secret"}""");
        var issuer = CreateIssuer(handler);
        var plan = new ScheduledInvocationAuthorizationPlan
        {
            Owner = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "owner-alpha",
            },
            CredentialPolicy = new ScheduledInvocationCredentialPolicy
            {
                Scopes = { NyxIdCredentialScope.Read, NyxIdCredentialScope.Proxy },
                AllowAllServices = false,
                AllowAllNodes = false,
                ServiceGrantRequirement = AuthorizationGrantRequirement.Required,
                NodeGrantRequirement = AuthorizationGrantRequirement.Required,
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-21T00:00:00Z")),
                PolicyVersion = "scheduled-invocation-auth/v1",
            },
            PermissionDigest = "digest-alpha",
        };
        plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant
        {
            UserServiceId = "us-alpha",
            ServiceSlug = "alpha",
            DisplayName = "Alpha Connector",
        });
        plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant
        {
            UserServiceId = "us-beta",
            ServiceSlug = "beta",
            DisplayName = "Beta Connector",
        });
        plan.NyxIdNodeGrants.Add(new NyxIdNodeGrant
        {
            UserServiceId = "us-alpha",
            NodeId = "node-shared",
            Role = NyxIdNodeRole.Primary,
            EdgeKind = NyxIdNodeEdgeKind.UserServicePrimary,
        });
        plan.NyxIdNodeGrants.Add(new NyxIdNodeGrant
        {
            UserServiceId = "us-beta",
            NodeId = "node-fallback",
            Role = NyxIdNodeRole.Fallback,
            EdgeKind = NyxIdNodeEdgeKind.NodeBinding,
            BindingId = "binding-beta-fallback",
            RoutePriority = -1,
        });
        plan.NyxIdNodeGrants.Add(new NyxIdNodeGrant
        {
            UserServiceId = "us-beta",
            NodeId = "node-shared",
            Role = NyxIdNodeRole.Primary,
            EdgeKind = NyxIdNodeEdgeKind.UserServicePrimary,
        });
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ApiKeyId.Should().Be("key-1");
        result.KeyExpiresAtUnixMs.Should().Be(DateTimeOffset.Parse("2026-07-21T00:00:00Z").ToUnixTimeMilliseconds());
        handler.RequestBodies.Should().ContainSingle();
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies.Single());
        body.RootElement.GetProperty("name").GetString().Should().Be("scheduled-key");
        body.RootElement.GetProperty("scopes").GetString().Should().Be("read proxy");
        body.RootElement.GetProperty("platform").GetString().Should().Be("generic");
        body.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static value => value.GetString())
            .Should().Equal("us-alpha", "us-beta");
        body.RootElement.GetProperty("allowed_node_ids").EnumerateArray().Select(static value => value.GetString())
            .Should().Equal("node-shared", "node-fallback", "node-shared");
        body.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("allow_all_nodes").GetBoolean().Should().BeFalse();
        body.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();
        DateTimeOffset.Parse(body.RootElement.GetProperty("expires_at").GetString()!).Should()
            .Be(DateTimeOffset.Parse("2026-07-21T00:00:00Z"));
    }

    [Fact]
    public async Task IssueAsync_ForOrganizationOwner_ShouldMapExactTargetOrganization()
    {
        var handler = new RoutingJsonHandler("""{"id":"key-org","full_key":"secret"}""");
        var issuer = CreateIssuer(handler);
        var plan = ValidPlan();
        plan.Owner.OwnerKind = AuthorizationOwnerKind.Organization;
        plan.Owner.OwnerSubject = "org-alpha";
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-org-key",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies.Single());
        body.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-alpha");
        body.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("allow_all_nodes").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task RevokeActiveKeysByNameAsync_ShouldRevokeEveryMatchingActivePersonalKeyOnly()
    {
        var handler = new RoutingJsonHandler(
            """
            {
              "keys": [
                {"id":"key-stale-1","name":"scheduled-key","is_active":true},
                {"id":"key-other","name":"other-key","is_active":true},
                {"id":"key-inactive","name":"scheduled-key","is_active":false},
                {"id":"key-stale-2","name":"scheduled-key","is_active":true}
              ]
            }
            """,
            """{"ok":true}""",
            """{"ok":true}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeActiveKeysByNameAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan()),
            "scheduled-key",
            CancellationToken.None);

        result.Completed.Should().BeTrue();
        handler.Requests.Should().Equal(
            "/api/v1/api-keys",
            "/api/v1/api-keys/key-stale-1",
            "/api/v1/api-keys/key-stale-2");
        handler.RequestMethods.Should().Equal(HttpMethod.Get, HttpMethod.Delete, HttpMethod.Delete);
    }

    [Fact]
    public async Task RevokeActiveKeysByNameAsync_ForOrganizationOwner_ShouldUseExactOwnerQuery()
    {
        var handler = new RoutingJsonHandler("""{"keys":[]}""");
        var issuer = CreateIssuer(handler);
        var plan = ValidPlan();
        plan.Owner.OwnerKind = AuthorizationOwnerKind.Organization;
        plan.Owner.OwnerSubject = "org alpha/ops";
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);

        var result = await issuer.RevokeActiveKeysByNameAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-key",
            CancellationToken.None);

        result.Completed.Should().BeTrue();
        handler.Requests.Should().ContainSingle().Which.Should()
            .Be("/api/v1/api-keys?org_id=org%20alpha%2Fops");
        handler.RequestMethods.Should().ContainSingle().Which.Should().Be(HttpMethod.Get);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"keys\":[{\"id\":\"key-1\",\"name\":\"scheduled-key\"}]}")]
    [InlineData("{\"keys\":[{\"id\":\" key-1 \",\"name\":\"scheduled-key\",\"is_active\":true}]}")]
    [InlineData("{\"keys\":[{\"id\":\"key-1\",\"name\":\"scheduled-key\",\"is_active\":true},{\"id\":\"key-2\",\"name\":4,\"is_active\":true}]}")]
    public async Task RevokeActiveKeysByNameAsync_WithMalformedList_ShouldFailBeforeDelete(string response)
    {
        var handler = new RoutingJsonHandler(response);
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeActiveKeysByNameAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan()),
            "scheduled-key",
            CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.Error.Should().Be("nyxid_api_key_list_malformed");
        result.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.ProviderError);
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys");
    }

    [Fact]
    public async Task RevokeActiveKeysByNameAsync_WhenListFails_ShouldReturnPendingWithoutDelete()
    {
        var handler = new RoutingJsonHandler(
            """{"error":true,"status":503,"body":"list unavailable"}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeActiveKeysByNameAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan()),
            "scheduled-key",
            CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.HttpStatus.Should().Be(503);
        result.Error.Should().Be("list unavailable");
        result.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys");
    }

    [Fact]
    public async Task RevokeActiveKeysByNameAsync_WhenAnyDeleteFails_ShouldRemainPending()
    {
        var handler = new RoutingJsonHandler(
            """{"keys":[{"id":"key-stale","name":"scheduled-key","is_active":true}]}""",
            """{"error":true,"status":503,"body":"delete unavailable"}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeActiveKeysByNameAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan()),
            "scheduled-key",
            CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.HttpStatus.Should().Be(503);
        result.Error.Should().Be("delete unavailable");
        result.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        handler.Requests.Should().Equal(
            "/api/v1/api-keys",
            "/api/v1/api-keys/key-stale");
    }

    [Fact]
    public async Task IssueAsync_WithInvalidDigest_FailsBeforeHttpEffect()
    {
        var handler = new RoutingJsonHandler("""{"id":"key-1","full_key":"secret"}""");
        var issuer = CreateIssuer(handler);
        var plan = ValidPlan();
        plan.PermissionDigest = "tampered";

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("authorization_plan_integrity_invalid");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task IssueAsync_ShouldUsePrivateValidatedSnapshot_WhenCallerMutatesExposedClone()
    {
        var handler = new RoutingJsonHandler("""{"id":"key-1","full_key":"secret"}""");
        var issuer = CreateIssuer(handler);
        var validated = new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan());
        validated.Plan.NyxIdServiceGrants[0].UserServiceId = "tampered";

        var result = await issuer.IssueAsync(
            "session-token",
            validated,
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies.Single());
        body.RootElement.GetProperty("allowed_service_ids")[0].GetString().Should().Be("us-alpha");
    }

    [Fact]
    public async Task RevokeAsync_WithSuccessfulDelete_Completes()
    {
        var handler = new RoutingJsonHandler("""{"ok":true}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeAsync("session-token", " key-1 ", CancellationToken.None);

        result.Completed.Should().BeTrue();
        result.HttpStatus.Should().Be(0);
        result.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.None);
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys/key-1");
    }

    [Fact]
    public async Task IssueAsync_WithUnboundedOwnerOrExpiredPolicy_FailsBeforeHttpEffect()
    {
        var plans = new[]
        {
            InvalidPlan(static plan => plan.CredentialPolicy.AllowAllServices = true),
            InvalidPlan(static plan => plan.CredentialPolicy.AllowAllNodes = true),
            InvalidPlan(static plan => plan.Owner.Authority = "https://nyx.example.com"),
            InvalidPlan(static plan => plan.Owner.OwnerSubject = " "),
            InvalidPlan(static plan => plan.Owner.OwnerKind = AuthorizationOwnerKind.Unspecified),
            InvalidPlan(static plan => plan.CredentialPolicy.ExpiresAt = Timestamp.FromDateTimeOffset(Now)),
        };
        var handler = new RoutingJsonHandler("""{"id":"key-1","full_key":"secret"}""");
        var issuer = CreateIssuer(handler);

        foreach (var plan in plans)
        {
            var result = await issuer.IssueAsync(
                "session-token",
                new ValidatedScheduledInvocationAuthorizationPlan(plan),
                "scheduled-key",
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Be("authorization_plan_policy_invalid");
        }
        handler.Requests.Should().BeEmpty();
        handler.RequestBodies.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeAsync_WithNotFound_CompletesIdempotently()
    {
        var handler = new RoutingJsonHandler("""{"error":true,"status":404,"body":"already deleted"}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeAsync("session-token", "key-404", CancellationToken.None);

        result.Completed.Should().BeTrue();
        result.HttpStatus.Should().Be(404);
        result.Error.Should().BeEmpty();
        result.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.None);
    }

    [Theory]
    [InlineData(401, "bearer expired", UserAgentApiKeyRevocationFailureKind.Unauthorized)]
    [InlineData(403, "owner mismatch", UserAgentApiKeyRevocationFailureKind.Unauthorized)]
    [InlineData(429, "rate limited", UserAgentApiKeyRevocationFailureKind.Transient)]
    [InlineData(503, "upstream unavailable", UserAgentApiKeyRevocationFailureKind.Transient)]
    [InlineData(400, "bad request", UserAgentApiKeyRevocationFailureKind.ProviderError)]
    public async Task RevokeAsync_WithErrorEnvelope_RecordsPendingFailure(
        int status,
        string detail,
        UserAgentApiKeyRevocationFailureKind failureKind)
    {
        var handler = new RoutingJsonHandler($$"""{"error":true,"status":{{status}},"body":"{{detail}}"}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeAsync("session-token", "key-fail", CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.HttpStatus.Should().Be(status);
        result.Error.Should().Be(detail);
        result.FailureKind.Should().Be(failureKind);
    }

    [Fact]
    public async Task RevokeAsync_WithMissingTokenOrKey_ReturnsPendingWithoutHttpCall()
    {
        var handler = new RoutingJsonHandler("""{"ok":true}""");
        var issuer = CreateIssuer(handler);

        var missingToken = await issuer.RevokeAsync("", "key-1", CancellationToken.None);
        var missingKey = await issuer.RevokeAsync("session-token", " ", CancellationToken.None);

        missingToken.Completed.Should().BeFalse();
        missingToken.Error.Should().Be("missing_access_token");
        missingToken.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Unauthorized);
        missingKey.Completed.Should().BeFalse();
        missingKey.Error.Should().Be("missing_api_key_id");
        missingKey.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.ProviderError);
        handler.Requests.Should().BeEmpty();
    }

    private static ScheduledAgentApiKeyIssuer CreateIssuer(RoutingJsonHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        return new ScheduledAgentApiKeyIssuer(
            new TestNyxIdApiClientFactory(client),
            timeProvider: new FakeTimeProvider(Now));
    }

    private static ScheduledInvocationAuthorizationPlan ValidPlan()
    {
        var plan = new ScheduledInvocationAuthorizationPlan
        {
            Owner = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "owner-alpha",
            },
            CredentialPolicy = new ScheduledInvocationCredentialPolicy
            {
                Scopes = { NyxIdCredentialScope.Read, NyxIdCredentialScope.Proxy },
                ServiceGrantRequirement = AuthorizationGrantRequirement.Required,
                NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired,
                ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddDays(30)),
                PolicyVersion = "scheduled-invocation-auth/v1",
            },
        };
        plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant
        {
            UserServiceId = "us-alpha",
            ServiceSlug = "alpha",
            DisplayName = "Alpha Connector",
        });
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);
        return plan;
    }

    private static ScheduledInvocationAuthorizationPlan InvalidPlan(
        Action<ScheduledInvocationAuthorizationPlan> mutate)
    {
        var plan = ValidPlan();
        mutate(plan);
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);
        return plan;
    }

    private sealed class TestNyxIdApiClientFactory : INyxIdApiClientFactory
    {
        private readonly NyxIdApiClient _client;

        public TestNyxIdApiClientFactory(NyxIdApiClient client)
        {
            _client = client;
        }

        public NyxIdApiClient CreateClient() => _client;
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Queue<string> _jsonResponses;

        public RoutingJsonHandler(params string[] jsonResponses)
        {
            if (jsonResponses.Length == 0)
                throw new ArgumentException("At least one JSON response is required.", nameof(jsonResponses));
            _jsonResponses = new Queue<string>(jsonResponses);
        }

        public List<string> Requests { get; } = [];
        public List<HttpMethod> RequestMethods { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            RequestMethods.Add(request.Method);
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            if (!_jsonResponses.TryDequeue(out var json))
                throw new InvalidOperationException("No queued JSON response remains.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
