using System.Net;
using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData(false, true, "sandbox:execute", true, "CODE_EXECUTION_ROUTE_ACCESS_DENIED")]
    [InlineData(true, false, "sandbox:execute", false, "CODE_EXECUTION_ROUTE_POLICY_MISMATCH")]
    [InlineData(true, true, "wrong:scope", false, "CODE_EXECUTION_ROUTE_POLICY_MISMATCH")]
    public async Task InspectAsync_UnusableCanonicalRoute_ReturnsTypedPlatformBlocker(
        bool allowed,
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
            scope: scope)));

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

    private static NyxIdCodeExecutionWorkflowCapabilitySource CreateSource(string inventory)
    {
        var handler = new InventoryHandler(inventory);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
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

    private static object Service(
        string id,
        string credentialSourceType,
        bool allowed,
        string? catalogServiceId = "catalog-chrono-sandbox",
        bool injectDelegationToken = true,
        string scope = "sandbox:execute",
        bool isActive = true)
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
            slug = "chrono-sandbox",
            catalog_service_id = catalogServiceId,
            is_active = isActive,
            forward_access_token = false,
            inject_delegation_token = injectDelegationToken,
            delegation_token_scope = scope,
            credential_source = credentialSource,
        };
    }

    private sealed class TestClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class InventoryHandler(string inventory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(inventory, Encoding.UTF8, "application/json"),
            });
    }
}
