using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdCodeExecutionRouteAdmissionPreparerTests
{
    [Fact]
    public async Task AdmitAsync_LegacyPersonalRoute_RepairsThenCommitsVerifiedExactProof()
    {
        const string yaml = "name: code-workflow\nsteps: []\n";
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            "{}",
            Inventory("personal", false, true, "proxy:* sandbox:execute"),
            Inventory("personal", false, true, "proxy:* sandbox:execute"));
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        var factory = new TestClientFactory(client);
        var source = new NyxIdCodeExecutionWorkflowCapabilitySource(
            factory,
            options,
            logger: NullLogger<NyxIdCodeExecutionWorkflowCapabilitySource>.Instance);
        var preparer = new NyxIdCodeExecutionRouteAdmissionPreparer(
            new NyxIdCodeExecutionRoutePolicyReconciler(factory),
            options,
            NullLogger<NyxIdCodeExecutionRouteAdmissionPreparer>.Instance);
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "code-workflow/run-code",
            ToolName = "code_execute",
            Selector = Selector(),
        });
        var admission = new WorkflowExternalCapabilityAdmissionService(
            new StaticParser(WorkflowYamlParseResult.Success("code-workflow", dependencies)),
            new ExternalWorkflowCapabilityReadinessService([source]),
            preparers: [preparer]);

        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            Access(),
            yaml,
            new Dictionary<string, string>(),
            "test",
            ExternalCapabilityExecutionMode.Interactive,
            workflowId: "wf-alpha",
            revisionId: "rev-alpha"));

        handler.Requests.Select(static request => request.Method)
            .Should().Equal(HttpMethod.Get, HttpMethod.Put, HttpMethod.Get, HttpMethod.Get);
        var proof = plan.InvocationAdmissions.Should().ContainSingle().Which.Capability.CodeExecution;
        proof.UserServiceId.Should().Be("us-code-alpha");
        proof.CatalogServiceId.Should().Be("catalog-chrono-sandbox");
        plan.SourceStamps.Should().ContainSingle().Which.ContentDigest.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PrepareAsync_PersonalLegacyRoute_ReconcilesDelegationOnlyAndVerifiesReadBack()
    {
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            "{}",
            Inventory("personal", false, true, "proxy:* sandbox:execute"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().HaveCount(3);
        handler.Requests.Select(static request => request.Method)
            .Should().Equal(HttpMethod.Get, HttpMethod.Put, HttpMethod.Get);
        handler.Requests.Should().OnlyContain(static request =>
            request.Authorization == "Bearer source-readable-alpha");
        handler.Requests[1].Uri.Should()
            .Be("https://nyx.example/api/v1/user-services/us-code-alpha");
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        body.RootElement.GetProperty("forward_access_token").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("inject_delegation_token").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("delegation_token_scope").GetString().Should()
            .Be("proxy:* sandbox:execute");
    }

    [Fact]
    public async Task PrepareAsync_CanonicalRoute_IsReadOnly()
    {
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:* sandbox:execute"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareAsync_AlreadyUsableForwardingRoute_RemainsReadOnly()
    {
        var handler = new SequenceHandler(
            Inventory("personal", true, true, "proxy:* sandbox:execute"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareAsync_ReadBackDropsExistingScope_FailsClosed()
    {
        var handler = new SequenceHandler(
            Inventory("personal", false, true, "proxy:*"),
            "{}",
            Inventory("personal", false, true, "sandbox:execute"));
        var preparer = CreatePreparer(handler);

        var act = () => preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        var exception = await act.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("CODE_EXECUTION_ROUTE_REPAIR_UNVERIFIED");
        handler.Requests.Select(static request => request.Method)
            .Should().Equal(HttpMethod.Get, HttpMethod.Put, HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareAsync_SharedOrganizationRoute_DoesNotMutateCallerVisibleState()
    {
        var handler = new SequenceHandler(
            Inventory("org", false, true, "proxy:*"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareAsync_MixedInventory_RepairsOnlyUniquePersonalRoute()
    {
        var handler = new SequenceHandler(
            MixedInventory(personalScope: "proxy:*"),
            "{}",
            MixedInventory(personalScope: "proxy:* sandbox:execute"));
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Select(static request => request.Method)
            .Should().Equal(HttpMethod.Get, HttpMethod.Put, HttpMethod.Get);
        handler.Requests[1].Uri.Should()
            .Be("https://nyx.example/api/v1/user-services/us-code-alpha");
    }

    [Fact]
    public async Task PrepareAsync_MultiplePersonalCandidates_DoesNotGuessMutationTarget()
    {
        var handler = new SequenceHandler(
            """
            {
              "services": [
                {
                  "id": "us-code-alpha",
                  "slug": "chrono-sandbox",
                  "catalog_service_id": "catalog-chrono-sandbox",
                  "is_active": true,
                  "forward_access_token": false,
                  "inject_delegation_token": true,
                  "delegation_token_scope": "proxy:*",
                  "credential_source": { "type": "personal" }
                },
                {
                  "id": "us-code-beta",
                  "slug": "chrono-sandbox",
                  "catalog_service_id": "catalog-chrono-sandbox",
                  "is_active": true,
                  "forward_access_token": false,
                  "inject_delegation_token": true,
                  "delegation_token_scope": "proxy:*",
                  "credential_source": { "type": "personal" }
                }
              ]
            }
            """);
        var preparer = CreatePreparer(handler);

        await preparer.PrepareAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task PrepareAsync_ProxyDelegationWithoutSourceBearer_DoesNotReadOrWrite()
    {
        var handler = new SequenceHandler();
        var preparer = CreatePreparer(handler);
        var access = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.ProxyDelegation("proxy-delegation-alpha"));

        await preparer.PrepareAsync(
            access,
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ReadOnlySourceBearer_DoesNotReadOrWrite()
    {
        var handler = new SequenceHandler();
        var preparer = CreatePreparer(handler);
        var access = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("broker-issued-alpha"));

        await preparer.PrepareAsync(
            access,
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void AddCodeExecutionDelegationScope_PreservesOrderAndRemovesDuplicates()
    {
        NyxIdCodeExecutionRouteResolver.AddCodeExecutionDelegationScope(
                " proxy:*  proxy:* account:read ")
            .Should().Be("proxy:* account:read sandbox:execute");
    }

    private static NyxIdCodeExecutionRouteAdmissionPreparer CreatePreparer(
        SequenceHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        var factory = new TestClientFactory(client);
        return new NyxIdCodeExecutionRouteAdmissionPreparer(
            new NyxIdCodeExecutionRoutePolicyReconciler(factory),
            options,
            NullLogger<NyxIdCodeExecutionRouteAdmissionPreparer>.Instance);
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.DirectUserBearer("source-readable-alpha"));

    private static ExternalWorkflowCapabilitySelector Selector() =>
        new() { CodeExecution = new CodeExecutionSelector() };

    private static string Inventory(
        string credentialSourceType,
        bool forwardAccessToken,
        bool injectDelegationToken,
        string scope)
    {
        object credentialSource = credentialSourceType == "personal"
            ? new { type = "personal" }
            : new
            {
                type = "org",
                org_id = "org-alpha",
                org_name = "Organization Alpha",
                role = "member",
                allowed = true,
            };
        return JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = forwardAccessToken,
                    inject_delegation_token = injectDelegationToken,
                    delegation_token_scope = scope,
                    credential_source = credentialSource,
                },
            },
        });
    }

    private static string MixedInventory(string personalScope) =>
        JsonSerializer.Serialize(new
        {
            services = new object[]
            {
                new
                {
                    id = "us-code-alpha",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = false,
                    inject_delegation_token = true,
                    delegation_token_scope = personalScope,
                    credential_source = new { type = "personal" },
                },
                new
                {
                    id = "us-code-org",
                    slug = "chrono-sandbox",
                    catalog_service_id = "catalog-chrono-sandbox",
                    is_active = true,
                    forward_access_token = false,
                    inject_delegation_token = true,
                    delegation_token_scope = "proxy:*",
                    credential_source = new
                    {
                        type = "org",
                        org_id = "org-alpha",
                        org_name = "Organization Alpha",
                        role = "member",
                        allowed = true,
                    },
                },
            },
        });

    private sealed class TestClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class StaticParser(WorkflowYamlParseResult result) : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) => Task.FromResult(result);

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) => throw new InvalidOperationException("Unexpected inline bundle parse.");
    }

    private sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                body));
            if (_responses.Count == 0)
                throw new InvalidOperationException("Unexpected NyxID request.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responses.Dequeue(),
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? Body);
}
