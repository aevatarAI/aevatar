using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Tests;

public class NyxIdConnectedServiceToolSourceTests
{
    private const string ShopSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Shop" },
          "paths": {
            "/orders/{orderId}": {
              "get": {
                "operationId": "get_order",
                "summary": "Get order",
                "x-aevatar-tool": true,
                "parameters": [
                  { "name": "orderId", "in": "path", "required": true, "schema": { "type": "string" } },
                  { "name": "expand", "in": "query", "required": false, "schema": { "type": "string" } }
                ]
              }
            },
            "/orders/search": {
              "post": {
                "operationId": "search_orders",
                "summary": "Search orders",
                "x-aevatar-tool": { "enabled": true, "name": "search_orders", "readOnly": true, "approval": "auto" },
                "requestBody": {
                  "required": true,
                  "content": { "application/json": { "schema": { "type": "object", "properties": { "q": { "type": "string" } }, "required": ["q"] } } }
                }
              }
            },
            "/secret": { "get": { "operationId": "secret_op", "summary": "Unmarked" } }
          }
        }
        """;

    [Fact]
    public async Task DiscoverToolsAsync_NoBaseUrl_ReturnsEmpty()
    {
        var handler = new FakeNyxIdHandler();
        var (source, _) = CreateSource(handler, baseUrl: null);

        using var _scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.DiscoveryRequests.Should().Be(0, "an unconfigured base URL must expose no dynamic tools and make no calls");
    }

    [Fact]
    public async Task DiscoverToolsAsync_NoAccessToken_ReturnsEmpty()
    {
        var handler = new FakeNyxIdHandler();
        var (source, _) = CreateSource(handler);

        // No AgentToolContextScope pushed → no token in request context.
        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.DiscoveryRequests.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverToolsAsync_RegistersOnlyMarkedOperations()
    {
        var handler = new FakeNyxIdHandler();
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = ShopSpec;
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo(
            ["nyxid_api-shop__get_order", "nyxid_api-shop__search_orders"]);
        tools.Should().NotContain(t => t.Name.Contains("secret"));
        tools.Should().NotContain(t => t.Name == "nyxid_service_request");
    }

    [Fact]
    public async Task DiscoveredTools_ApprovalAndReadOnlyMetadata()
    {
        var handler = new FakeNyxIdHandler();
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = ShopSpec;
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        var getOrder = tools.Single(t => t.Name == "nyxid_api-shop__get_order");
        getOrder.IsReadOnly.Should().BeTrue("GET operations default to read-only");
        getOrder.ApprovalMode.Should().Be(ToolApprovalMode.Auto);

        var search = tools.Single(t => t.Name == "nyxid_api-shop__search_orders");
        search.IsReadOnly.Should().BeTrue("the marker sets readOnly:true");
        search.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
    }

    [Fact]
    public async Task ExecuteTool_MapsPathQueryAndBodyToProxyRequest()
    {
        var handler = new FakeNyxIdHandler();
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = ShopSpec;
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        var getOrder = tools.Single(t => t.Name == "nyxid_api-shop__get_order");
        await getOrder.ExecuteAsync("""{ "orderId": "o 1", "expand": "items" }""");

        var search = tools.Single(t => t.Name == "nyxid_api-shop__search_orders");
        await search.ExecuteAsync("""{ "body": { "q": "shoes" } }""");

        handler.ProxyRequests.Should().HaveCount(2);

        var getCall = handler.ProxyRequests.Single(r => r.Method == "GET");
        getCall.RelativePath.Should().StartWith("api-shop/orders/");
        getCall.RelativePath.Should().NotContain("{", "the path template parameter must be substituted");
        getCall.Query.Should().Contain("expand=items");
        getCall.Token.Should().Be("user-token");

        var postCall = handler.ProxyRequests.Single(r => r.Method == "POST");
        postCall.RelativePath.Should().Be("api-shop/orders/search");
        using var postBody = JsonDocument.Parse(postCall.Body);
        postBody.RootElement.GetProperty("q").GetString().Should().Be("shoes");
    }

    [Fact]
    public async Task ExecuteTool_ShouldNotLogQueryValues()
    {
        var handler = new FakeNyxIdHandler();
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = ShopSpec;
        var logger = new RecordingLogger<NyxIdConnectedServiceToolSource>();
        var (source, _) = CreateSource(handler, logger: logger);

        using var _scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_api-shop__get_order");

        await tool.ExecuteAsync("""{ "orderId": "o-1", "expand": "query-secret" }""");

        logger.Output.Should()
            .NotContain("query-secret")
            .And.NotContain("expand=");
    }

    [Fact]
    public async Task ExecuteTool_MissingRequiredBody_ReturnsErrorWithoutCallingProxy()
    {
        var handler = new FakeNyxIdHandler();
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = ShopSpec;
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        var search = tools.Single(t => t.Name == "nyxid_api-shop__search_orders");

        var result = await search.ExecuteAsync("{}");

        result.Should().Contain("error").And.Contain("body");
        handler.ProxyRequests.Should().BeEmpty("a missing required operation body must not reach the NyxID proxy");
    }

    [Fact]
    public async Task ExecuteTool_MissingRequiredPathParam_ReturnsErrorWithoutCallingProxy()
    {
        var handler = new FakeNyxIdHandler();
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = ShopSpec;
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        var getOrder = tools.Single(t => t.Name == "nyxid_api-shop__get_order");

        var result = await getOrder.ExecuteAsync("{}");

        result.Should().Contain("error").And.Contain("orderId");
        handler.ProxyRequests.Should().BeEmpty("a missing required path parameter must not reach the proxy");
    }

    [Fact]
    public async Task ExecuteTool_AuthorizationError_ShouldCreateCredentialFreeTypedReceipt()
    {
        var handler = new FakeNyxIdHandler
        {
            ProxyResponseFactory = () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":"unauthorized","error_code":1001,"message":"credential bearer-secret rejected"}""",
                    Encoding.UTF8,
                    "application/json"),
            },
        };
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = ShopSpec;
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_api-shop__get_order");
        var result = await tool.ExecuteAsync("""{ "orderId": "o-1" }""");
        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            """{ "orderId": "o-1" }""",
            result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
        receipt.AuthorizationRequired.Should().NotBeNull();
        receipt.AuthorizationRequired.ServiceSlug.Should().Be("api-shop");
        receipt.AuthorizationRequired.ResourceUri.Should().Be("/orders/o-1");
        receipt.AuthorizationRequired.ReasonCode.Should().Be("NYXID_UNAUTHORIZED");
        receipt.AuthorizationRequired.SafeMessage.Should().NotBeNullOrWhiteSpace();
        receipt.ResultJson.Should().NotBeNullOrWhiteSpace();
        receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("credential");
    }

    [Theory]
    [InlineData("{\"error\":\"forbidden\",\"error_code\":1002,\"message\":\"approval denied bearer-secret\"}")]
    [InlineData("{\"message\":\"ordinary upstream 403 bearer-secret\",\"documentation_url\":\"https://example.test?token=query-secret\"}")]
    public async Task ExecuteTool_ForbiddenFailure_ShouldCreateSafeErrorReceiptWithoutAuthorizationBlocker(
        string responseBody)
    {
        var handler = new FakeNyxIdHandler
        {
            ProxyResponseFactory = () => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            },
        };
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = ShopSpec;
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_api-shop__get_order");
        var result = await tool.ExecuteAsync("""{ "orderId": "o-1" }""");
        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            """{ "orderId": "o-1" }""",
            result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.AuthorizationRequired.Should().BeNull();
        receipt.ErrorCode.Should().Be("NYXID_PROXY_FORBIDDEN");
        receipt.ErrorMessage.Should().Be("The service request was denied.");
        receipt.ResultJson.Should().Contain("NYXID_PROXY_FORBIDDEN");
        receipt.ToString().Should()
            .NotContain("bearer-secret")
            .And.NotContain("credential")
            .And.NotContain("query-secret")
            .And.NotContain("token=");
    }

    [Fact]
    public async Task DiscoverToolsAsync_ToolNameConflict_DropsDuplicate()
    {
        const string conflictSpec = """
            {
              "paths": {
                "/a": { "get": { "operationId": "a", "x-aevatar-tool": { "name": "dup" } } },
                "/b": { "get": { "operationId": "b", "x-aevatar-tool": { "name": "dup" } } }
              }
            }
            """;
        var handler = new FakeNyxIdHandler();
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-shop", "id": "svc-1" }]""";
        handler.SpecsByServiceId["svc-1"] = conflictSpec;
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(t => t.Name).Should().ContainSingle()
            .Which.Should().Be("nyxid_api-shop__dup");
    }

    [Fact]
    public async Task DiscoverToolsAsync_DualToken_RoutesEachServiceThroughItsOwningToken()
    {
        var handler = new FakeNyxIdHandler();
        handler.ServicesByToken["user-token"] = """[{ "slug": "api-user", "id": "svc-u" }]""";
        handler.ServicesByToken["org-token"] = """[{ "slug": "api-org", "id": "svc-o" }]""";
        handler.SpecsByServiceId["svc-u"] = SpecWithPing("ping_user");
        handler.SpecsByServiceId["svc-o"] = SpecWithPing("ping_org");
        var (source, _) = CreateSource(handler);

        using var _scope = PushContext("user-token", "org-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo(
            ["nyxid_api-user__ping_user", "nyxid_api-org__ping_org"]);

        await tools.Single(t => t.Name == "nyxid_api-user__ping_user").ExecuteAsync("{}");
        await tools.Single(t => t.Name == "nyxid_api-org__ping_org").ExecuteAsync("{}");

        handler.ProxyRequests.Single(r => r.RelativePath == "api-user/ping").Token.Should().Be("user-token");
        handler.ProxyRequests.Single(r => r.RelativePath == "api-org/ping").Token.Should().Be("org-token",
            "an org-only service must be proxied with the org token, matching NyxIdProxyTool visibility");
    }

    private static string SpecWithPing(string operationId) => $$"""
        { "paths": { "/ping": { "get": { "operationId": "{{operationId}}", "x-aevatar-tool": true } } } }
        """;

    private static (NyxIdConnectedServiceToolSource Source, NyxIdApiClient Client) CreateSource(
        FakeNyxIdHandler handler,
        string? baseUrl = "https://nyx.test",
        ILogger<NyxIdConnectedServiceToolSource>? logger = null)
    {
        var options = new NyxIdToolOptions { BaseUrl = baseUrl };
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        return (new NyxIdConnectedServiceToolSource(options, client, logger), client);
    }

    private static AgentToolContextScope PushContext(string userToken, string? orgToken = null) =>
        AgentToolContextScope.Push(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials(userToken, orgToken, null),
            AgentToolCallerContext.Empty,
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)));

    private sealed record ProxyRequestRecord(string Method, string RelativePath, string Query, string Body, string Token);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _entries = [];

        public string Output => string.Join('\n', _entries);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add(formatter(state, exception));
    }

    private sealed class FakeNyxIdHandler : HttpMessageHandler
    {
        private const string ProxyPrefix = "/api/v1/proxy/s/";
        private readonly object _lock = new();

        public Dictionary<string, string> ServicesByToken { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SpecsByServiceId { get; } = new(StringComparer.Ordinal);
        public List<ProxyRequestRecord> ProxyRequests { get; } = [];
        public int DiscoveryRequests { get; private set; }
        public Func<HttpResponseMessage>? ProxyResponseFactory { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/api/v1/proxy/services")
            {
                lock (_lock)
                    DiscoveryRequests++;
                return Json(ServicesByToken.TryGetValue(token, out var services) ? services : "[]");
            }

            if (path.StartsWith("/api/v1/proxy/services/", StringComparison.Ordinal) &&
                path.EndsWith("/openapi.json", StringComparison.Ordinal))
            {
                var id = path["/api/v1/proxy/services/".Length..^"/openapi.json".Length];
                return SpecsByServiceId.TryGetValue(id, out var spec)
                    ? Json(spec)
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            if (path.StartsWith(ProxyPrefix, StringComparison.Ordinal))
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
                lock (_lock)
                {
                    ProxyRequests.Add(new ProxyRequestRecord(
                        request.Method.Method,
                        path[ProxyPrefix.Length..],
                        request.RequestUri?.Query ?? string.Empty,
                        body,
                        token));
                }

                return ProxyResponseFactory?.Invoke() ?? Json("""{ "ok": true }""");
            }

            return Json($$"""{ "error": "unexpected", "path": "{{path}}" }""");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
