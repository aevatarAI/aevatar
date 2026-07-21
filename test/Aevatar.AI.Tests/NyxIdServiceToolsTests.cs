using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdServiceToolsTests
{
    private const string OperationSpec = """
        {
          "paths": {
            "/orders/{order_id}": {
              "get": {
                "operationId": "get_order",
                "x-aevatar-tool": true,
                "parameters": [
                  { "name": "order_id", "in": "path", "required": true, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public async Task DiscoverToolsAsync_ShouldExposeFiveFixedToolsAndOneMultiInstanceOperation()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop", true),
            Instance("us-personal-8", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["svc-shop"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(
        [
            "nyxid_service_inventory",
            "nyxid_service_update",
            "nyxid_service_route",
            "nyxid_service_delete",
            "nyxid_service_request",
            "nyxid_service_operation__get_order",
        ]);
        var operation = tools.Single(tool => tool.Name == "nyxid_service_operation__get_order");
        using var schema = JsonDocument.Parse(operation.ParametersSchema);
        schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString()).Should().Contain("user_service_id");
        schema.RootElement.GetProperty("properties").GetProperty("user_service_id")
            .GetProperty("enum").EnumerateArray().Select(static item => item.GetString())
            .Should().BeEquivalentTo("us-personal-7", "us-personal-8");
        operation.ParametersSchema.Should().NotContain("token").And.NotContain("credential");
    }

    [Fact]
    public async Task InventoryTool_ShouldAllowListWithoutSelectingAnInstance()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop", true),
            Instance("us-personal-8", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["svc-shop"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var inventory = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_inventory");

        using var schema = JsonDocument.Parse(inventory.ParametersSchema);
        schema.RootElement.TryGetProperty("required", out var required).Should().BeTrue();
        required.EnumerateArray().Select(static item => item.GetString())
            .Should().NotContain("user_service_id");
        var result = await inventory.ExecuteAsync("{}");
        result.Should().Contain("us-personal-7").And.Contain("us-personal-8");
    }

    [Fact]
    public async Task OperationTool_ShouldRevalidateExactIdentityAndUseEncodedViaOnCatalogRoute()
    {
        var handler = new ServiceHandler();
        var instance = Instance("us/personal 7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us/personal 7"] = instance;
        handler.SpecsByServiceId["svc-shop"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_service_operation__get_order");

        var result = await tool.ExecuteAsync(
            """{ "user_service_id": "us/personal 7", "order_id": "order/9" }""");

        result.Should().Contain("ok");
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("us/personal 7");
        var proxy = handler.ProxyRequests.Should().ContainSingle().Subject;
        proxy.Path.Should().Be("/api/v1/proxy/svc-shop/orders/order%2F9");
        proxy.Query.Should().Be("?_nyxid_via=us%2Fpersonal%207");
    }

    [Fact]
    public async Task RequestTool_ForgedIdentity_ShouldFailBeforeExactReadOrProxy()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(Instance("us-personal-7", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["svc-shop"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_service_request");

        var result = await tool.ExecuteAsync(
            """{ "user_service_id": "us-forged", "method": "GET", "relative_path": "orders" }""");

        result.Should().Contain("identity_not_authorized");
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestTool_InactiveRevalidation_ShouldFailBeforeProxy()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(Instance("us-personal-7", "api-shop", "svc-shop", true));
        handler.ExactKeys["us-personal-7"] = Instance("us-personal-7", "api-shop", "svc-shop", false);
        handler.SpecsByServiceId["svc-shop"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_service_request");

        var result = await tool.ExecuteAsync(
            """{ "user_service_id": "us-personal-7", "method": "GET", "relative_path": "orders" }""");

        result.Should().Contain("identity_revalidation_failed");
        handler.ExactReads.Should().ContainSingle();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task FixedTools_ShouldExposeCodeOwnedApprovalFloor()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(Instance("us-personal-7", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["svc-shop"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        var inventory = tools.Single(tool => tool.Name == "nyxid_service_inventory");
        inventory.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        inventory.IsReadOnly.Should().BeTrue();
        tools.Single(tool => tool.Name == "nyxid_service_update").ApprovalMode
            .Should().Be(ToolApprovalMode.AlwaysRequire);
        tools.Single(tool => tool.Name == "nyxid_service_route").ApprovalMode
            .Should().Be(ToolApprovalMode.AlwaysRequire);
        var delete = tools.Single(tool => tool.Name == "nyxid_service_delete");
        delete.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        delete.IsDestructive.Should().BeTrue();
        var request = tools.Single(tool => tool.Name == "nyxid_service_request");
        request.RequiresApproval("""{ "method": "GET" }""").Should().BeFalse();
        request.RequiresApproval("""{ "method": "POST" }""").Should().BeTrue();
        request.RequiresApproval("{}").Should().BeTrue();
    }

    [Fact]
    public async Task FixedMutationAndRequestTools_ShouldRevalidateAndReturnTypedResults()
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-personal-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-personal-7"] = instance;
        handler.SpecsByServiceId["svc-shop"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        var updateResult = await tools.Single(tool => tool.Name == "nyxid_service_update")
            .ExecuteAsync("""{ "user_service_id": "us-personal-7", "label": "Primary" }""");
        var routeResult = await tools.Single(tool => tool.Name == "nyxid_service_route")
            .ExecuteAsync("""{ "user_service_id": "us-personal-7", "route": "node", "node_id": "node-3" }""");
        var deleteResult = await tools.Single(tool => tool.Name == "nyxid_service_delete")
            .ExecuteAsync("""{ "user_service_id": "us-personal-7" }""");
        var requestResult = await tools.Single(tool => tool.Name == "nyxid_service_request")
            .ExecuteAsync(
                """
                {
                  "user_service_id": "us-personal-7",
                  "method": "POST",
                  "relative_path": "orders",
                  "accept": "application/json",
                  "if_match": "etag-1",
                  "json_body": { "sku": "sku-1" }
                }
                """);

        updateResult.Should().Contain("\"userServiceId\": \"us-personal-7\"")
            .And.Contain("\"accepted\": true");
        routeResult.Should().Contain("\"userServiceId\": \"us-personal-7\"")
            .And.Contain("\"accepted\": true");
        deleteResult.Should().Contain("\"userServiceId\": \"us-personal-7\"")
            .And.Contain("\"deleted\": true");
        requestResult.Should().Contain("\"userServiceId\": \"us-personal-7\"")
            .And.Contain("\"responseJson\"");
        handler.ExactReads.Should().HaveCount(4);
        handler.Requests.Should().ContainSingle(request =>
            request.Method == "PUT" &&
            request.Path == "/api/v1/keys/us-personal-7" &&
            request.Body.Contains("\"label\":\"Primary\"", StringComparison.Ordinal));
        handler.Requests.Should().ContainSingle(request =>
            request.Method == "PUT" &&
            request.Path == "/api/v1/user-services/us-personal-7" &&
            request.Body.Contains("\"node_id\":\"node-3\"", StringComparison.Ordinal));
        handler.Requests.Should().ContainSingle(request =>
            request.Method == "DELETE" && request.Path == "/api/v1/keys/us-personal-7");
        var proxy = handler.Requests.Should().ContainSingle(request =>
            request.Method == "POST" && request.Path == "/api/v1/proxy/svc-shop/orders").Subject;
        proxy.Authorization.Should().Be("user-token");
        proxy.IdempotencyKey.Should().Be("idem-1");
        proxy.Accept.Should().Be("application/json");
        proxy.ContentType.Should().Be("application/json; charset=utf-8");
        proxy.IfMatch.Should().Be("etag-1");
    }

    private static NyxIdConnectedServiceToolSource CreateSource(ServiceHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdConnectedServiceToolSource(options, new NyxIdServiceInstanceClient(client));
    }

    private static AgentToolContextScope PushContext(string userToken) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(userToken, null, null),
            Request = new AgentToolRequestIdentity("request-1", "call-1", "idem-1"),
        });

    private static string Instance(string id, string slug, string serviceId, bool active) => $$"""
        {
          "id": "{{id}}",
          "slug": "{{slug}}",
          "label": "Shop",
          "service_id": "{{serviceId}}",
          "endpoint_id": "endpoint-1",
          "endpoint_url": "https://shop.test",
          "active": {{active.ToString().ToLowerInvariant()}}
        }
        """;

    private static string Keys(params string[] instances) => $"[{string.Join(',', instances)}]";

    private sealed record RequestRecord(
        string Method,
        string Path,
        string Query,
        string Body,
        string Authorization,
        string? IdempotencyKey,
        string? Accept,
        string? ContentType,
        string? IfMatch);

    private sealed class ServiceHandler : HttpMessageHandler
    {
        public Dictionary<string, string> KeysByToken { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ExactKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SpecsByServiceId { get; } = new(StringComparer.Ordinal);
        public List<string> ExactReads { get; } = [];
        public List<RequestRecord> Requests { get; } = [];
        public IEnumerable<RequestRecord> ProxyRequests =>
            Requests.Where(static request => request.Path.StartsWith("/api/v1/proxy/", StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            if (path == "/api/v1/keys")
                return Json(KeysByToken.GetValueOrDefault(token, "[]"));

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/keys/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/api/v1/keys/".Length..]);
                ExactReads.Add(id);
                return ExactKeys.TryGetValue(id, out var instance)
                    ? Json(instance)
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            if (path.StartsWith("/api/v1/proxy/services/", StringComparison.Ordinal) &&
                path.EndsWith("/openapi.json", StringComparison.Ordinal))
            {
                var id = path["/api/v1/proxy/services/".Length..^"/openapi.json".Length];
                return Json(SpecsByServiceId.GetValueOrDefault(id, "{}"));
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new RequestRecord(
                request.Method.Method,
                path,
                request.RequestUri.Query,
                body,
                token,
                request.Headers.TryGetValues("Idempotency-Key", out var idempotencyValues)
                    ? idempotencyValues.Single()
                    : null,
                request.Headers.TryGetValues("Accept", out var acceptValues) ? acceptValues.Single() : null,
                request.Content?.Headers.ContentType?.ToString(),
                request.Headers.TryGetValues("If-Match", out var ifMatchValues) ? ifMatchValues.Single() : null));

            return Json("""{ "ok": true }""");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
