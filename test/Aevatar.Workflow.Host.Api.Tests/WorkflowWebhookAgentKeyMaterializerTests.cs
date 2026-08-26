using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowWebhookAgentKeyMaterializerTests
{
    [Fact]
    public async Task Materialize_ShouldIssueExactAdmissionScopedKeyAndPersistOnlyIssuedMaterial()
    {
        const string issuedAgentKey = "nyxid_ag_dedicated_webhook_runtime_key";
        var handler = new SequencedNyxIdHandler(
            ScopePlanResponse(),
            CreatedKeyResponse("provider-key-1", issuedAgentKey),
            string.Empty);
        var vault = new InMemorySecretVault();
        var materializer = CreateMaterializer(handler, vault);

        var result = await materializer.MaterializeAsync(
            CallerAuthority(),
            DurableAdmissionPlan(),
            "scope-1",
            "hr-01",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Credential!.ProviderCredentialId.Should().Be("provider-key-1");
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Select(static request => request.Authorization)
            .Should().OnlyContain(static value => value == "Bearer management-bearer");

        using (var scopePlanBody = JsonDocument.Parse(handler.Requests[0].Body!))
        {
            scopePlanBody.RootElement.GetProperty("selected_service_ids")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .Should().Equal("service-alpha", "service-chrono-sandbox");
            scopePlanBody.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();
        }

        using (var createBody = JsonDocument.Parse(handler.Requests[1].Body!))
        {
            var root = createBody.RootElement;
            root.GetProperty("scopes").GetString().Should().Be("proxy");
            root.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
            root.GetProperty("allow_all_nodes").GetBoolean().Should().BeFalse();
            root.GetProperty("allowed_service_ids")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .Should().Equal("service-alpha", "service-chrono-sandbox");
            root.GetProperty("allowed_node_ids").GetArrayLength().Should().Be(0);
            root.GetProperty("scope_plan_digest").GetString()
                .Should().Be("sha256:" + new string('a', 64));
        }

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            result.Credential.Ref,
            result.Credential.Purpose,
            result.Credential.OwnerScopeKey,
            result.Credential.SubjectId,
            "test-read"));
        resolved.Secret.Should().Be(issuedAgentKey);
        resolved.Secret.Should().NotBe("management-bearer");

        var revoked = await materializer.RevokeAsync(
            CallerAuthority(),
            result.Credential,
            "test-revoke",
            CancellationToken.None);

        revoked.Should().BeTrue();
        handler.Requests[2].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[2].Uri.Should().EndWith("/api/v1/api-keys/provider-key-1");
        var afterRevoke = await vault.ResolveAsync(new ResolveSecretRequest(
            result.Credential.Ref,
            result.Credential.Purpose,
            result.Credential.OwnerScopeKey,
            result.Credential.SubjectId,
            "test-read-after-revoke"));
        afterRevoke.FailureReason.Should().Be(SecretResolutionFailureReason.Revoked);
    }

    [Fact]
    public async Task Materialize_WhenVaultWriteFails_ShouldRollbackIssuedProviderKey()
    {
        var handler = new SequencedNyxIdHandler(
            ScopePlanResponse(),
            CreatedKeyResponse("provider-key-rollback", "nyxid_ag_rollback_runtime_key"),
            string.Empty);
        var materializer = CreateMaterializer(handler, new FailingPutSecretVault());

        var result = await materializer.MaterializeAsync(
            CallerAuthority(),
            DurableAdmissionPlan(),
            "scope-1",
            "hr-01",
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_VAULT_UNAVAILABLE");
        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[2].Uri.Should().EndWith("/api/v1/api-keys/provider-key-rollback");
    }

    [Fact]
    public async Task Materialize_WhenAdmissionPlanIsInvalid_ShouldFailBeforeCallingProvider()
    {
        var handler = new SequencedNyxIdHandler();
        var materializer = CreateMaterializer(handler, new InMemorySecretVault());

        var interactivePlan = DurableAdmissionPlan();
        interactivePlan.ExecutionMode = ExternalCapabilityExecutionMode.Interactive;
        var interactive = await materializer.MaterializeAsync(
            CallerAuthority(), interactivePlan, "scope-1", "hr-01", CancellationToken.None);

        var emptyPlan = DurableAdmissionPlan();
        emptyPlan.InvocationAdmissions.Clear();
        emptyPlan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(emptyPlan);
        var empty = await materializer.MaterializeAsync(
            CallerAuthority(), emptyPlan, "scope-1", "hr-01", CancellationToken.None);

        interactive.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_SCOPE_INVALID");
        empty.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_SCOPE_INVALID");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Materialize_WhenManagementBearerCannotBeIssued_ShouldFailClosed(bool throwOnIssue)
    {
        var handler = new SequencedNyxIdHandler();
        var materializer = CreateMaterializer(
            handler,
            new InMemorySecretVault(),
            new FailingAccessTokenProvider(throwOnIssue));

        var result = await materializer.MaterializeAsync(
            CallerAuthority(), DurableAdmissionPlan(), "scope-1", "hr-01", CancellationToken.None);

        result.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_ISSUANCE_UNAVAILABLE");
        result.StatusCode.Should().Be(503);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Materialize_WhenScopePlanningFailsOrDrifts_ShouldReturnTypedFailure()
    {
        var unavailableHandler = new SequencedNyxIdHandler { ThrowAtRequestIndex = 0 };
        var unavailable = await CreateMaterializer(unavailableHandler, new InMemorySecretVault())
            .MaterializeAsync(
                CallerAuthority(), DurableAdmissionPlan(), "scope-1", "hr-01", CancellationToken.None);

        var rejectedHandler = new SequencedNyxIdHandler("""{"error":true,"status":429}""");
        var rejected = await CreateMaterializer(rejectedHandler, new InMemorySecretVault())
            .MaterializeAsync(
                CallerAuthority(), DurableAdmissionPlan(), "scope-1", "hr-01", CancellationToken.None);

        var driftedAuthority = CallerAuthority();
        driftedAuthority.ExternalUserId = "owner-beta";
        var driftedAdmissionPlan = DurableAdmissionPlan();
        driftedAdmissionPlan.DurableAuthorizationOwner.OwnerSubject = "owner-beta";
        driftedAdmissionPlan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeAdmissionDigest(driftedAdmissionPlan);
        var driftedHandler = new SequencedNyxIdHandler(ScopePlanResponse());
        var drifted = await CreateMaterializer(driftedHandler, new InMemorySecretVault())
            .MaterializeAsync(
                driftedAuthority,
                driftedAdmissionPlan,
                "scope-1",
                "hr-01",
                CancellationToken.None);

        unavailable.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_SCOPE_PLAN_FAILED");
        unavailable.StatusCode.Should().Be(502);
        rejected.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_SCOPE_PLAN_FAILED");
        rejected.StatusCode.Should().Be(429);
        drifted.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_SCOPE_CHANGED");
        drifted.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task Materialize_WhenProviderCreationTransportFails_ShouldReturnTypedFailure()
    {
        var handler = new SequencedNyxIdHandler(ScopePlanResponse())
        {
            ThrowAtRequestIndex = 1,
        };

        var result = await CreateMaterializer(handler, new InMemorySecretVault())
            .MaterializeAsync(
                CallerAuthority(), DurableAdmissionPlan(), "scope-1", "hr-01", CancellationToken.None);

        result.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_CREATE_FAILED");
        result.StatusCode.Should().Be(502);
    }

    [Theory]
    [InlineData("", 502)]
    [InlineData("[]", 502)]
    [InlineData("{not-json", 502)]
    [InlineData("{}", 502)]
    [InlineData("{\"error\":true}", 502)]
    [InlineData("{\"error\":true,\"status\":409}", 409)]
    [InlineData("{\"id\":\" provider-key\",\"full_key\":\"nyxid_ag_key\"}", 502)]
    public async Task Materialize_WhenProviderCreationResponseIsInvalid_ShouldNormalizeStatus(
        string createResponse,
        int expectedStatus)
    {
        var handler = new SequencedNyxIdHandler(ScopePlanResponse(), createResponse);

        var result = await CreateMaterializer(handler, new InMemorySecretVault())
            .MaterializeAsync(
                CallerAuthority(), DurableAdmissionPlan(), "scope-1", "hr-01", CancellationToken.None);

        result.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_CREATE_FAILED");
        result.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task Materialize_WhenProviderOmitsFullKey_RollsBackIssuedIdentity()
    {
        var handler = new SequencedNyxIdHandler(
            ScopePlanResponse(),
            """{"id":"provider-key-missing-secret","full_key":" "}""",
            string.Empty);

        var result = await CreateMaterializer(handler, new InMemorySecretVault())
            .MaterializeAsync(
                CallerAuthority(), DurableAdmissionPlan(), "scope-1", "hr-01", CancellationToken.None);

        result.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_CREATE_FAILED");
        result.StatusCode.Should().Be(502);
        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[2].Uri.Should().EndWith("/api/v1/api-keys/provider-key-missing-secret");
    }

    [Theory]
    [InlineData("scheduled_invocation", true)]
    [InlineData("scheduled_invocation", false)]
    [InlineData("general", true)]
    public async Task Materialize_WhenProviderReturnsIncompatibleCredentialClass_RollsBackKey(
        string purpose,
        bool scheduledWriteEnabled)
    {
        var createResponse =
            $$"""{"id":"provider-key-wrong-class","full_key":"nyxid_ag_key","purpose":"{{purpose}}","scheduled_write_enabled":{{scheduledWriteEnabled.ToString().ToLowerInvariant()}}}""";
        var handler = new SequencedNyxIdHandler(
            ScopePlanResponse(),
            createResponse,
            string.Empty);

        var result = await CreateMaterializer(handler, new InMemorySecretVault())
            .MaterializeAsync(
                CallerAuthority(), DurableAdmissionPlan(), "scope-1", "hr-01", CancellationToken.None);

        result.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_CLASS_INVALID");
        result.StatusCode.Should().Be(502);
        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[2].Uri.Should().EndWith("/api/v1/api-keys/provider-key-wrong-class");
    }

    [Fact]
    public async Task Materialize_WhenIncompatibleCredentialCleanupIsRejected_ReportsManualCleanupFailure()
    {
        var handler = new SequencedNyxIdHandler(
            ScopePlanResponse(),
            """{"id":"provider-key-wrong-class","full_key":"nyxid_ag_key","purpose":"scheduled_invocation","scheduled_write_enabled":true}""",
            """{"error":true,"status":503}""");

        var result = await CreateMaterializer(handler, new InMemorySecretVault())
            .MaterializeAsync(
                CallerAuthority(), DurableAdmissionPlan(), "scope-1", "hr-01", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("WEBHOOK_CALLER_CREDENTIAL_CLEANUP_FAILED");
        result.StatusCode.Should().Be(503);
        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task Revoke_WhenReferenceOrAuthorityIsInvalid_ShouldFailClosed()
    {
        var handler = new SequencedNyxIdHandler();
        var materializer = CreateMaterializer(handler, new ConfigurableSecretVault(revoked: true));
        var invalidReference = WebhookCredential(providerCredentialId: string.Empty);
        invalidReference.Purpose = "other-purpose";

        var invalid = await materializer.RevokeAsync(
            CallerAuthority(), invalidReference, "test-revoke", CancellationToken.None);

        var mismatchedAuthority = CallerAuthority();
        mismatchedAuthority.ExternalUserId = "owner-beta";
        var mismatched = await materializer.RevokeAsync(
            mismatchedAuthority,
            WebhookCredential(providerCredentialId: "provider-key-mismatch"),
            "test-revoke",
            CancellationToken.None);

        invalid.Should().BeFalse();
        mismatched.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("{}", true)]
    [InlineData("{\"error\":true,\"status\":404}", true)]
    [InlineData("{\"error\":true,\"status\":500}", false)]
    public async Task Revoke_ShouldCombineProviderAndVaultOutcomes(
        string providerResponse,
        bool expected)
    {
        var handler = new SequencedNyxIdHandler(providerResponse);
        var materializer = CreateMaterializer(handler, new ConfigurableSecretVault(revoked: true));

        var result = await materializer.RevokeAsync(
            CallerAuthority(),
            WebhookCredential(providerCredentialId: "provider-key-1"),
            "test-revoke",
            CancellationToken.None);

        result.Should().Be(expected);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task Revoke_WhenCleanupDependenciesFail_ShouldReturnFalse()
    {
        var providerFailureHandler = new SequencedNyxIdHandler { ThrowAtRequestIndex = 0 };
        var providerFailure = await CreateMaterializer(
                providerFailureHandler,
                new ConfigurableSecretVault(revoked: true))
            .RevokeAsync(
                CallerAuthority(),
                WebhookCredential(providerCredentialId: "provider-key-1"),
                "test-revoke",
                CancellationToken.None);

        var vaultFailure = await CreateMaterializer(
                new SequencedNyxIdHandler(),
                new ConfigurableSecretVault(revoked: false, throwOnRevoke: true))
            .RevokeAsync(
                CallerAuthority(),
                WebhookCredential(providerCredentialId: string.Empty),
                "test-revoke",
                CancellationToken.None);

        providerFailure.Should().BeFalse();
        vaultFailure.Should().BeFalse();
    }

    private static WorkflowWebhookAgentKeyMaterializer CreateMaterializer(
        HttpMessageHandler handler,
        ISecretVault vault,
        IWorkflowCallerAccessTokenProvider? accessTokenProvider = null) => new(
        accessTokenProvider ?? new FixedAccessTokenProvider(),
        new StaticApiClientFactory(handler),
        vault,
        NullLogger<WorkflowWebhookAgentKeyMaterializer>.Instance);

    private static string CreatedKeyResponse(string id, string fullKey) =>
        $$"""{"id":"{{id}}","full_key":"{{fullKey}}","purpose":"general","scheduled_write_enabled":false}""";

    private static WorkflowCallerNyxIdAuthority CallerAuthority() => new()
    {
        Platform = "nyxid",
        ExternalUserId = "owner-alpha",
        Scope = "proxy",
        BindingId = "binding-alpha",
    };

    private static DurableCallerCredentialRef WebhookCredential(string providerCredentialId) => new()
    {
        Ref = "sec-webhook-binding-1",
        Purpose = CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
        OwnerScopeKey = "scope-1",
        SubjectId = "owner-alpha",
        SourceKind = DurableCallerCredentialSourceKind.WebhookBinding,
        ProviderCredentialId = providerCredentialId,
    };

    private static WorkflowCapabilityAdmissionPlan DurableAdmissionPlan()
    {
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = NyxIdOperationRisk.Write,
            Approval = NyxIdOperationApproval.Required,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
            AllowedExecutionModes =
            {
                ExternalCapabilityExecutionMode.Interactive,
                ExternalCapabilityExecutionMode.Durable,
            },
        };
        var codeExecution = new CodeExecutionCapabilityRef
        {
            UserServiceId = "service-chrono-sandbox",
            ServiceSlugSnapshot = "chrono-sandbox",
            CatalogServiceId = "catalog-chrono-sandbox",
            AllowedExecutionModes =
            {
                ExternalCapabilityExecutionMode.Interactive,
                ExternalCapabilityExecutionMode.Durable,
            },
        };
        codeExecution.ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeCodeExecutionCapabilityDigest(
                codeExecution.UserServiceId,
                codeExecution.ServiceSlugSnapshot,
                codeExecution.CatalogServiceId);

        var plan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            DefinitionDigest = "sha256:definition",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            DurableAuthorizationOwner = new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "owner-alpha",
            },
        };
        plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "hr-01/create_request",
            Capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "service-alpha",
                    ServiceSlugSnapshot = "api-lark-base",
                    HttpMethod = "POST",
                    PathTemplate = "/records",
                    ContractDigest = "sha256:published-operation",
                    EndpointId = "endpoint-create-record",
                    ExecutionPolicy = policy,
                },
            },
        });
        plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "hr-01/normalize_person",
            Capability = new ExternalWorkflowCapabilityRef { CodeExecution = codeExecution },
        });
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);
        return plan;
    }

    private static string ScopePlanResponse() => $$"""
        {
          "authority": "nyxid",
          "contract_version": "1",
          "policy_version": "api-key-scope-v1",
          "authenticated_actor": { "id": "owner-alpha", "type": "personal" },
          "intended_key_owner": { "id": "owner-alpha", "type": "personal" },
          "services": [
            {
              "user_service_id": "service-alpha",
              "resource_owner": { "id": "owner-alpha", "type": "personal" },
              "node_grant": { "type": "not_required" }
            },
            {
              "user_service_id": "service-chrono-sandbox",
              "resource_owner": { "id": "owner-alpha", "type": "personal" },
              "node_grant": { "type": "not_required" }
            }
          ],
          "allowed_service_ids": ["service-alpha", "service-chrono-sandbox"],
          "allowed_node_ids": [],
          "evaluated_at": "2026-08-23T08:09:10.123456789Z",
          "normalized_grant_digest": "sha256:{{new string('a', 64)}}",
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

    private sealed class FixedAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        public Task<string> IssueAsync(
            WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default) => Task.FromResult("management-bearer");
    }

    private sealed class FailingAccessTokenProvider(bool throwOnIssue)
        : IWorkflowCallerAccessTokenProvider
    {
        public Task<string> IssueAsync(
            WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default) => throwOnIssue
            ? Task.FromException<string>(new InvalidOperationException("bearer issuance unavailable"))
            : Task.FromResult(string.Empty);
    }

    private sealed class StaticApiClientFactory(HttpMessageHandler handler)
        : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example/" },
            new HttpClient(handler, disposeHandler: false),
            NullLogger<NyxIdApiClient>.Instance);
    }

    private sealed class SequencedNyxIdHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<RecordedRequest> Requests { get; } = [];
        public int? ThrowAtRequestIndex { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (_index == ThrowAtRequestIndex)
                throw new HttpRequestException("NyxID unavailable");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[_index++], Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? Body);

    private sealed class FailingPutSecretVault : ISecretVault
    {
        public Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default) =>
            Task.FromException<StoreSecretResult>(new InvalidOperationException("vault unavailable"));

        public Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ConfigurableSecretVault(bool revoked, bool throwOnRevoke = false)
        : ISecretVault
    {
        public Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default) => throwOnRevoke
            ? Task.FromException<RevokeSecretResult>(new InvalidOperationException("vault unavailable"))
            : Task.FromResult(new RevokeSecretResult(revoked));
    }
}
