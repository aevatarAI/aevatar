using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdServiceToolsTests
{
    private const string PersonalCredentialSource = """{ "type": "personal" }""";

    [Fact]
    public async Task DiscoverToolsAsync_ShouldExposeOnlyFourFixedToolsForExactInstances()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop", true),
            Instance("usvc-beta", "api-shop", "svc-shop", true));
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(
        [
            "nyxid_service_inventory",
            "nyxid_service_update",
            "nyxid_service_route",
            "nyxid_service_delete",
        ]);
        tools.Should().NotContain(tool => tool.Name == "nyxid_service_request");
        tools.Should().NotContain(tool =>
            tool.Name.StartsWith("nyxid_service_operation__", StringComparison.Ordinal));
        using var schema = JsonDocument.Parse(
            tools.Single(tool => tool.Name == "nyxid_service_update").ParametersSchema);
        schema.RootElement.GetProperty("properties").GetProperty("user_service_id")
            .GetProperty("enum").EnumerateArray().Select(static item => item.GetString())
            .Should().BeEquivalentTo("usvc-alpha", "usvc-beta");
    }

    [Fact]
    public async Task InventoryTool_ShouldListOrSelectOnlyAuthorizedExactInstances()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop", true),
            Instance("usvc-beta", "api-shop", "svc-shop", true));
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var inventory = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_inventory");

        var all = await inventory.ExecuteAsync("{}");
        all.Should().Contain("usvc-alpha").And.Contain("usvc-beta");
        var allReceipt = inventory.CreateResultReceipt("call-all", inventory.Name, "{}", all);
        allReceipt.Should().NotBeNull();
        allReceipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        var selected = await inventory.ExecuteAsync(
            """{ "user_service_id": "usvc-beta" }""");
        using var selectedDocument = JsonDocument.Parse(selected);
        selectedDocument.RootElement.GetProperty("instances").EnumerateArray()
            .Select(static instance => instance.GetProperty("userServiceId").GetString())
            .Should().ContainSingle().Which.Should().Be("usvc-beta");

        var forged = await inventory.ExecuteAsync(
            """{ "user_service_id": "usvc-forged" }""");
        ErrorCode(forged).Should().Be("identity_not_authorized");
        var forgedReceipt = inventory.CreateResultReceipt(
            "call-forged", inventory.Name, "{}", forged);
        forgedReceipt.Should().NotBeNull();
        forgedReceipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        forgedReceipt.ErrorCode.Should().Be("NYXID_SERVICE_INVENTORY_FAILED");
        handler.ExactReads.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task UpdateTool_InvalidJsonArguments_ShouldFailBeforeExactReadOrMutation(
        string argumentsJson)
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop", true));
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var update = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_update");
        var result = await update.ExecuteAsync(argumentsJson);

        ErrorCode(result).Should().Be("invalid_arguments");
        handler.ExactReads.Should().BeEmpty();
        handler.MutationRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task FixedTools_ShouldExposeCodeOwnedApprovalFloor()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop", true));
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
    }

    [Theory]
    [InlineData("nyxid_service_update", "{ \"user_service_id\": \"usvc-alpha\", \"label\": \"Changed\" }")]
    [InlineData("nyxid_service_route", "{ \"user_service_id\": \"usvc-alpha\", \"route\": \"node\", \"node_id\": \"node-c\" }")]
    [InlineData("nyxid_service_delete", "{ \"user_service_id\": \"usvc-alpha\" }")]
    public async Task MutationTools_ChangedNodeBinding_ShouldFailBeforeMutation(
        string toolName,
        string arguments)
    {
        var handler = new ServiceHandler();
        var discovered = Instance("usvc-alpha", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(discovered);
        handler.ExactKeys["usvc-alpha"] = WithNodeId(discovered, "node-b");
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync()).Single(candidate => candidate.Name == toolName);
        var result = await tool.ExecuteAsync(arguments);

        ResponseErrorCode(result).Should().Be("identity_revalidation_failed");
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("usvc-alpha");
        handler.MutationRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task MutationTools_ShouldRevalidateAndUseExactNyxIdWireContracts()
    {
        var handler = new ServiceHandler();
        var instance = Instance("usvc-alpha", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["usvc-alpha"] = instance;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        var updateResult = await tools.Single(tool => tool.Name == "nyxid_service_update")
            .ExecuteAsync("""{ "user_service_id": "usvc-alpha", "label": "Primary" }""");
        var routeResult = await tools.Single(tool => tool.Name == "nyxid_service_route")
            .ExecuteAsync(
                """{ "user_service_id": "usvc-alpha", "route": "node", "node_id": "node-3" }""");
        var deleteResult = await tools.Single(tool => tool.Name == "nyxid_service_delete")
            .ExecuteAsync("""{ "user_service_id": "usvc-alpha" }""");

        updateResult.Should().Contain("\"userServiceId\": \"usvc-alpha\"")
            .And.Contain("\"accepted\": true");
        routeResult.Should().Contain("\"userServiceId\": \"usvc-alpha\"")
            .And.Contain("\"accepted\": true");
        deleteResult.Should().Contain("\"userServiceId\": \"usvc-alpha\"")
            .And.Contain("\"deleted\": true");
        handler.ExactReads.Should().HaveCount(3);
        handler.MutationRequests.Should().ContainSingle(request =>
            request.Method == "PUT" &&
            request.Path == "/api/v1/keys/usvc-alpha" &&
            request.Body.Contains("\"label\":\"Primary\"", StringComparison.Ordinal));
        handler.MutationRequests.Should().ContainSingle(request =>
            request.Method == "PUT" &&
            request.Path == "/api/v1/user-services/usvc-alpha" &&
            request.Body.Contains("\"node_id\":\"node-3\"", StringComparison.Ordinal));
        handler.MutationRequests.Should().ContainSingle(request =>
            request.Method == "DELETE" && request.Path == "/api/v1/keys/usvc-alpha");
    }

    [Fact]
    public async Task UpdateTool_IsActive_ShouldUsePublishedNyxIdFieldName()
    {
        var handler = new ServiceHandler();
        var instance = Instance("usvc-alpha", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["usvc-alpha"] = instance;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var update = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_update");
        var result = await update.ExecuteAsync(
            """{ "user_service_id": "usvc-alpha", "is_active": false }""");

        result.Should().Contain("\"accepted\": true");
        using var body = JsonDocument.Parse(handler.MutationRequests.Should().ContainSingle().Subject.Body);
        body.RootElement.GetProperty("is_active").GetBoolean().Should().BeFalse();
        body.RootElement.TryGetProperty("active", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("endpoint_url", "https://api.shop.test/v2")]
    [InlineData("openapi_spec_url", "https://contracts.example.test/shop-openapi.json")]
    [InlineData("openapi_spec_url", "")]
    public async Task UpdateTool_OptionalString_ShouldUseExactNyxIdWireContract(
        string field,
        string value)
    {
        var handler = new ServiceHandler();
        var instance = Instance("usvc-alpha", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["usvc-alpha"] = instance;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var update = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_update");
        var result = await update.ExecuteAsync(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["user_service_id"] = "usvc-alpha",
            [field] = value,
        }));

        result.Should().Contain("\"accepted\": true");
        using var body = JsonDocument.Parse(handler.MutationRequests.Should().ContainSingle().Subject.Body);
        body.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().Equal(field);
        body.RootElement.GetProperty(field).GetString().Should().Be(value);
    }

    [Fact]
    public async Task RouteTool_Direct_ShouldRevalidateAndClearNodeId()
    {
        var handler = new ServiceHandler();
        var instance = Instance("usvc-alpha", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["usvc-alpha"] = instance;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var route = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_route");
        var result = await route.ExecuteAsync(
            """{ "user_service_id": "usvc-alpha", "route": "direct" }""");

        result.Should().Contain("\"accepted\": true");
        using var body = JsonDocument.Parse(handler.MutationRequests.Should().ContainSingle().Subject.Body);
        body.RootElement.GetProperty("node_id").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task RouteTool_InvalidRoute_ShouldFailBeforeRevalidationOrMutation()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("usvc-alpha", "api-shop", "svc-shop", true));
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var route = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_route");
        var result = await route.ExecuteAsync(
            """{ "user_service_id": "usvc-alpha", "route": "unsupported" }""");

        ErrorCode(result).Should().Be("invalid_route");
        handler.ExactReads.Should().BeEmpty();
        handler.MutationRequests.Should().BeEmpty();
    }

    private static NyxIdConnectedServiceToolSource CreateSource(ServiceHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdConnectedServiceToolSource(
            options,
            client,
            new NyxIdServiceInstanceClient(client));
    }

    private static AgentToolContextScope PushContext(string userToken) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(userToken, null, null),
            Request = new AgentToolRequestIdentity("request-alpha", "call-alpha", "idem-alpha"),
        });

    private static string Instance(
        string id,
        string slug,
        string serviceId,
        bool active,
        string credentialSource = PersonalCredentialSource) => $$"""
        {
          "id": "{{id}}",
          "slug": "{{slug}}",
          "label": "Shop",
          "catalog_service_id": "{{serviceId}}",
          "endpoint_id": "instance-endpoint-alpha",
          "endpoint_url": "https://shop.test",
          "openapi_url": "https://nyx.test/api/v1/proxy/services/{{id}}/openapi.json",
          "is_active": {{active.ToString().ToLowerInvariant()}},
          "credential_source": {{credentialSource}}
        }
        """;

    private static string WithNodeId(string json, string nodeId)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["node_id"] = nodeId;
        return root.ToJsonString();
    }

    private static string Keys(params string[] instances) =>
        $$"""{ "keys": [{{string.Join(',', instances)}}] }""";

    private static string? ResponseErrorCode(string toolResult)
    {
        using var result = JsonDocument.Parse(toolResult);
        return ErrorCode(result.RootElement.GetProperty("responseJson").GetString()!);
    }

    private static string? ErrorCode(string responseJson)
    {
        using var response = JsonDocument.Parse(responseJson);
        return response.RootElement.TryGetProperty("error", out var error)
            ? error.GetString()
            : null;
    }

    private sealed record RequestRecord(string Method, string Path, string Body);

    private sealed class ServiceHandler : HttpMessageHandler
    {
        public Dictionary<string, string> KeysByToken { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ExactKeys { get; } = new(StringComparer.Ordinal);
        public List<string> ExactReads { get; } = [];
        public List<RequestRecord> MutationRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            if (path == "/api/v1/keys")
                return Json(KeysByToken.GetValueOrDefault(token, "[]"));
            if (path == "/api/v1/mcp/config")
                return Json("""
                    {
                      "contract_version": "1.0",
                      "catalog_digest": "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                      "user_id": "nyx-user-alpha",
                      "services": []
                    }
                    """);
            if (request.Method == HttpMethod.Get &&
                path.StartsWith("/api/v1/keys/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/api/v1/keys/".Length..]);
                ExactReads.Add(id);
                return ExactKeys.TryGetValue(id, out var instance)
                    ? Json(instance)
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            MutationRequests.Add(new RequestRecord(request.Method.Method, path, body));
            return Json("""{ "ok": true }""");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
