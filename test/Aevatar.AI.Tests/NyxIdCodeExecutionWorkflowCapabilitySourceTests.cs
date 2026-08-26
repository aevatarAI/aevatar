using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdCodeExecutionWorkflowCapabilitySourceTests
{
    [Theory]
    [InlineData("personal")]
    [InlineData("org")]
    public async Task InspectAsync_CanonicalAccessibleRoute_ReturnsExactTypedProof(
        string credentialSourceType)
    {
        var source = CreateSource(Inventory(Service(
            "us-code-alpha",
            credentialSourceType,
            allowed: true)));

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        readiness.SelectedCapability.CapabilityCase.Should()
            .Be(ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution);
        var proof = readiness.SelectedCapability.CodeExecution;
        proof.UserServiceId.Should().Be("us-code-alpha");
        proof.ServiceSlugSnapshot.Should().Be("chrono-sandbox");
        proof.CatalogServiceId.Should().Be("catalog-chrono-sandbox");
        proof.ContractDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeCodeExecutionCapabilityDigest(
                proof.UserServiceId,
                proof.ServiceSlugSnapshot,
                proof.CatalogServiceId));
        proof.AllowedExecutionModes.Should().Equal(
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
        readiness.Sources.Should().ContainSingle()
            .Which.SourceKind.Should().Be(ExternalCapabilitySourceKind.NyxIdUserServices);
    }

    [Fact]
    public async Task InspectAsync_CustomSameSlugCannotShadowCanonicalRoute()
    {
        var source = CreateSource(Inventory(
            Service("us-custom-shadow", "personal", true, catalogServiceId: null),
            Service("us-code-alpha", "org", true)));

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        readiness.SelectedCapability.CodeExecution.UserServiceId.Should().Be("us-code-alpha");
    }

    [Fact]
    public async Task InspectAsync_SharedAndPersonalAliasAreEligible_PrefersSharedCanonicalRoute()
    {
        var routes = Inventory(
            Service("us-code-shared", "personal", true),
            Service(
                "us-code-alias",
                "personal",
                true,
                slug: CodeExecutionContract.PersonalServiceSlug));
        var source = CreateSource(new InventoryHandler(
            routes,
            ExecutionInventory(
                ExecutionService("us-code-shared", CodeExecutionContract.ServiceSlug),
                ExecutionService("us-code-alias", CodeExecutionContract.PersonalServiceSlug))));

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        readiness.SelectedCapability.CodeExecution.UserServiceId.Should().Be("us-code-shared");
        readiness.SelectedCapability.CodeExecution.ServiceSlugSnapshot.Should()
            .Be(CodeExecutionContract.ServiceSlug);
    }

    [Fact]
    public void Resolve_ExactPersonalAlias_RemainsAuthoritative()
    {
        var routes = NyxIdApiAccessResponseParser.ParseUserServiceRoutes(Inventory(
            Service("us-code-shared", "personal", true),
            Service(
                "us-code-alias",
                "personal",
                true,
                slug: CodeExecutionContract.PersonalServiceSlug)));
        var execution = NyxIdApiAccessResponseParser.ParseUserServiceKeys(ExecutionInventory(
            ExecutionService("us-code-shared", CodeExecutionContract.ServiceSlug),
            ExecutionService("us-code-alias", CodeExecutionContract.PersonalServiceSlug)));

        var resolution = NyxIdCodeExecutionRouteResolver.Resolve(
            new NyxIdUserServiceAuthoritySnapshot(routes, execution),
            "us-code-alias");

        resolution.IsReady.Should().BeTrue();
        resolution.Service!.Id.Should().Be("us-code-alias");
    }

    [Fact]
    public void Resolve_DuplicateSharedRoutes_RemainsAmbiguous()
    {
        var routes = NyxIdApiAccessResponseParser.ParseUserServiceRoutes(Inventory(
            Service("us-code-shared-a", "personal", true),
            Service("us-code-shared-b", "personal", true),
            Service(
                "us-code-alias",
                "personal",
                true,
                slug: CodeExecutionContract.PersonalServiceSlug)));
        var execution = NyxIdApiAccessResponseParser.ParseUserServiceKeys(ExecutionInventory(
            ExecutionService("us-code-shared-a", CodeExecutionContract.ServiceSlug),
            ExecutionService("us-code-shared-b", CodeExecutionContract.ServiceSlug),
            ExecutionService("us-code-alias", CodeExecutionContract.PersonalServiceSlug)));

        var resolution = NyxIdCodeExecutionRouteResolver.Resolve(
            new NyxIdUserServiceAuthoritySnapshot(routes, execution));

        resolution.Kind.Should().Be(NyxIdCodeExecutionRouteResolutionKind.Ambiguous);
        resolution.Service.Should().BeNull();
        resolution.EligibleCandidateCount.Should().Be(3);
    }

    [Fact]
    public async Task InspectAsync_ProxyDelegationOnly_UsesDelegatedAccountReadAuthority()
    {
        var handler = new InventoryHandler(Inventory(Service(
            "us-code-alpha",
            "personal",
            allowed: true)));
        var source = CreateSource(handler);
        var access = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.ProxyDelegation("delegation-alpha"));

        var readiness = await source.InspectAsync(
            access,
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        readiness.SelectedCapability.CodeExecution.UserServiceId.Should().Be("us-code-alpha");
        handler.Authorization.Should().Be("Bearer delegation-alpha");
    }

    [Theory]
    [InlineData(false, true, "proxy:* sandbox:execute")]
    [InlineData(true, false, "proxy:* sandbox:execute")]
    [InlineData(true, true, "proxy:*")]
    [InlineData(true, true, "sandbox:execute")]
    [InlineData(true, true, "llm:proxy sandbox:execute")]
    public async Task InspectAsync_RouteMissingEitherCredentialContract_IsRejected(
        bool forwardAccessToken,
        bool injectDelegationToken,
        string scope)
    {
        var source = CreateSource(Inventory(Service(
            "us-code-alpha",
            "personal",
            allowed: true,
            injectDelegationToken: injectDelegationToken,
            scope: scope,
            forwardAccessToken: forwardAccessToken)));

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("CODE_EXECUTION_ROUTE_POLICY_MISMATCH");
    }

    [Fact]
    public async Task InspectAsync_PolicyMismatch_UsesPublicApiForTrustedLocator()
    {
        var source = CreateSource(
            new InventoryHandler(Inventory(Service(
                "us-code-alpha",
                "personal",
                allowed: true,
                injectDelegationToken: true,
                scope: "llm:proxy"))),
            new NyxIdToolOptions
            {
                BaseUrl = "http://nyxid.internal:3001",
                ApiBaseUrl = "https://nyx.example/",
            });

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        var blocker = readiness.Blockers.Should().ContainSingle().Subject;
        blocker.Code.Should().Be("CODE_EXECUTION_ROUTE_POLICY_MISMATCH");
        blocker.SafeMessage.Should()
            .Contain("delegation_token_scope: missing [proxy:*, sandbox:execute]")
            .And.NotContain("forward_access_token")
            .And.NotContain("inject_delegation_token");
        var remediation = readiness.Remediations.Should().ContainSingle().Subject;
        remediation.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.ConnectCredential);
        remediation.TrustedLocator.Should().Be("https://nyx.example");
        readiness.ToString().Should().NotContain("caller-bearer").And.NotContain("us-code-alpha");
    }

    [Fact]
    public async Task InspectAsync_PolicyMismatch_ReportsOnlyObservedFieldDifferences()
    {
        var source = CreateSource(Inventory(Service(
            "us-code-alpha",
            "personal",
            allowed: true,
            injectDelegationToken: false,
            scope: "proxy:*",
            forwardAccessToken: false)));

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        var message = readiness.Blockers.Should().ContainSingle().Which.SafeMessage;
        message.Should()
            .Contain("forward_access_token: false -> true")
            .And.Contain("inject_delegation_token: false -> true")
            .And.Contain("delegation_token_scope: missing [sandbox:execute] -> contains [proxy:*, sandbox:execute]")
            .And.NotContain("caller-bearer")
            .And.NotContain("us-code-alpha");
    }

    [Theory]
    [InlineData(false, true, true, "proxy:* sandbox:execute", true, "CODE_EXECUTION_ROUTE_ACCESS_DENIED")]
    [InlineData(true, false, true, "proxy:* sandbox:execute", false, "CODE_EXECUTION_ROUTE_POLICY_MISMATCH")]
    [InlineData(true, true, false, "proxy:* sandbox:execute", false, "CODE_EXECUTION_ROUTE_POLICY_MISMATCH")]
    [InlineData(true, true, true, "wrong:scope", false, "CODE_EXECUTION_ROUTE_POLICY_MISMATCH")]
    [InlineData(true, true, true, "llm:proxy account:read", false, "CODE_EXECUTION_ROUTE_POLICY_MISMATCH")]
    public async Task InspectAsync_UnusableCanonicalRoute_ReturnsTypedPlatformBlocker(
        bool allowed,
        bool forwardAccessToken,
        bool injectDelegationToken,
        string scope,
        bool organization,
        string expectedCode)
    {
        var source = CreateSource(Inventory(Service(
            "us-code-alpha",
            organization ? "org" : "personal",
            allowed,
            injectDelegationToken: injectDelegationToken,
            scope: scope,
            forwardAccessToken: forwardAccessToken)));

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().NotBe(ExternalCapabilityReadinessStatus.Ready);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should().Be(expectedCode);
        readiness.SelectedCapability.Should().BeNull();
    }

    [Fact]
    public async Task InspectAsync_InactiveCanonicalRoute_ReturnsTypedPlatformBlocker()
    {
        var source = CreateSource(Inventory(Service(
            "us-code-alpha",
            "personal",
            allowed: true,
            isActive: false)));

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("CODE_EXECUTION_ROUTE_INACTIVE");
        readiness.SelectedCapability.Should().BeNull();
    }

    [Fact]
    public async Task InspectAsync_ExecutionInventoryNotReady_PrecedesConvergableRoutePolicyDrift()
    {
        var routeInventory = Inventory(Service(
            "us-code-alpha",
            "personal",
            allowed: true,
            scope: "proxy:*"));
        var handler = new InventoryHandler(
            routeInventory,
            """
            {
              "keys": [{
                "id": "us-code-alpha",
                "slug": "chrono-sandbox",
                "catalog_service_id": "catalog-chrono-sandbox",
                "catalog_service_slug": "chrono-sandbox",
                "is_active": true,
                "status": "expired",
                "connected": true,
                "credential_source": { "type": "personal" }
              }]
            }
            """);
        var source = CreateSource(handler);

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(
            ExternalCapabilityReadinessStatus.CredentialConnectionRequired);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("CODE_EXECUTION_ROUTE_NOT_READY");
    }

    [Fact]
    public async Task InspectAsync_SameSlugFromDifferentCatalogCannotShadowPlatformRoute()
    {
        var routeInventory = Inventory(Service("us-code-alpha", "personal", allowed: true));
        var handler = new InventoryHandler(
            routeInventory,
            """
            {
              "keys": [{
                "id": "us-code-alpha",
                "slug": "chrono-sandbox",
                "catalog_service_id": "catalog-shadow",
                "catalog_service_slug": "other-sandbox",
                "is_active": true,
                "status": "active",
                "connected": true,
                "credential_source": { "type": "personal" }
              }]
            }
            """);
        var source = CreateSource(handler);

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(
            ExternalCapabilityReadinessStatus.ServiceRegistrationRequired);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("CODE_EXECUTION_ROUTE_MISSING");
    }

    [Fact]
    public async Task InspectAsync_UnsupportedAliasForSandboxCatalogCannotBecomePlatformRoute()
    {
        var routeInventory = Inventory(Service(
            "us-code-alias",
            "personal",
            allowed: true,
            slug: "arbitrary-shadow"));
        var handler = new InventoryHandler(
            routeInventory,
            """
            {
              "keys": [{
                "id": "us-code-alias",
                "slug": "arbitrary-shadow",
                "catalog_service_id": "catalog-chrono-sandbox",
                "catalog_service_slug": "chrono-sandbox",
                "is_active": true,
                "status": "active",
                "connected": true,
                "credential_source": { "type": "personal" }
              }]
            }
            """);
        var source = CreateSource(handler);

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive);

        readiness.Status.Should().Be(
            ExternalCapabilityReadinessStatus.ServiceRegistrationRequired);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("CODE_EXECUTION_ROUTE_MISSING");
    }

    [Fact]
    public async Task InspectAsync_DurableWithoutCommittedGrant_DoesNotReportReady()
    {
        var source = CreateSource(Inventory(Service("us-code-alpha", "org", true)));

        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Durable);

        readiness.Status.Should().Be(
            ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("CODE_EXECUTION_DURABLE_AUTHORIZATION_UNAVAILABLE");
        readiness.SelectedCapability.CodeExecution.UserServiceId.Should().Be("us-code-alpha");
    }

    private static NyxIdCodeExecutionWorkflowCapabilitySource CreateSource(string inventory) =>
        CreateSource(new InventoryHandler(inventory));

    private static NyxIdCodeExecutionWorkflowCapabilitySource CreateSource(
        InventoryHandler handler,
        NyxIdToolOptions? configuredOptions = null)
    {
        var options = configuredOptions ?? new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdCodeExecutionWorkflowCapabilitySource(
            new TestClientFactory(client),
            options,
            logger: NullLogger<NyxIdCodeExecutionWorkflowCapabilitySource>.Instance);
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("caller-bearer"));

    private static ExternalWorkflowCapabilitySelector Selector() =>
        new() { CodeExecution = new CodeExecutionSelector() };

    private static string Inventory(params object[] services) =>
        JsonSerializer.Serialize(new { services });

    private static string ExecutionInventory(params object[] services) =>
        JsonSerializer.Serialize(new { keys = services });

    private static object ExecutionService(string id, string slug) => new
    {
        id,
        slug,
        catalog_service_id = "catalog-chrono-sandbox",
        catalog_service_slug = CodeExecutionContract.ServiceSlug,
        is_active = true,
        status = "active",
        connected = true,
        auto_connected = string.Equals(slug, CodeExecutionContract.ServiceSlug, StringComparison.Ordinal),
        credential_source = new { type = "personal" },
    };

    private static object Service(
        string id,
        string credentialSourceType,
        bool allowed,
        string? catalogServiceId = "catalog-chrono-sandbox",
        bool injectDelegationToken = true,
        string scope = "proxy:* sandbox:execute",
        bool isActive = true,
        bool forwardAccessToken = true,
        string slug = "chrono-sandbox")
    {
        var credentialSource = credentialSourceType == "personal"
            ? (object)new { type = "personal" }
            : new
            {
                type = "org",
                org_id = "org-platform",
                org_name = "Aevatar Platform",
                role = "member",
                allowed,
            };
        return new
        {
            id,
            slug,
            catalog_service_id = catalogServiceId,
            is_active = isActive,
            forward_access_token = forwardAccessToken,
            inject_delegation_token = injectDelegationToken,
            delegation_token_scope = scope,
            credential_source = credentialSource,
        };
    }

    private sealed class TestClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class InventoryHandler : HttpMessageHandler
    {
        private readonly string _routeInventory;
        private readonly string _executionInventory;

        public InventoryHandler(string routeInventory, string? executionInventory = null)
        {
            _routeInventory = routeInventory;
            _executionInventory = executionInventory ?? BuildExecutionInventory(routeInventory);
        }

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            var content = request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/user-services" => _routeInventory,
                "/api/v1/keys" => _executionInventory,
                _ => throw new InvalidOperationException("Unexpected NyxID request."),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }

        private static string BuildExecutionInventory(string routeInventory)
        {
            using var document = JsonDocument.Parse(routeInventory);
            var keys = document.RootElement.GetProperty("services")
                .EnumerateArray()
                .Select(static service => new Dictionary<string, object?>
                {
                    ["id"] = service.GetProperty("id").GetString(),
                    ["slug"] = service.GetProperty("slug").GetString(),
                    ["catalog_service_id"] = service.TryGetProperty(
                        "catalog_service_id",
                        out var catalogServiceId)
                        ? catalogServiceId.GetString()
                        : null,
                    ["catalog_service_slug"] = service.GetProperty("slug").GetString(),
                    ["is_active"] = service.GetProperty("is_active").GetBoolean(),
                    ["status"] = "active",
                    ["connected"] = true,
                    ["credential_source"] = service.GetProperty("credential_source").Clone(),
                })
                .ToArray();
            return JsonSerializer.Serialize(new { keys });
        }
    }
}
