using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdExternalWorkflowCapabilitySourceTests
{
    private const string AdmittedSpec = """
        {
          "openapi": "3.1.0",
          "paths": {
            "/states/{entity_id}": {
              "get": {
                "operationId": "get-state",
                "x-aevatar-tool": { "enabled": true, "readOnly": true },
                "parameters": [
                  { "name": "entity_id", "in": "path", "required": true, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public void AddNyxIdTools_ShouldRegisterNyxIdCapabilitySource()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyxid.invalid");

        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilitySource) &&
            descriptor.ImplementationType == typeof(NyxIdExternalWorkflowCapabilitySource));
    }

    [Fact]
    public async Task ListAsync_ShouldPreserveDuplicateSlugsByExactUserServiceId()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = """
                {
                  "keys": [
                    { "id": "us-home-alpha", "slug": "home-assistant", "label": "Home A", "status": "active", "allowed": true, "credential_source": { "kind": "oauth", "status": "connected", "allowed": true } },
                    { "id": "us-home-beta", "slug": "home-assistant", "label": "Home B", "status": "active", "allowed": true, "credential_source": { "kind": "api_key", "status": "ready", "allowed": true } }
                  ]
                }
                """,
            Specs =
            {
                ["us-home-alpha"] = AdmittedSpec,
                ["us-home-beta"] = AdmittedSpec,
            },
        };
        var source = CreateSource(handler);

        var descriptors = await source.ListAsync(Access(), CancellationToken.None);

        descriptors.Should().HaveCount(2);
        descriptors.Select(static item => item.Capability.NyxIdUserService.UserServiceId)
            .Should().BeEquivalentTo("us-home-alpha", "us-home-beta");
        descriptors.Should().OnlyContain(static item =>
            item.Capability.NyxIdUserService.ServiceSlugSnapshot == "home-assistant" &&
            item.Capability.NyxIdUserService.OperationId == "get-state");
        descriptors.Select(static item => item.ToString()).Should()
            .OnlyContain(text => !text.Contains("runtime-caller-credential", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectAsync_ShouldRequireExactSelection_WhenSlugIsAmbiguous()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = """
                { "keys": [
                  { "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "allowed": true },
                  { "id": "us-home-beta", "slug": "home-assistant", "status": "active", "allowed": true }
                ] }
                """,
        };
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            Access(),
            NyxIdRef(string.Empty, "home-assistant", "get-state", "GET", "/states/{entity_id}", string.Empty),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.SelectionRequired);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("EXACT_USER_SERVICE_SELECTION_REQUIRED");
    }

    [Theory]
    [MemberData(nameof(NonReadyCases))]
    public async Task InspectAsync_ShouldMapPublishedFactsToStableTypedStatus(
        string keysJson,
        string? specJson,
        ExternalCapabilityReadinessStatus expectedStatus,
        string expectedCode)
    {
        var handler = new ReadinessHandler { KeysJson = keysJson };
        if (specJson is not null)
            handler.Specs["us-home-alpha"] = specJson;
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            Access(),
            NyxIdRef(
                "us-home-alpha",
                "home-assistant",
                "get-state",
                "GET",
                "/states/{entity_id}",
                "candidate-contract-digest"),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(expectedStatus);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be(expectedCode);
        result.Remediations.Should().OnlyContain(static item =>
            !item.TrustedLocator.Contains("runtime-caller-credential", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("api_key", "ready", false, "", "")]
    [InlineData("oauth", "connected", false, "", "")]
    [InlineData("direct", "ready", false, "", "")]
    [InlineData("node", "ready", true, "node-home-alpha", "online")]
    public async Task InspectAsync_ShouldBeReadyForSupportedInteractiveCapabilityKinds(
        string credentialKind,
        string credentialStatus,
        bool requiresNode,
        string nodeId,
        string nodeStatus)
    {
        var handler = new ReadinessHandler
        {
            KeysJson = $$"""
                { "keys": [{
                  "id": "us-home-alpha",
                  "slug": "home-assistant",
                  "status": "active",
                  "allowed": true,
                  "requires_node": {{requiresNode.ToString().ToLowerInvariant()}},
                  "node_id": "{{nodeId}}",
                  "node_status": "{{nodeStatus}}",
                  "credential_source": { "kind": "{{credentialKind}}", "status": "{{credentialStatus}}", "allowed": true }
                }] }
                """,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();

        var result = await source.InspectAsync(
            Access(),
            descriptor.Capability,
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.Blockers.Should().BeEmpty();
        result.Sources.Should().HaveCount(2);
    }

    [Fact]
    public async Task InspectAsync_ShouldFailClosedForDurableMode_WhenTopologyProofIsUnavailable()
    {
        var handler = new ReadinessHandler
        {
            KeysJson = ReadyKeys,
            Specs = { ["us-home-alpha"] = AdmittedSpec },
        };
        var source = CreateSource(handler);
        var descriptor = (await source.ListAsync(Access(), CancellationToken.None)).Single();

        var result = await source.InspectAsync(
            Access(),
            descriptor.Capability,
            ExternalCapabilityExecutionMode.Durable,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be("DURABLE_AUTHORIZATION_UNAVAILABLE");
        result.Remediations.Should().ContainSingle().Which.ActionKind
            .Should().Be(ExternalCapabilityRemediationActionKind.UseInteractiveExecution);
    }

    public static TheoryData<string, string?, ExternalCapabilityReadinessStatus, string> NonReadyCases =>
        new()
        {
            {
                """{ "keys": [] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                "USER_SERVICE_NOT_VISIBLE"
            },
            {
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "pending", "allowed": true }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                "USER_SERVICE_NOT_ACTIVE"
            },
            {
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "allowed": false }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "USER_SERVICE_ACCESS_DENIED"
            },
            {
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "allowed": true, "requires_node": true }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.NodeBindingRequired,
                "NODE_BINDING_REQUIRED"
            },
            {
                """{ "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "allowed": true, "requires_node": true, "node_id": "node-home-alpha", "node_status": "offline" }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.NodeUnavailable,
                "NODE_UNAVAILABLE"
            },
            {
                ReadyKeys,
                null,
                ExternalCapabilityReadinessStatus.EndpointContractRequired,
                "OPENAPI_CONTRACT_REQUIRED"
            },
            {
                ReadyKeys,
                """{ "openapi": "3.1.0", "paths": { "/states/{entity_id}": { "get": { "operationId": "get-state" } } } }""",
                ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                "OPERATION_NOT_ALLOWLISTED"
            },
            {
                """{ "fresh_until": "2026-07-21T09:59:00Z", "keys": [{ "id": "us-home-alpha", "slug": "home-assistant", "status": "active", "allowed": true }] }""",
                AdmittedSpec,
                ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SOURCE_STALE"
            },
        };

    private const string ReadyKeys = """
        { "keys": [{
          "id": "us-home-alpha",
          "slug": "home-assistant",
          "status": "active",
          "allowed": true,
          "credential_source": { "kind": "oauth", "status": "connected", "allowed": true }
        }] }
        """;

    private static NyxIdExternalWorkflowCapabilitySource CreateSource(ReadinessHandler handler) =>
        new(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyxid.invalid" },
                new HttpClient(handler)),
            new NyxIdToolOptions { BaseUrl = "https://nyxid.invalid" },
            new FixedTimeProvider());

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new("scope-alpha", "caller-alpha", "runtime-caller-credential");

    private static ExternalWorkflowCapabilityRef NyxIdRef(
        string serviceId,
        string slug,
        string operationId,
        string method,
        string path,
        string digest) =>
        new()
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = serviceId,
                ServiceSlugSnapshot = slug,
                OperationId = operationId,
                HttpMethod = method,
                PathTemplate = path,
                ContractDigest = digest,
            },
        };

    private sealed class ReadinessHandler : HttpMessageHandler
    {
        public string KeysJson { get; init; } = """{ "keys": [] }""";

        public Dictionary<string, string> Specs { get; init; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/v1/keys")
                return Task.FromResult(Json(HttpStatusCode.OK, KeysJson));

            const string prefix = "/api/v1/proxy/services/";
            const string suffix = "/openapi.json";
            if (path.StartsWith(prefix, StringComparison.Ordinal) &&
                path.EndsWith(suffix, StringComparison.Ordinal))
            {
                var serviceId = path[prefix.Length..^suffix.Length];
                return Task.FromResult(Specs.TryGetValue(serviceId, out var spec)
                    ? Json(HttpStatusCode.OK, spec)
                    : Json(HttpStatusCode.NotFound, """{ "detail": "not found" }"""));
            }

            return Task.FromResult(Json(HttpStatusCode.NotFound, """{ "detail": "not found" }"""));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
    }
}
