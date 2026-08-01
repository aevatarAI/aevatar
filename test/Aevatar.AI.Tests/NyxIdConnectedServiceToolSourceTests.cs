using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Tests;

public class NyxIdConnectedServiceToolSourceTests
{
    private const string PersonalCredentialSource = """{ "type": "personal" }""";

    private static readonly string[] FixedToolNames =
    [
        "nyxid_service_inventory",
        "nyxid_service_update",
        "nyxid_service_route",
        "nyxid_service_delete",
    ];

    private const string ExactMcpCatalog = """
        {
          "contract_version": "1.0",
          "catalog_digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "user_id": "nyx-user-alpha",
          "services": [
            {
              "service_id": "usvc-alpha",
              "service_name": "Shop",
              "service_slug": "api-shop",
              "is_user_service": true,
              "is_generic_proxy": false,
              "endpoints": [
                {
                  "endpoint_id": "endpoint-alpha",
                  "name": "get_order",
                  "method": "GET",
                  "path": "/orders/{orderId}",
                  "parameters": [
                    { "name": "orderId", "in": "path", "required": true, "schema": { "type": "string" } }
                  ],
                  "request_body_schema": null,
                  "request_content_type": null,
                  "request_body_required": false,
                  "response": {
                    "content_types": ["application/json"],
                    "binary_artifact": false
                  }
                }
              ]
            }
          ],
          "diagnostics": {
            "no_visible_connections": false,
            "unavailable_services": 0,
            "generic_only_services": 0,
            "invalid_contract_services": 0,
            "service_scope_restricted": false,
            "node_scope_restricted": false,
            "node_scope_exclusions_present": false
          }
        }
        """;

    private const string GenericOnlyMcpCatalog = """
        {
          "contract_version": "1.0",
          "catalog_digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "user_id": "nyx-user-alpha",
          "services": [
            {
              "service_id": "generic-alpha",
              "service_name": "Generic Proxy",
              "service_slug": "generic-proxy",
              "is_user_service": true,
              "is_generic_proxy": true,
              "endpoints": [
                {
                  "endpoint_id": "endpoint-generic-alpha",
                  "name": "request",
                  "method": "POST",
                  "path": "/request",
                  "parameters": [],
                  "request_body_schema": { "type": "object" },
                  "request_content_type": "application/json",
                  "request_body_required": true,
                  "response": {
                    "content_types": ["application/json"],
                    "binary_artifact": false
                  }
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task DiscoverToolsAsync_NoBaseUrl_ReturnsEmptyWithoutReadingKeys()
    {
        var handler = new FakeNyxIdHandler();
        var source = CreateSource(handler, baseUrl: null);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.DiscoveryRequests.Should().Be(0);
        handler.McpConfigRequests.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverToolsAsync_NoAccessToken_ReturnsEmptyWithoutReadingKeys()
    {
        var handler = new FakeNyxIdHandler();
        var source = CreateSource(handler);

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.DiscoveryRequests.Should().Be(0);
        handler.McpConfigRequests.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverToolsAsync_ShouldUseMcpCatalogAndFailClosedWithoutDynamicExposurePolicy()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            InstanceWithOpenApiUrl(
                "usvc-alpha",
                "api-shop",
                "svc-shop",
                "https://nyx.test/api/v1/proxy/services/usvc-alpha/openapi.json"));
        handler.McpConfigByToken["user-token"] = ExactMcpCatalog;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(FixedToolNames);
        handler.DiscoveryRequests.Should().Be(1);
        handler.McpConfigRequests.Should().Be(1);
        handler.RawOpenApiRequests.Should().BeEmpty();
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_ShouldLogZeroExposedOperationsWithoutCatalogIdentities()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop"));
        handler.McpConfigByToken["user-token"] = ExactMcpCatalog;
        var logger = new RecordingLogger<NyxIdConnectedServiceToolSource>();
        var source = CreateSource(handler, logger: logger);

        using var scope = PushContext("user-token");
        await source.DiscoverToolsAsync();

        logger.Output.Should().Contain("exposedOperationCount=0");
        logger.Output.Should().NotContain("usvc-alpha").And.NotContain("endpoint-alpha");
        logger.Entries.Should().OnlyContain(static entry => entry.Exception == null);
    }

    [Fact]
    public async Task DiscoverToolsAsync_ConnectedInstanceWithoutOpenApiUrl_StillExposesFixedTools()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop"));
        handler.McpConfigByToken["user-token"] = ExactMcpCatalog;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(FixedToolNames);
        handler.DiscoveryRequests.Should().Be(1);
        handler.McpConfigRequests.Should().Be(1);
        handler.RawOpenApiRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_McpFailure_KeepsFixedTools()
    {
        var handler = new FakeNyxIdHandler { FailMcpConfig = true };
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop"));
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(FixedToolNames);
        handler.McpConfigRequests.Should().Be(1);
        handler.RawOpenApiRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_GenericOnlyMcpCatalog_DoesNotExposeDynamicOperations()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop"));
        handler.McpConfigByToken["user-token"] = GenericOnlyMcpCatalog;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(FixedToolNames);
        handler.McpConfigRequests.Should().Be(1);
        handler.RawOpenApiRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_CallerCancellationDuringMcpRead_Propagates()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop"));
        using var cancellation = new CancellationTokenSource();
        handler.CancelMcpConfigWith = cancellation;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var action = () => source.DiscoverToolsAsync(cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.McpConfigRequests.Should().Be(1);
        handler.RawOpenApiRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_RealCredentialSources_ExposePersonalAndAllowedOrganizationOnly()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal", "api-shop", "svc-personal"),
            Instance("us-org-allowed", "api-shop", "svc-org-allowed", OrganizationCredentialSource(true)),
            Instance("us-org-denied", "api-shop", "svc-org-denied", OrganizationCredentialSource(false)));
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        var inventory = await tools.Single(tool => tool.Name == "nyxid_service_inventory")
            .ExecuteAsync("{}");

        inventory.Should().Contain("us-personal").And.Contain("us-org-allowed");
        inventory.Should().NotContain("us-org-denied");
        using var document = JsonDocument.Parse(inventory);
        var allowedOrganization = document.RootElement.GetProperty("instances").EnumerateArray()
            .Single(instance => instance.GetProperty("userServiceId").GetString() == "us-org-allowed");
        allowedOrganization.GetProperty("credentialSource").GetString()
            .Should().Be("NYX_ID_SERVICE_CREDENTIAL_SOURCE_ORGANIZATION");
        allowedOrganization.GetProperty("credentialAllowed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task DiscoverToolsAsync_DualTokenConflictingIdentity_DropsThatIdentity()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-shared", "api-shop", "svc-personal"));
        handler.KeysByToken["org-token"] = Keys(
            Instance("us-shared", "api-shop", "svc-organization", OrganizationCredentialSource(true)));
        var source = CreateSource(handler);

        using var scope = PushContext("user-token", "org-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.DiscoveryRequests.Should().Be(2);
        handler.McpConfigRequests.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{ \"unknown\": [] }")]
    [InlineData("{ \"keys\": {} }")]
    [InlineData("true")]
    public async Task DiscoverToolsAsync_InvalidKeysResponse_FailsClosedWithoutDownstreamRequests(
        string keysResponse)
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = keysResponse;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.McpConfigRequests.Should().Be(0);
        handler.RawOpenApiRequests.Should().BeEmpty();
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task InventorySource_ConnectedInstanceWithoutOpenApiUrl_IncludesConnection()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        var source = CreateInventorySource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        var result = await tools.Should().ContainSingle().Subject.ExecuteAsync("{}");

        result.Should().Contain("us-personal-7");
        handler.DiscoveryRequests.Should().Be(1);
        handler.McpConfigRequests.Should().Be(0);
        handler.RawOpenApiRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task InventorySource_NoConnections_ExposesEmptyInventory()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys();
        var source = CreateInventorySource(handler);

        using var scope = PushContext("user-token");
        var inventory = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;
        using var schema = JsonDocument.Parse(inventory.ParametersSchema);
        schema.RootElement.GetProperty("properties")
            .TryGetProperty("user_service_id", out _)
            .Should().BeFalse();
        var result = await inventory.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("instances").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task InventorySource_WithArguments_ShouldRejectWithoutBackendRead()
    {
        var handler = new FakeNyxIdHandler();
        var source = CreateInventorySource(handler);

        using var scope = PushContext("user-token");
        var inventory = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;
        var result = await inventory.ExecuteAsync("""{"unexpected":true}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
        handler.DiscoveryRequests.Should().Be(0);
    }

    [Fact]
    public async Task InventorySource_WhenBackendThrows_ShouldReturnSafeUnavailableError()
    {
        const string secret = "inventory-provider-secret";
        var handler = new FakeNyxIdHandler
        {
            DiscoveryException = new HttpRequestException(secret),
        };
        var logger = new RecordingLogger<NyxIdConnectedServiceInventoryToolSource>();
        var source = CreateInventorySource(handler, logger);

        using var scope = PushContext("user-token");
        var inventory = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;
        var result = await inventory.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString()
            .Should().Be("inventory_query_unavailable");
        result.Should().NotContain(secret);
        handler.DiscoveryRequests.Should().Be(1);
        logger.Entries.Should().ContainSingle()
            .Which.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("connected_service_inventory_unavailable");
    }

    [Fact]
    public async Task InventorySource_WhenCallerCancelsBackendRead_ShouldPropagateCancellation()
    {
        var handler = new FakeNyxIdHandler();
        using var cancellation = new CancellationTokenSource();
        handler.CancelDiscoveryWith = cancellation;
        var source = CreateInventorySource(handler);

        using var scope = PushContext("user-token");
        var inventory = (await source.DiscoverToolsAsync()).Should().ContainSingle().Subject;
        var action = () => inventory.ExecuteAsync("{}", cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.DiscoveryRequests.Should().Be(1);
    }

    private static NyxIdConnectedServiceToolSource CreateSource(
        FakeNyxIdHandler handler,
        string? baseUrl = "https://nyx.test",
        ILogger<NyxIdConnectedServiceToolSource>? logger = null)
    {
        var options = new NyxIdToolOptions { BaseUrl = baseUrl };
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler));
        return new NyxIdConnectedServiceToolSource(
            options,
            client,
            new NyxIdServiceInstanceClient(client),
            logger);
    }

    private static NyxIdConnectedServiceInventoryToolSource CreateInventorySource(
        FakeNyxIdHandler handler,
        ILogger<NyxIdConnectedServiceInventoryToolSource>? logger = null)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdConnectedServiceInventoryToolSource(
            options,
            new NyxIdServiceInstanceClient(client),
            logger);
    }

    private static AgentToolContextScope PushContext(
        string userToken,
        string? organizationToken = null) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(userToken, organizationToken, null),
            Request = new AgentToolRequestIdentity("request-alpha", "call-alpha"),
        });

    private static string Instance(
        string id,
        string slug,
        string catalogServiceId,
        string credentialSource = PersonalCredentialSource) => $$"""
        {
          "id": "{{id}}",
          "slug": "{{slug}}",
          "label": "Shop",
          "catalog_service_id": "{{catalogServiceId}}",
          "endpoint_id": "instance-endpoint-alpha",
          "endpoint_url": "https://shop.test",
          "is_active": true,
          "credential_source": {{credentialSource}}
        }
        """;

    private static string InstanceWithOpenApiUrl(
        string id,
        string slug,
        string catalogServiceId,
        string openApiUrl) => $$"""
        {
          "id": "{{id}}",
          "slug": "{{slug}}",
          "label": "Shop",
          "catalog_service_id": "{{catalogServiceId}}",
          "endpoint_id": "instance-endpoint-alpha",
          "endpoint_url": "https://shop.test",
          "openapi_url": "{{openApiUrl}}",
          "is_active": true,
          "credential_source": {{PersonalCredentialSource}}
        }
        """;

    private static string OrganizationCredentialSource(bool allowed) => $$"""
        {
          "type": "org",
          "org_id": "org-alpha",
          "org_name": "Example Org",
          "avatar_url": null,
          "role": "member",
          "allowed": {{allowed.ToString().ToLowerInvariant()}}
        }
        """;

    private static string Keys(params string[] instances) =>
        $$"""{ "keys": [{{string.Join(',', instances)}}] }""";

    private sealed record ProxyRequestRecord(string Method, string Path);

    private sealed record LogEntry(string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public string Output => string.Join('\n', Entries.Select(static entry => entry.Message));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(formatter(state, exception), exception));
    }

    private sealed class FakeNyxIdHandler : HttpMessageHandler
    {
        private const string EmptyMcpCatalog = """
            {
              "contract_version": "1.0",
              "catalog_digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "user_id": "nyx-user-alpha",
              "services": []
            }
            """;

        public Dictionary<string, string> KeysByToken { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> McpConfigByToken { get; } = new(StringComparer.Ordinal);
        public List<string> RawOpenApiRequests { get; } = [];
        public List<string> ExactReads { get; } = [];
        public List<ProxyRequestRecord> ProxyRequests { get; } = [];
        public int DiscoveryRequests { get; private set; }
        public int McpConfigRequests { get; private set; }
        public bool FailMcpConfig { get; init; }
        public Exception? DiscoveryException { get; init; }
        public CancellationTokenSource? CancelDiscoveryWith { get; set; }
        public CancellationTokenSource? CancelMcpConfigWith { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/v1/keys")
            {
                DiscoveryRequests++;
                if (CancelDiscoveryWith is not null)
                {
                    CancelDiscoveryWith.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                if (DiscoveryException is not null)
                    throw DiscoveryException;
                return Task.FromResult(Json(KeysByToken.GetValueOrDefault(token, "[]")));
            }

            if (path == "/api/v1/mcp/config")
            {
                McpConfigRequests++;
                if (CancelMcpConfigWith is not null)
                {
                    CancelMcpConfigWith.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                if (FailMcpConfig)
                    throw new HttpRequestException("mcp_config_failed");
                return Task.FromResult(Json(McpConfigByToken.GetValueOrDefault(token, EmptyMcpCatalog)));
            }

            if (path.StartsWith("/api/v1/proxy/services/", StringComparison.Ordinal) &&
                path.EndsWith("/openapi.json", StringComparison.Ordinal))
            {
                RawOpenApiRequests.Add(path);
                throw new InvalidOperationException("raw_openapi_must_not_be_requested");
            }

            if (request.Method == HttpMethod.Get &&
                path.StartsWith("/api/v1/keys/", StringComparison.Ordinal))
            {
                ExactReads.Add(Uri.UnescapeDataString(path["/api/v1/keys/".Length..]));
            }
            if (path.StartsWith("/api/v1/proxy/", StringComparison.Ordinal))
                ProxyRequests.Add(new ProxyRequestRecord(request.Method.Method, path));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
