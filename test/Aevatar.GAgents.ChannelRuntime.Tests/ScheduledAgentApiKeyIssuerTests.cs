using Aevatar.GAgents.Scheduled;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Google.Protobuf.WellKnownTypes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ScheduledAgentApiKeyIssuerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
    private const string TargetedScopePlanDigest =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task IssueAsync_WithoutProducerExactSelections_FailsBeforeProviderKeyCreation()
    {
        var handler = new RoutingJsonHandler(PersonalScopePlanJson());
        var issuer = CreateIssuer(handler);
        var plan = ValidPlan();

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ApiKeyId.Should().BeNull();
        result.Error.Should().Be("scheduled_durable_operation_authority_unavailable");
        handler.Requests.Should().ContainSingle()
            .Which.Should().Be("/api/v1/api-keys/scope-plan");
        handler.RequestMethods.Should().ContainSingle().Which.Should().Be(HttpMethod.Post);
        handler.RequestBodies.Should().ContainSingle();

        using var scopeRequest = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
        scopeRequest.RootElement.GetProperty("selected_service_ids")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should().Equal("us-alpha", "us-beta");
        scopeRequest.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();

    }

    [Theory]
    [InlineData("scheduled_invocation", false)]
    [InlineData("general", true)]
    [InlineData("general", false)]
    public void ExtractIssuedKey_WhenNyxIdReturnsIncompatibleCredentialClass_PreservesIdForCleanup(
        string purpose,
        bool scheduledWriteEnabled)
    {
        var result = ScheduledAgentApiKeyIssuer.ExtractIssuedKey(
            $$"""{"id":"key-wrong-class","full_key":"secret","purpose":"{{purpose}}","scheduled_write_enabled":{{scheduledWriteEnabled.ToString().ToLowerInvariant()}}}""",
            Now.AddDays(30).ToUnixTimeMilliseconds(),
            Now);

        result.Success.Should().BeFalse();
        result.ApiKeyId.Should().Be("key-wrong-class");
        result.Error.Should().Be("api_key_create_credential_class_invalid");
        result.ToString().Should().NotContain("secret");
    }

    [Fact]
    public void ExtractIssuedKey_WhenNyxIdOmitsCredentialClass_PreservesIdForCleanup()
    {
        var result = ScheduledAgentApiKeyIssuer.ExtractIssuedKey(
            """{"id":"key-missing-class","full_key":"secret"}""",
            Now.AddDays(30).ToUnixTimeMilliseconds(),
            Now);

        result.Success.Should().BeFalse();
        result.ApiKeyId.Should().Be("key-missing-class");
        result.Error.Should().Be("api_key_create_credential_class_invalid");
    }

    [Fact]
    public void ExtractIssuedKey_WhenProviderOmitsFullKey_PreservesIdForCleanup()
    {
        var result = ScheduledAgentApiKeyIssuer.ExtractIssuedKey(
            """{"id":"key-missing-secret","purpose":"scheduled_invocation","scheduled_write_enabled":true}""",
            Now.AddDays(30).ToUnixTimeMilliseconds(),
            Now);

        result.Success.Should().BeFalse();
        result.ApiKeyId.Should().Be("key-missing-secret");
        result.Error.Should().Be("api_key_create_missing_full_key");
    }

    [Fact]
    public void ExtractIssuedKey_WithCompleteActiveReceipt_ReturnsTypedGrant()
    {
        var result = Extract(CompleteScheduledKeyResponse());

        result.Success.Should().BeTrue();
        result.ApiKeyId.Should().Be("key-1");
        result.KeyExpiresAtUnixMs.Should().Be(Now.AddDays(30).ToUnixTimeMilliseconds());
        var grant = result.DurableOperationGrants.Should().ContainSingle().Which;
        grant.GrantId.Should().Be("grant-1");
        grant.ApiKeyId.Should().Be("key-1");
        grant.UserServiceId.Should().Be("us-alpha");
        grant.EndpointId.Should().Be("endpoint-executions");
        grant.HttpMethod.Should().Be(NyxIdDurableOperationHttpMethod.Post);
        grant.NormalizedPathTemplate.Should().Be("/executions");
        grant.ContractDigest.Should().Be(TargetedScopePlanDigest);
        grant.ValidFromUnixMs.Should().Be(Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        grant.ExpiresAtUnixMs.Should().Be(Now.AddDays(1).ToUnixTimeMilliseconds());
        grant.ReplayPolicy.Should().Be(NyxIdDurableOperationReplayPolicy.DownstreamIdempotencyKey);
        grant.ClientAuditBinding.Should().BeEquivalentTo(new NyxIdDurableOperationClientAuditBinding
        {
            Platform = "lark",
            ScheduleId = "schedule-alpha",
            WorkflowRevision = "revision-7",
            CallSite = "code_execute",
        });
        grant.GrantId = "mutated";
        result.DurableOperationGrants.Single().GrantId.Should().Be("grant-1");
        result.ToString().Should().NotContain("secret-value");
    }

    [Theory]
    [InlineData("future-valid-from")]
    [InlineData("expired")]
    [InlineData("duplicate")]
    [InlineData("api-key-mismatch")]
    [InlineData("unknown-method")]
    [InlineData("unknown-replay-policy")]
    [InlineData("bad-digest")]
    [InlineData("revoked")]
    [InlineData("total-exhausted")]
    [InlineData("window-exhausted")]
    [InlineData("state-version-zero")]
    [InlineData("created-in-future")]
    [InlineData("constraints-missing")]
    [InlineData("missing-grants")]
    [InlineData("malformed-grant")]
    public void ExtractIssuedKey_WithInvalidOrInactiveReceipt_PreservesIdForCleanup(string variant)
    {
        var result = Extract(MutateScheduledKeyResponse(variant));

        result.Success.Should().BeFalse();
        result.ApiKeyId.Should().Be("key-1");
        result.Error.Should().Be("api_key_create_durable_grants_invalid");
        result.DurableOperationGrants.Should().BeEmpty();
        result.ToString().Should().NotContain("secret-value");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("17")]
    [InlineData("\"scalar\"")]
    public void ExtractIssuedKey_WithNonObjectJson_FailsWithoutThrowing(string response)
    {
        var result = Extract(response);

        result.Success.Should().BeFalse();
        result.ApiKeyId.Should().BeNull();
        result.Error.Should().Be("api_key_create_invalid_response_shape");
    }

    [Fact]
    public async Task IssueAsync_WhenNoServiceGrantsRequired_StillRequiresExactSelections()
    {
        var handler = new RoutingJsonHandler(EmptyScopePlanJson());
        var issuer = CreateIssuer(handler);
        var plan = ValidPlan();
        plan.NyxIdServiceGrants.Clear();
        plan.CatalogAuthority = null;
        plan.CredentialPolicy.ServiceGrantRequirement = AuthorizationGrantRequirement.NotRequired;
        plan.CredentialPolicy.NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired;
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-empty-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("scheduled_durable_operation_authority_unavailable");
        handler.Requests.Should().ContainSingle()
            .Which.Should().Be("/api/v1/api-keys/scope-plan");
        using var scopeRequest = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
        scopeRequest.RootElement.GetProperty("selected_service_ids")
            .EnumerateArray()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task IssueAsync_ForOrganizationOwner_ShouldScopePlanExactOwnerBeforeSelectionGate()
    {
        var handler = new RoutingJsonHandler(OrganizationScopePlanJson());
        var issuer = CreateIssuer(handler);
        var plan = ValidPlan();
        plan.Owner.OwnerKind = AuthorizationOwnerKind.Organization;
        plan.Owner.OwnerSubject = "org-alpha";
        plan.AuthenticatedActor = Owner(AuthorizationOwnerKind.Personal, "admin-alpha");
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-org-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("scheduled_durable_operation_authority_unavailable");
        handler.RequestBodies.Should().ContainSingle();
        using var scopeRequest = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
        scopeRequest.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-alpha");
    }

    [Fact]
    public async Task IssueAsync_ForOrganizationOwner_WhenAuthenticatedActorDiffers_ShouldFailBeforeCreate()
    {
        var handler = new RoutingJsonHandler(OrganizationScopePlanJson().Replace(
            "\"authenticated_actor\": { \"id\": \"admin-alpha\", \"type\": \"personal\" }",
            "\"authenticated_actor\": { \"id\": \"admin-other\", \"type\": \"personal\" }",
            StringComparison.Ordinal));
        var issuer = CreateIssuer(handler);
        var plan = ValidPlan();
        plan.Owner = Owner(AuthorizationOwnerKind.Organization, "org-alpha");
        plan.AuthenticatedActor = Owner(AuthorizationOwnerKind.Personal, "admin-alpha");
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-org-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("authorization_plan_changed");
        result.Detail.Should().BeNull();
        result.AuthorizationPlanMismatchReason.Should()
            .Be(ScheduledAuthorizationPlanMismatchReason.AuthenticatedActorMismatch);
        result.ToErrorJson().Should()
            .Contain("authenticated_actor_mismatch")
            .And.NotContain("admin-alpha")
            .And.NotContain("admin-other");
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys/scope-plan");
    }

    [Fact]
    public async Task IssueAsync_WhenTargetedScopePlanChanges_FailsBeforeCreate()
    {
        var changedPlan = PersonalScopePlanJson().Replace(
            "\"node_ids\": [\"node-a\", \"node-shared\"]",
            "\"node_ids\": [\"node-other\"]",
            StringComparison.Ordinal).Replace(
            "\"allowed_node_ids\": [\"node-a\", \"node-shared\"]",
            "\"allowed_node_ids\": [\"node-other\"]",
            StringComparison.Ordinal);
        var handler = new RoutingJsonHandler(changedPlan);
        var issuer = CreateIssuer(handler);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan()),
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("authorization_plan_changed");
        result.Detail.Should().BeNull();
        result.AuthorizationPlanMismatchReason.Should()
            .Be(ScheduledAuthorizationPlanMismatchReason.AllowedNodeIdsMismatch);
        result.ToErrorJson().Should()
            .Contain("allowed_node_ids_mismatch")
            .And.NotContain("node-other");
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys/scope-plan");
    }

    [Theory]
    [InlineData("contract_version", ScheduledAuthorizationPlanMismatchReason.ScopePlanVersionsMismatch)]
    [InlineData("policy_version", ScheduledAuthorizationPlanMismatchReason.ScopePlanVersionsMismatch)]
    [InlineData("principal", ScheduledAuthorizationPlanMismatchReason.IntendedKeyOwnerMismatch)]
    [InlineData("service_identity", ScheduledAuthorizationPlanMismatchReason.AllowedServiceIdsMismatch)]
    [InlineData("resource_owner", ScheduledAuthorizationPlanMismatchReason.ServiceGrantResourceOwnerMismatch)]
    [InlineData("node_requirement", ScheduledAuthorizationPlanMismatchReason.AllowedNodeIdsMismatch)]
    public async Task IssueAsync_WhenCurrentScopeFactDiffersFromValidatedPlan_FailsBeforeCreate(
        string mismatch,
        ScheduledAuthorizationPlanMismatchReason expectedReason)
    {
        var plan = ValidPlan();
        var scopePlanJson = PersonalScopePlanJson();
        switch (mismatch)
        {
            case "contract_version":
                plan.CatalogAuthority.ContractVersion = "2";
                break;
            case "policy_version":
                plan.CatalogAuthority.PolicyVersion = "api-key-scope-v2";
                break;
            case "principal":
                scopePlanJson = scopePlanJson
                    .Replace(
                        "\"authenticated_actor\": { \"id\": \"owner-alpha\", \"type\": \"personal\" }",
                        "\"authenticated_actor\": { \"id\": \"owner-other\", \"type\": \"personal\" }",
                        StringComparison.Ordinal)
                    .Replace(
                        "\"intended_key_owner\": { \"id\": \"owner-alpha\", \"type\": \"personal\" }",
                        "\"intended_key_owner\": { \"id\": \"owner-other\", \"type\": \"personal\" }",
                        StringComparison.Ordinal);
                break;
            case "service_identity":
                scopePlanJson = scopePlanJson.Replace("us-beta", "us-other", StringComparison.Ordinal);
                break;
            case "resource_owner":
                scopePlanJson = scopePlanJson.Replace("resource-org", "resource-other", StringComparison.Ordinal);
                break;
            case "node_requirement":
                scopePlanJson = scopePlanJson
                    .Replace(
                        "\"node_grant\": { \"type\": \"required\", \"node_ids\": [\"node-a\", \"node-shared\"] }",
                        "\"node_grant\": { \"type\": \"not_required\" }",
                        StringComparison.Ordinal)
                    .Replace(
                        "\"allowed_node_ids\": [\"node-a\", \"node-shared\"]",
                        "\"allowed_node_ids\": []",
                        StringComparison.Ordinal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, null);
        }
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);
        var handler = new RoutingJsonHandler(scopePlanJson);
        var issuer = CreateIssuer(handler);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(plan),
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("authorization_plan_changed");
        result.Detail.Should().BeNull();
        result.AuthorizationPlanMismatchReason.Should().Be(expectedReason);
        result.ToErrorJson().Should()
            .Contain(ScheduledAuthorizationPlanMismatchReasons.ToWireValue(expectedReason))
            .And.NotContain("owner-alpha")
            .And.NotContain("owner-other")
            .And.NotContain("us-other")
            .And.NotContain("resource-other")
            .And.NotContain("node-a");
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys/scope-plan");
    }

    [Fact]
    public async Task IssueAsync_WhenScopePlanProviderFails_ReturnsSanitizedErrorBeforeCreate()
    {
        var handler = new RoutingJsonHandler(
            """
            {"error":true,"status":503,"body":"{\"error\":\"secret/token\",\"error_code\":1006,\"message\":\"bearer-secret\"}"}
            """);
        var issuer = CreateIssuer(handler);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan()),
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("nyxid_scope_plan_failed");
        result.HttpStatus.Should().Be(503);
        result.ToErrorJson().Should().NotContain("bearer-secret").And.NotContain("secret/token");
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys/scope-plan");
    }

    [Fact]
    public async Task IssueAsync_WhenScopePlanProviderTimesOut_ReturnsSanitizedFailureBeforeCreate()
    {
        var handler = new CancelingHandler();
        var issuer = CreateIssuer(handler);

        var result = await issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan()),
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("nyxid_scope_plan_provider_timed_out");
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys/scope-plan");
    }

    [Fact]
    public async Task IssueAsync_WhenCallerCancelsScopePlanRequest_PropagatesCancellationBeforeCreate()
    {
        using var callerCancellation = new CancellationTokenSource();
        var handler = new CancelingHandler(callerCancellation);
        var issuer = CreateIssuer(handler);

        var act = () => issuer.IssueAsync(
            "session-token",
            new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan()),
            "scheduled-key",
            callerCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys/scope-plan");
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
        var handler = new RoutingJsonHandler(PersonalScopePlanJson());
        var issuer = CreateIssuer(handler);
        var validated = new ValidatedScheduledInvocationAuthorizationPlan(ValidPlan());
        validated.Plan.NyxIdServiceGrants[0].UserServiceId = "tampered";

        var result = await issuer.IssueAsync(
            "session-token",
            validated,
            "scheduled-key",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("scheduled_durable_operation_authority_unavailable");
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
        body.RootElement.GetProperty("selected_service_ids")[0].GetString().Should().Be("us-alpha");
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
            InvalidPlan(static plan => plan.Owner.Authority = " nyxid "),
            InvalidPlan(static plan => plan.Owner.OwnerSubject = " "),
            InvalidPlan(static plan => plan.Owner.OwnerSubject = " owner-alpha "),
            InvalidPlan(static plan => plan.Owner.OwnerKind = AuthorizationOwnerKind.Unspecified),
            InvalidPlan(static plan => plan.AuthenticatedActor = null),
            InvalidPlan(static plan => plan.AuthenticatedActor.OwnerKind = AuthorizationOwnerKind.Organization),
            InvalidPlan(static plan => plan.AuthenticatedActor.Authority = "authority-other"),
            InvalidPlan(static plan => plan.AuthenticatedActor.OwnerSubject = " owner-alpha "),
            InvalidPlan(static plan =>
                plan.CredentialPolicy.ServiceGrantRequirement = AuthorizationGrantRequirement.Unspecified),
            InvalidPlan(static plan =>
                plan.CredentialPolicy.NodeGrantRequirement = AuthorizationGrantRequirement.Unspecified),
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

    private static ScheduledAgentApiKeyIssuer CreateIssuer(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        return new ScheduledAgentApiKeyIssuer(
            new TestNyxIdApiClientFactory(client),
            timeProvider: new FakeTimeProvider(Now));
    }

    private static ScheduledAgentApiKeyIssueResult Extract(string response) =>
        ScheduledAgentApiKeyIssuer.ExtractIssuedKey(
            response,
            Now.AddDays(30).ToUnixTimeMilliseconds(),
            Now);

    private static string CompleteScheduledKeyResponse() => $$"""
        {
          "id": "key-1",
          "full_key": "secret-value",
          "purpose": "scheduled_invocation",
          "scheduled_write_enabled": true,
          "durable_grants": [
            {
              "id": "grant-1",
              "api_key_id": "key-1",
              "user_service_id": "us-alpha",
              "endpoint_id": "endpoint-executions",
              "method": "POST",
              "normalized_path_template": "/executions",
              "contract_digest": "{{TargetedScopePlanDigest}}",
              "constraints": {
                "path": {},
                "query": {},
                "headers": {},
                "body": {
                  "fields": {
                    "": { "required": true, "type": "exact", "value": { "language": "python" } }
                  },
                  "allow_additional_fields": false
                }
              },
              "valid_from": "2026-07-15T23:59:00Z",
              "expires_at": "2026-07-17T00:00:00Z",
              "total_limit": 10,
              "total_used": 0,
              "window": { "duration_seconds": 3600, "max_operations": 2 },
              "window_used": 0,
              "replay_policy": "downstream_idempotency_key",
              "client_audit_binding": {
                "platform": "lark",
                "schedule_id": "schedule-alpha",
                "workflow_revision": "revision-7",
                "call_site": "code_execute"
              },
              "revoked_at": null,
              "state_version": 1,
              "created_at": "2026-07-15T23:58:00Z"
            }
          ]
        }
        """;

    private static string MutateScheduledKeyResponse(string variant)
    {
        var root = JsonNode.Parse(CompleteScheduledKeyResponse())!.AsObject();
        var grants = root["durable_grants"]!.AsArray();
        var grant = grants[0]!.AsObject();
        switch (variant)
        {
            case "future-valid-from":
                grant["valid_from"] = "2026-07-16T00:01:00Z";
                break;
            case "expired":
                grant["expires_at"] = "2026-07-16T00:00:00Z";
                break;
            case "duplicate":
                grants.Add(grant.DeepClone());
                break;
            case "api-key-mismatch":
                grant["api_key_id"] = "key-other";
                break;
            case "unknown-method":
                grant["method"] = "GET";
                break;
            case "unknown-replay-policy":
                grant["replay_policy"] = "caller_declared";
                break;
            case "bad-digest":
                grant["contract_digest"] = "sha256:not-a-canonical-digest";
                break;
            case "revoked":
                grant["revoked_at"] = "2026-07-15T23:59:30Z";
                break;
            case "total-exhausted":
                grant["total_used"] = 10;
                break;
            case "window-exhausted":
                grant["window_used"] = 2;
                break;
            case "state-version-zero":
                grant["state_version"] = 0;
                break;
            case "created-in-future":
                grant["created_at"] = "2026-07-16T00:01:00Z";
                break;
            case "constraints-missing":
                grant.Remove("constraints");
                break;
            case "missing-grants":
                root.Remove("durable_grants");
                break;
            case "malformed-grant":
                grants[0] = 17;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }

        return root.ToJsonString();
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
            AuthenticatedActor = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "owner-alpha",
            },
            CredentialPolicy = new ScheduledInvocationCredentialPolicy
            {
                Scopes = { NyxIdCredentialScope.Read, NyxIdCredentialScope.Proxy },
                ServiceGrantRequirement = AuthorizationGrantRequirement.Required,
                NodeGrantRequirement = AuthorizationGrantRequirement.Required,
                ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddDays(30)),
                PolicyVersion = ScheduledInvocationAuthorizationContractVersions.CredentialPolicy,
            },
            CatalogAuthority = new NyxIdCatalogAuthorityStamp
            {
                ActorStateVersion = 7,
                ObservedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-5)),
                FreshUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(10)),
                ContentDigest = "local-protobuf-content-digest",
                ContractVersion = NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                PolicyVersion = NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
                EvaluatedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-6)),
            },
        };
        plan.NyxIdServiceGrants.Add(new NyxIdServiceGrant
        {
            UserServiceId = "us-alpha",
            ServiceSlug = "alpha",
            DisplayName = "Alpha Connector",
            ResourceOwner = Owner(
                AuthorizationOwnerKind.Personal,
                "owner-alpha"),
            NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired,
        });
        var beta = new NyxIdServiceGrant
        {
            UserServiceId = "us-beta",
            ServiceSlug = "beta",
            DisplayName = "Beta Connector",
            ResourceOwner = Owner(
                AuthorizationOwnerKind.Organization,
                "resource-org"),
            NodeGrantRequirement = AuthorizationGrantRequirement.Required,
        };
        beta.NodeIds.Add("node-a");
        beta.NodeIds.Add("node-shared");
        plan.NyxIdServiceGrants.Add(beta);
        plan.PermissionDigest = ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(plan);
        return plan;
    }

    private static AuthorizationOwnerIdentity Owner(
        AuthorizationOwnerKind ownerKind,
        string ownerSubject) => new()
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = ownerKind,
            OwnerSubject = ownerSubject,
        };

    private static string PersonalScopePlanJson() => $$"""
        {
          "authority": "nyxid",
          "contract_version": "1",
          "policy_version": "api-key-scope-v1",
          "authenticated_actor": { "id": "owner-alpha", "type": "personal" },
          "intended_key_owner": { "id": "owner-alpha", "type": "personal" },
          "services": [
            {
              "user_service_id": "us-alpha",
              "resource_owner": { "id": "owner-alpha", "type": "personal" },
              "node_grant": { "type": "not_required" }
            },
            {
              "user_service_id": "us-beta",
              "resource_owner": { "id": "resource-org", "type": "organization" },
              "node_grant": { "type": "required", "node_ids": ["node-a", "node-shared"] }
            }
          ],
          "allowed_service_ids": ["us-alpha", "us-beta"],
          "allowed_node_ids": ["node-a", "node-shared"],
          "evaluated_at": "2026-07-16T00:00:01Z",
          "normalized_grant_digest": "{{TargetedScopePlanDigest}}",
          "freshness": {
            "mode": "mutation_revalidated_snapshot",
            "precondition_field": "scope_plan_digest",
            "post_creation_drift": "fail_closed"
          },
          "completeness": {
            "list_complete": true,
            "no_duplicates": true,
            "route_candidate_basis": "active_configured_routes",
            "transient_node_state_excluded": true
          }
        }
        """;

    private static string EmptyScopePlanJson() => $$"""
        {
          "authority": "nyxid",
          "contract_version": "1",
          "policy_version": "api-key-scope-v1",
          "authenticated_actor": { "id": "owner-alpha", "type": "personal" },
          "intended_key_owner": { "id": "owner-alpha", "type": "personal" },
          "services": [],
          "allowed_service_ids": [],
          "allowed_node_ids": [],
          "evaluated_at": "2026-07-16T00:00:01Z",
          "normalized_grant_digest": "{{TargetedScopePlanDigest}}",
          "freshness": {
            "mode": "mutation_revalidated_snapshot",
            "precondition_field": "scope_plan_digest",
            "post_creation_drift": "fail_closed"
          },
          "completeness": {
            "list_complete": true,
            "no_duplicates": true,
            "route_candidate_basis": "active_configured_routes",
            "transient_node_state_excluded": true
          }
        }
        """;

    private static string OrganizationScopePlanJson() => PersonalScopePlanJson()
        .Replace(
            "\"authenticated_actor\": { \"id\": \"owner-alpha\", \"type\": \"personal\" }",
            "\"authenticated_actor\": { \"id\": \"admin-alpha\", \"type\": \"personal\" }",
            StringComparison.Ordinal)
        .Replace(
            "\"intended_key_owner\": { \"id\": \"owner-alpha\", \"type\": \"personal\" }",
            "\"intended_key_owner\": { \"id\": \"org-alpha\", \"type\": \"organization\" }",
            StringComparison.Ordinal);

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

    private sealed class CancelingHandler(CancellationTokenSource? callerCancellation = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            callerCancellation?.Cancel();
            throw new TaskCanceledException("Simulated NyxID scope-plan timeout.", null, cancellationToken);
        }
    }
}
