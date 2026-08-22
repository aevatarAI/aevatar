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
            $$"""{"id":"provider-key-1","full_key":"{{issuedAgentKey}}"}""",
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
            """{"id":"provider-key-rollback","full_key":"nyxid_ag_rollback_runtime_key"}""",
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

    private static WorkflowWebhookAgentKeyMaterializer CreateMaterializer(
        HttpMessageHandler handler,
        ISecretVault vault) => new(
        new FixedAccessTokenProvider(),
        new StaticApiClientFactory(handler),
        vault,
        NullLogger<WorkflowWebhookAgentKeyMaterializer>.Instance);

    private static WorkflowCallerNyxIdAuthority CallerAuthority() => new()
    {
        Platform = "nyxid",
        ExternalUserId = "owner-alpha",
        Scope = "proxy",
        BindingId = "binding-alpha",
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
}
