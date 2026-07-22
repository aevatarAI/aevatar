using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdServiceToolsTests
{
    private const string PersonalCredentialSource = """{ "type": "personal" }""";

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

    public static TheoryData<NyxIdServiceHeaderName, string, string> RejectedHeaderCases => new()
    {
        { NyxIdServiceHeaderName.Unspecified, "value", "header_not_allowed" },
        { NyxIdServiceHeaderName.ContentType, "text/plain", "unsupported_media_type" },
        { NyxIdServiceHeaderName.ContentType, "application/json", "content_type_without_body" },
        { NyxIdServiceHeaderName.IfMatch, string.Empty, "invalid_conditional_header" },
        { NyxIdServiceHeaderName.IfMatch, new string('x', 1025), "invalid_conditional_header" },
        { NyxIdServiceHeaderName.IfMatch, "etag\r\nInjected: value", "invalid_conditional_header" },
    };

    public static TheoryData<string, bool, bool, bool> RequestMethodCases => new()
    {
        { "HEAD", false, false, false },
        { "OPTIONS", true, false, false },
        { "PUT", true, true, true },
        { "PATCH", true, true, true },
        { "DELETE", true, true, true },
    };

    [Fact]
    public async Task DiscoverToolsAsync_ShouldExposeFiveFixedToolsAndOneMultiInstanceOperation()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop", true),
            Instance("us-personal-8", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        handler.SpecsByServiceId["us-personal-8"] = OperationSpec;
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
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        handler.SpecsByServiceId["us-personal-8"] = OperationSpec;
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
    public async Task InventoryTool_AuthorizedInstanceSelection_ShouldReturnOnlyTheSelectedInstance()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop", true),
            Instance("us-personal-8", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        handler.SpecsByServiceId["us-personal-8"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var inventory = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_inventory");

        var result = await inventory.ExecuteAsync(
            """{ "user_service_id": "us-personal-8" }""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("instances").EnumerateArray()
            .Select(static instance => instance.GetProperty("userServiceId").GetString())
            .Should().ContainSingle().Which.Should().Be("us-personal-8");
    }

    [Fact]
    public async Task InventoryTool_UnauthorizedInstanceSelection_ShouldFailClosed()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var inventory = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_inventory");

        var result = await inventory.ExecuteAsync(
            """{ "user_service_id": "us-forged" }""");

        ErrorCode(result).Should().Be("identity_not_authorized");
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task FixedUpdateTool_InvalidJsonArguments_ShouldFailBeforeExactReadOrMutation(
        string argumentsJson)
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var update = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_update");

        var result = await update.ExecuteAsync(argumentsJson);

        ErrorCode(result).Should().Be("invalid_arguments");
        handler.ExactReads.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationTool_ShouldRevalidateExactIdentityAndUseEncodedViaOnCatalogRoute()
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-personal-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-personal-7"] = instance;
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_service_operation__get_order");

        var result = await tool.ExecuteAsync(
            """{ "user_service_id": "us-personal-7", "order_id": "order/9" }""");

        result.Should().Contain("ok");
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("us-personal-7");
        var proxy = handler.ProxyRequests.Should().ContainSingle().Subject;
        proxy.Path.Should().Be("/api/v1/proxy/svc-shop/orders/order%2F9");
        proxy.Query.Should().Be("?_nyxid_via=us-personal-7");
    }

    [Fact]
    public async Task ProxyExactServiceRequestAsync_ServiceSlugRoute_ShouldEncodeSlugViaAndToken()
    {
        var handler = new ServiceHandler();
        var client = CreateClient(handler);

        await client.ProxyExactServiceRequestAsync(
            "user-token",
            new NyxIdProxyRouteConstraint { ServiceSlug = "custom/api" },
            "custom/id 7",
            "orders/o 1",
            NyxIdServiceHttpMethod.Get,
            [new KeyValuePair<string, string>("expand", "line items")],
            null,
            null,
            CancellationToken.None);

        var request = handler.ProxyRequests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/api/v1/proxy/s/custom%2Fapi/orders/o%201");
        request.Query.Should().Be("?expand=line%20items&_nyxid_via=custom%2Fid%207");
        request.Authorization.Should().Be("user-token");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RequestTool_ForgedIdentity_ShouldFailBeforeExactReadOrProxy()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(Instance("us-personal-7", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
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
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
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

    [Theory]
    [InlineData("changed_source", false)]
    [InlineData("missing_source", true)]
    [InlineData("missing_active", false)]
    [InlineData("org_missing_allowed", true)]
    [InlineData("org_denied", false)]
    public async Task SideEffectTool_InvalidRevalidationFacts_ShouldFailBeforeMutationOrProxy(
        string scenario,
        bool useRequestTool)
    {
        var handler = new ServiceHandler();
        var discovered = scenario.StartsWith("org_", StringComparison.Ordinal)
            ? Instance("us-7", "api-shop", "svc-shop", true, OrganizationCredentialSource(true))
            : Instance("us-7", "api-shop", "svc-shop", true);
        var exact = scenario switch
        {
            "changed_source" => Instance(
                "us-7",
                "api-shop",
                "svc-shop",
                true,
                OrganizationCredentialSource(true)),
            "missing_source" => WithoutProperty(discovered, "credential_source"),
            "missing_active" => WithoutProperty(discovered, "is_active"),
            "org_missing_allowed" => Instance(
                "us-7",
                "api-shop",
                "svc-shop",
                true,
                OrganizationCredentialSource(null)),
            "org_denied" => Instance(
                "us-7",
                "api-shop",
                "svc-shop",
                true,
                OrganizationCredentialSource(false)),
            _ => throw new InvalidOperationException($"Unknown scenario: {scenario}"),
        };
        handler.KeysByToken["user-token"] = Keys(discovered);
        handler.ExactKeys["us-7"] = exact;
        handler.SpecsByServiceId["us-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        var result = useRequestTool
            ? await tools.Single(tool => tool.Name == "nyxid_service_request")
                .ExecuteAsync("""{ "user_service_id": "us-7", "method": "GET", "relative_path": "orders" }""")
            : await tools.Single(tool => tool.Name == "nyxid_service_update")
                .ExecuteAsync("""{ "user_service_id": "us-7", "label": "Changed" }""");

        ResponseErrorCode(result).Should().Be("identity_revalidation_failed");
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("us-7");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("nyxid_service_update", "{ \"user_service_id\": \"us-7\", \"label\": \"Changed\" }")]
    [InlineData("nyxid_service_route", "{ \"user_service_id\": \"us-7\", \"route\": \"node\", \"node_id\": \"node-c\" }")]
    [InlineData("nyxid_service_delete", "{ \"user_service_id\": \"us-7\" }")]
    [InlineData("nyxid_service_request", "{ \"user_service_id\": \"us-7\", \"method\": \"GET\", \"relative_path\": \"orders\" }")]
    [InlineData("nyxid_service_operation__get_order", "{ \"user_service_id\": \"us-7\", \"order_id\": \"order-1\" }")]
    public async Task SideEffectTools_ChangedNodeBinding_ShouldFailBeforeMutationOrProxy(
        string toolName,
        string arguments)
    {
        var handler = new ServiceHandler();
        var discovered = Instance("us-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(discovered);
        handler.ExactKeys["us-7"] = WithNodeId(discovered, "node-b");
        handler.SpecsByServiceId["us-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync()).Single(candidate => candidate.Name == toolName);
        var result = await tool.ExecuteAsync(arguments);

        ResponseErrorCode(result).Should().Be("identity_revalidation_failed");
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("us-7");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://evil.test/orders")]
    [InlineData("orders?expand=items")]
    [InlineData("orders#fragment")]
    [InlineData("orders/../secret")]
    public async Task RequestTool_InvalidRelativePath_ShouldFailBeforeProxy(string relativePath)
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-7"] = instance;
        handler.SpecsByServiceId["us-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_service_request");
        var result = await tool.ExecuteAsync(JsonSerializer.Serialize(new
        {
            user_service_id = "us-7",
            method = "GET",
            relative_path = relativePath,
        }));

        ResponseErrorCode(result).Should().Be("invalid_relative_path");
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestTool_ReservedNyxIdQuery_ShouldFailBeforeProxy()
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-7"] = instance;
        handler.SpecsByServiceId["us-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_service_request");
        var result = await tool.ExecuteAsync(
            """
            {
              "user_service_id": "us-7",
              "method": "GET",
              "relative_path": "orders",
              "query": [{ "name": "_nyxid_via", "value": "forged" }]
            }
            """);

        ResponseErrorCode(result).Should().Be("reserved_query_name");
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(RejectedHeaderCases))]
    public async Task ServiceRequest_RejectedHeader_ShouldFailBeforeProxy(
        NyxIdServiceHeaderName headerName,
        string headerValue,
        string expectedError)
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-7", "api-shop", "svc-shop", true);
        handler.ExactKeys["us-7"] = instance;
        var client = new NyxIdServiceInstanceClient(CreateClient(handler));
        var request = new NyxIdServiceRequest
        {
            UserServiceId = "us-7",
            Method = NyxIdServiceHttpMethod.Get,
            RelativePath = "headers",
        };
        request.Headers.Add(new NyxIdServiceHeader { Name = headerName, Value = headerValue });

        var result = await client.RequestAsync(PersonalBinding(), request, CancellationToken.None);

        ErrorCode(result.ResponseJson).Should().Be(expectedError);
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProxyExactServiceRequestAsync_MissingRouteConstraint_ShouldFailBeforeHttp()
    {
        var handler = new ServiceHandler();
        var client = CreateClient(handler);

        var action = () => client.ProxyExactServiceRequestAsync(
            "user-token",
            new NyxIdProxyRouteConstraint(),
            "us-7",
            "orders",
            NyxIdServiceHttpMethod.Get,
            [],
            null,
            null,
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("missing_route_constraint");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProxyExactServiceRequestAsync_UnsupportedMethod_ShouldFailBeforeHttp()
    {
        var handler = new ServiceHandler();
        var client = CreateClient(handler);

        var action = () => client.ProxyExactServiceRequestAsync(
            "user-token",
            new NyxIdProxyRouteConstraint { CatalogServiceId = "svc-shop" },
            "us-7",
            "orders",
            NyxIdServiceHttpMethod.Unspecified,
            [],
            null,
            null,
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("unsupported_http_method");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FixedTools_ShouldExposeCodeOwnedApprovalFloor()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(Instance("us-personal-7", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
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
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
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

    [Fact]
    public async Task UpdateTool_IsActive_ShouldUseNyxIdWireContract()
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-personal-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-personal-7"] = instance;
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var update = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_update");

        var result = await update.ExecuteAsync(
            """{ "user_service_id": "us-personal-7", "is_active": false }""");

        result.Should().Contain("\"accepted\": true");
        var mutation = handler.Requests.Should().ContainSingle().Subject;
        mutation.Method.Should().Be("PUT");
        mutation.Path.Should().Be("/api/v1/keys/us-personal-7");
        using var body = JsonDocument.Parse(mutation.Body);
        body.RootElement.GetProperty("is_active").GetBoolean().Should().BeFalse();
        body.RootElement.TryGetProperty("active", out _).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateTool_EndpointUrl_ShouldUseExactNyxIdWireContract()
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-personal-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-personal-7"] = instance;
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var update = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_update");

        var result = await update.ExecuteAsync(
            """{ "user_service_id": "us-personal-7", "endpoint_url": "https://api.shop.test/v2" }""");

        result.Should().Contain("\"accepted\": true");
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("us-personal-7");
        var mutation = handler.Requests.Should().ContainSingle().Subject;
        mutation.Method.Should().Be("PUT");
        mutation.Path.Should().Be("/api/v1/keys/us-personal-7");
        using var body = JsonDocument.Parse(mutation.Body);
        body.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("endpoint_url");
        body.RootElement.GetProperty("endpoint_url").GetString()
            .Should().Be("https://api.shop.test/v2");
    }

    [Theory]
    [MemberData(nameof(RequestMethodCases))]
    public async Task RequestTool_PublicHttpMethod_ShouldPreserveTransportAndApprovalContract(
        string method,
        bool shouldSendBody,
        bool shouldAddIdempotencyKey,
        bool requiresApproval)
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-personal-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-personal-7"] = instance;
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var requestTool = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_request");
        var arguments = $$"""
            {
              "user_service_id": "us-personal-7",
              "method": "{{method}}",
              "relative_path": "orders",
              "json_body": { "sku": "sku-1" }
            }
            """;

        requestTool.RequiresApproval(arguments).Should().Be(requiresApproval);
        var result = await requestTool.ExecuteAsync(arguments);

        ResponseErrorCode(result).Should().BeNull();
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("us-personal-7");
        var request = handler.ProxyRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be(method);
        request.Path.Should().Be("/api/v1/proxy/svc-shop/orders");
        request.Authorization.Should().Be("user-token");
        request.IdempotencyKey.Should().Be(shouldAddIdempotencyKey ? "idem-1" : null);
        if (shouldSendBody)
        {
            using var body = JsonDocument.Parse(request.Body);
            body.RootElement.GetProperty("sku").GetString().Should().Be("sku-1");
            request.ContentType.Should().Be("application/json; charset=utf-8");
        }
        else
        {
            request.Body.Should().BeEmpty();
            request.ContentType.Should().BeNull();
        }
    }

    [Fact]
    public async Task RouteTool_Direct_ShouldRevalidateAndClearNodeId()
    {
        var handler = new ServiceHandler();
        var instance = Instance("us-personal-7", "api-shop", "svc-shop", true);
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-personal-7"] = instance;
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var route = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_route");
        var result = await route.ExecuteAsync(
            """{ "user_service_id": "us-personal-7", "route": "direct" }""");

        result.Should().Contain("\"accepted\": true");
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("us-personal-7");
        var mutation = handler.Requests.Should().ContainSingle().Subject;
        mutation.Method.Should().Be("PUT");
        mutation.Path.Should().Be("/api/v1/user-services/us-personal-7");
        using var body = JsonDocument.Parse(mutation.Body);
        body.RootElement.GetProperty("node_id").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task RouteTool_InvalidRoute_ShouldFailBeforeRevalidationOrMutation()
    {
        var handler = new ServiceHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop", true));
        handler.SpecsByServiceId["us-personal-7"] = OperationSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var route = (await source.DiscoverToolsAsync())
            .Single(tool => tool.Name == "nyxid_service_route");
        var result = await route.ExecuteAsync(
            """{ "user_service_id": "us-personal-7", "route": "unsupported" }""");

        ErrorCode(result).Should().Be("invalid_route");
        handler.ExactReads.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    private static NyxIdConnectedServiceToolSource CreateSource(ServiceHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = CreateClient(handler);
        return new NyxIdConnectedServiceToolSource(options, new NyxIdServiceInstanceClient(client));
    }

    private static NyxIdApiClient CreateClient(ServiceHandler handler) =>
        new(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));

    private static NyxIdServiceInstanceBinding PersonalBinding() => new(
        new NyxIdServiceInstance
        {
            UserServiceId = "us-7",
            DisplaySlug = "api-shop",
            Label = "Shop",
            EndpointUrl = "https://shop.test",
            EndpointId = "endpoint-1",
            IsActive = true,
            CatalogServiceId = "svc-shop",
            CredentialSource = NyxIdServiceCredentialSource.Personal,
            AccessTokenSource = NyxIdServiceAccessTokenSource.User,
            ProxySpecServiceId = "us-7",
            RouteConstraint = new NyxIdProxyRouteConstraint { CatalogServiceId = "svc-shop" },
            CredentialAllowed = true,
        },
        "user-token");

    private static AgentToolContextScope PushContext(string userToken) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(userToken, null, null),
            Request = new AgentToolRequestIdentity("request-1", "call-1", "idem-1"),
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
          "endpoint_id": "endpoint-1",
          "endpoint_url": "https://shop.test",
          "openapi_url": "https://nyx.test/api/v1/proxy/services/{{id}}/openapi.json",
          "is_active": {{active.ToString().ToLowerInvariant()}},
          "credential_source": {{credentialSource}}
        }
        """;

    private static string OrganizationCredentialSource(bool? allowed)
    {
        var allowedProperty = allowed.HasValue
            ? $", \"allowed\": {allowed.Value.ToString().ToLowerInvariant()}"
            : string.Empty;
        return $$"""
            {
              "type": "org",
              "org_id": "org-1",
              "org_name": "Example Org",
              "avatar_url": null,
              "role": "member"{{allowedProperty}}
            }
            """;
    }

    private static string WithoutProperty(string json, string propertyName)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root.Remove(propertyName);
        return root.ToJsonString();
    }

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
