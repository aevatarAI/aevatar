using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

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
        "nyxid_service_request",
    ];

    private const string ShopSpec = """
        {
          "openapi": "3.0.0",
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
                "x-aevatar-tool": { "enabled": true, "readOnly": true, "approval": "auto" },
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "properties": { "q": { "type": "string" } },
                        "required": ["q"]
                      }
                    }
                  }
                }
              }
            },
            "/secret": { "get": { "operationId": "secret_op" } }
          }
        }
        """;

    private const string RequiredInputSpec = """
        {
          "openapi": "3.0.0",
          "paths": {
            "/orders/by-status": {
              "get": {
                "operationId": "orders_by_status",
                "x-aevatar-tool": true,
                "parameters": [
                  { "name": "status", "in": "query", "required": true, "schema": { "type": "string" } }
                ]
              }
            },
            "/orders/conditional": {
              "get": {
                "operationId": "conditional_order",
                "x-aevatar-tool": true,
                "parameters": [
                  { "name": "If-Match", "in": "header", "required": true, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;

    private const string MethodAndHeaderSpec = """
        {
          "openapi": "3.0.0",
          "paths": {
            "/method/head": {
              "head": {
                "operationId": "head_probe",
                "x-aevatar-tool": { "enabled": true, "readOnly": true, "approval": "auto" }
              }
            },
            "/method/options": {
              "options": {
                "operationId": "options_probe",
                "x-aevatar-tool": { "enabled": true, "readOnly": true, "approval": "auto" }
              }
            },
            "/method/put": {
              "put": {
                "operationId": "put_probe",
                "x-aevatar-tool": { "enabled": true, "readOnly": true, "approval": "auto" },
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": { "type": "object" }
                    }
                  }
                }
              }
            },
            "/method/patch": {
              "patch": {
                "operationId": "patch_probe",
                "x-aevatar-tool": { "enabled": true, "readOnly": true, "approval": "auto" },
                "parameters": [
                  { "name": "Accept", "in": "header", "required": false, "schema": { "type": "string" } },
                  { "name": "Content-Type", "in": "header", "required": false, "schema": { "type": "string" } },
                  { "name": "If-Match", "in": "header", "required": false, "schema": { "type": "string" } },
                  { "name": "If-None-Match", "in": "header", "required": false, "schema": { "type": "string" } }
                ],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": { "type": "object" }
                    }
                  }
                }
              }
            },
            "/method/delete": {
              "delete": {
                "operationId": "delete_probe",
                "x-aevatar-tool": { "enabled": true, "readOnly": true, "approval": "auto" }
              }
            }
          }
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
    }

    [Fact]
    public async Task DiscoverToolsAsync_NoAccessToken_ReturnsEmptyWithoutReadingKeys()
    {
        var handler = new FakeNyxIdHandler();
        var source = CreateSource(handler);

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.DiscoveryRequests.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverToolsAsync_ExposesFixedToolsAndMarkedOperationsForExactInstances()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        handler.SpecsByServiceId["us-personal-7"] = ShopSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(
            FixedToolNames.Append("nyxid_service_operation__get_order")
                .Append("nyxid_service_operation__search_orders"));
        tools.Should().NotContain(tool => tool.Name.Contains("secret", StringComparison.Ordinal));
        tools.Should().OnlyContain(tool => !tool.Name.Contains("api-shop", StringComparison.Ordinal));
        handler.DiscoveryRequests.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverToolsAsync_RealCredentialSources_ShouldExposePersonalAndAllowedOrgOnly()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal", "api-shop", "svc-personal"),
            Instance("us-org-allowed", "api-shop", "svc-org-allowed", OrganizationCredentialSource(true)),
            Instance("us-org-denied", "api-shop", "svc-org-denied", OrganizationCredentialSource(false)));
        handler.SpecsByServiceId["us-personal"] = SpecWithPing("ping_personal");
        handler.SpecsByServiceId["us-org-allowed"] = SpecWithPing("ping_org_allowed");
        handler.SpecsByServiceId["us-org-denied"] = SpecWithPing("ping_org_denied");
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        var inventory = await tools.Single(tool => tool.Name == "nyxid_service_inventory")
            .ExecuteAsync("{}");

        inventory.Should().Contain("us-personal").And.Contain("us-org-allowed");
        inventory.Should().NotContain("us-org-denied");
        using var inventoryDocument = JsonDocument.Parse(inventory);
        var allowedOrg = inventoryDocument.RootElement.GetProperty("instances").EnumerateArray()
            .Single(instance => instance.GetProperty("userServiceId").GetString() == "us-org-allowed");
        allowedOrg.GetProperty("credentialSource").GetString()
            .Should().Be("NYX_ID_SERVICE_CREDENTIAL_SOURCE_ORGANIZATION");
        allowedOrg.GetProperty("accessTokenSource").GetString()
            .Should().Be("NYX_ID_SERVICE_ACCESS_TOKEN_SOURCE_USER");
        allowedOrg.GetProperty("credentialAllowed").GetBoolean().Should().BeTrue();
        tools.Select(static tool => tool.Name).Should().Contain("nyxid_service_operation__ping_personal")
            .And.Contain("nyxid_service_operation__ping_org_allowed")
            .And.NotContain("nyxid_service_operation__ping_org_denied");
    }

    [Fact]
    public async Task DynamicOperations_EnforceCodeOwnedApprovalFloor()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        handler.SpecsByServiceId["us-personal-7"] = ShopSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        var getOrder = tools.Single(tool => tool.Name == "nyxid_service_operation__get_order");
        getOrder.IsReadOnly.Should().BeTrue();
        getOrder.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        getOrder.RequiresApproval("{}").Should().BeFalse();

        var search = tools.Single(tool => tool.Name == "nyxid_service_operation__search_orders");
        search.IsReadOnly.Should().BeFalse("a POST marker cannot lower the code-owned write floor");
        search.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        search.RequiresApproval("{}").Should().BeTrue();
    }

    [Fact]
    public async Task DynamicOperations_RevalidateExactIdentityAndKeepCatalogAndInstanceIdsSeparate()
    {
        var handler = new FakeNyxIdHandler();
        var instance = Instance("us-personal-7", "api-shop", "svc-shop");
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-personal-7"] = instance;
        handler.SpecsByServiceId["us-personal-7"] = ShopSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        await tools.Single(tool => tool.Name == "nyxid_service_operation__get_order")
            .ExecuteAsync(
                """{ "user_service_id": "us-personal-7", "orderId": "o 1", "expand": "items" }""");
        await tools.Single(tool => tool.Name == "nyxid_service_operation__search_orders")
            .ExecuteAsync(
                """{ "user_service_id": "us-personal-7", "body": { "q": "shoes" } }""");

        handler.ExactReads.Should().Equal("us-personal-7", "us-personal-7");
        handler.ProxyRequests.Should().HaveCount(2);
        var getCall = handler.ProxyRequests.Single(request => request.Method == "GET");
        getCall.Path.Should().Be("/api/v1/proxy/svc-shop/orders/o%201");
        getCall.Query.Should().Be("?expand=items&_nyxid_via=us-personal-7");
        getCall.Token.Should().Be("user-token");
        var postCall = handler.ProxyRequests.Single(request => request.Method == "POST");
        postCall.Path.Should().Be("/api/v1/proxy/svc-shop/orders/search");
        using var body = JsonDocument.Parse(postCall.Body);
        body.RootElement.GetProperty("q").GetString().Should().Be("shoes");
    }

    [Theory]
    [InlineData(
        "head_probe",
        "HEAD",
        "/method/head",
        """{ "user_service_id": "us-personal-7" }""",
        false,
        true,
        false)]
    [InlineData(
        "options_probe",
        "OPTIONS",
        "/method/options",
        """{ "user_service_id": "us-personal-7" }""",
        false,
        true,
        false)]
    [InlineData(
        "put_probe",
        "PUT",
        "/method/put",
        """{ "user_service_id": "us-personal-7", "body": { "value": 1 } }""",
        true,
        false,
        false)]
    [InlineData(
        "patch_probe",
        "PATCH",
        "/method/patch",
        """{ "user_service_id": "us-personal-7", "Accept": "application/json", "Content-Type": "application/json", "If-Match": "etag-a", "If-None-Match": "etag-b", "body": { "value": 1 } }""",
        true,
        false,
        false)]
    [InlineData(
        "delete_probe",
        "DELETE",
        "/method/delete",
        """{ "user_service_id": "us-personal-7" }""",
        false,
        false,
        true)]
    public async Task DynamicOperations_MapAllowedMethodsHeadersAndApprovalFloorThroughExactSourceChain(
        string operationId,
        string expectedMethod,
        string expectedRelativePath,
        string arguments,
        bool expectsBody,
        bool expectedReadOnly,
        bool expectedDestructive)
    {
        var handler = new FakeNyxIdHandler();
        var instance = Instance("us-personal-7", "api-shop", "svc-shop");
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["us-personal-7"] = instance;
        handler.SpecsByServiceId["us-personal-7"] = MethodAndHeaderSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token", idempotencyKey: "idem-dynamic");
        var tool = (await source.DiscoverToolsAsync()).Single(candidate =>
            candidate.Name == $"nyxid_service_operation__{operationId}");
        var result = await tool.ExecuteAsync(arguments);

        result.Should().Contain("ok");
        tool.IsReadOnly.Should().Be(expectedReadOnly);
        tool.ApprovalMode.Should().Be(expectedReadOnly
            ? ToolApprovalMode.NeverRequire
            : ToolApprovalMode.AlwaysRequire);
        tool.RequiresApproval(arguments).Should().Be(!expectedReadOnly);
        tool.IsDestructive.Should().Be(expectedDestructive);
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("us-personal-7");
        var proxy = handler.ProxyRequests.Should().ContainSingle().Subject;
        proxy.Method.Should().Be(expectedMethod);
        proxy.Path.Should().Be($"/api/v1/proxy/svc-shop{expectedRelativePath}");
        proxy.Query.Should().Be("?_nyxid_via=us-personal-7");
        proxy.Accept.Should().Be("application/json");
        proxy.ContentType.Should().Be(expectsBody ? "application/json" : null);
        proxy.IdempotencyKey.Should().Be(expectedReadOnly ? null : "idem-dynamic");
        if (expectsBody)
        {
            using var body = JsonDocument.Parse(proxy.Body);
            body.RootElement.GetProperty("value").GetInt32().Should().Be(1);
        }
        else
        {
            proxy.Body.Should().BeEmpty();
        }

        proxy.IfMatch.Should().Be(expectedMethod == "PATCH" ? "etag-a" : null);
        proxy.IfNoneMatch.Should().Be(expectedMethod == "PATCH" ? "etag-b" : null);
    }

    [Fact]
    public async Task DiscoverToolsAsync_CustomKeyContract_ShouldExposeAndRouteExactOperationBySlug()
    {
        var handler = new FakeNyxIdHandler();
        var instance = CustomInstance("custom-service-7", "custom/api");
        handler.KeysByToken["user-token"] = Keys(instance);
        handler.ExactKeys["custom-service-7"] = instance;
        handler.SpecsByServiceId["custom-service-7"] = ShopSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().Contain(FixedToolNames);
        var operation = tools.Single(tool => tool.Name == "nyxid_service_operation__get_order");
        var result = await operation.ExecuteAsync(
            """{ "user_service_id": "custom-service-7", "orderId": "o/1" }""");

        result.Should().Contain("ok");
        handler.ExactReads.Should().ContainSingle().Which.Should().Be("custom-service-7");
        var proxy = handler.ProxyRequests.Should().ContainSingle().Subject;
        proxy.Path.Should().Be("/api/v1/proxy/s/custom%2Fapi/orders/o%2F1");
        proxy.Query.Should().Be("?_nyxid_via=custom-service-7");
        proxy.Token.Should().Be("user-token");
    }

    [Theory]
    [InlineData("nyxid_service_operation__get_order", "{ \"user_service_id\": \"us-personal-7\" }", "path")]
    [InlineData("nyxid_service_operation__search_orders", "{ \"user_service_id\": \"us-personal-7\" }", "body")]
    public async Task DynamicOperations_MissingRequiredInput_FailsBeforeRevalidation(
        string toolName,
        string arguments,
        string expectedError)
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        handler.SpecsByServiceId["us-personal-7"] = ShopSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync()).Single(candidate => candidate.Name == toolName);
        var result = await tool.ExecuteAsync(arguments);

        result.Should().Contain(expectedError);
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{", "invalid_arguments")]
    [InlineData("[]", "invalid_arguments")]
    [InlineData("{ \"user_service_id\": \"us-forged\", \"orderId\": \"o-1\" }", "identity_not_authorized")]
    public async Task DynamicOperations_InvalidArguments_FailBeforeExactIdentityRead(
        string arguments,
        string expectedError)
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        handler.SpecsByServiceId["us-personal-7"] = ShopSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name == "nyxid_service_operation__get_order");
        var result = await tool.ExecuteAsync(arguments);

        using var response = JsonDocument.Parse(result);
        response.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("nyxid_service_operation__orders_by_status", "missing_required_query_parameter")]
    [InlineData("nyxid_service_operation__conditional_order", "missing_required_header")]
    public async Task DynamicOperations_MissingRequiredQueryOrHeader_FailsBeforeRevalidation(
        string toolName,
        string expectedError)
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        handler.SpecsByServiceId["us-personal-7"] = RequiredInputSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync()).Single(candidate => candidate.Name == toolName);
        var result = await tool.ExecuteAsync("""{ "user_service_id": "us-personal-7" }""");

        using var response = JsonDocument.Parse(result);
        response.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DynamicOperation_UndeclaredPathPlaceholder_FailsBeforeExactReadOrProxy()
    {
        const string unresolvedPathSpec = """
            {
              "paths": {
                "/orders/{id}": {
                  "get": {
                    "operationId": "get_order_with_undeclared_path",
                    "x-aevatar-tool": true
                  }
                }
              }
            }
            """;
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        handler.SpecsByServiceId["us-personal-7"] = unresolvedPathSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tool = (await source.DiscoverToolsAsync())
            .Single(candidate => candidate.Name ==
                "nyxid_service_operation__get_order_with_undeclared_path");
        var result = await tool.ExecuteAsync(
            """{ "user_service_id": "us-personal-7" }""");

        using var response = JsonDocument.Parse(result);
        response.RootElement.GetProperty("error").GetString().Should().Be("unresolved_path_template");
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_DifferentContractsWithSameName_DropsWholeDynamicName()
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
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        handler.SpecsByServiceId["us-personal-7"] = conflictSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(FixedToolNames);
    }

    [Fact]
    public async Task DiscoverToolsAsync_SameOperationAcrossDifferentRoutes_DropsWholeDynamicName()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop-a"),
            Instance("us-personal-8", "api-shop", "svc-shop-b"));
        handler.SpecsByServiceId["us-personal-7"] = ShopSpec;
        handler.SpecsByServiceId["us-personal-8"] = ShopSpec;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(FixedToolNames);
    }

    [Fact]
    public async Task DiscoverToolsAsync_DualTokenRoutesEachExactInstanceThroughItsOwningCredential()
    {
        var handler = new FakeNyxIdHandler();
        var personal = Instance("us-personal-7", "api-shop", "svc-personal");
        var organization = Instance(
            "us-org-9",
            "api-shop",
            "svc-organization",
            OrganizationCredentialSource(true));
        handler.KeysByToken["user-token"] = Keys(personal);
        handler.KeysByToken["org-token"] = Keys(organization);
        handler.ExactKeys["us-personal-7"] = personal;
        handler.ExactKeys["us-org-9"] = organization;
        handler.SpecsByServiceId["us-personal-7"] = SpecWithPing("ping_personal");
        handler.SpecsByServiceId["us-org-9"] = SpecWithPing("ping_organization");
        var source = CreateSource(handler);

        using var scope = PushContext("user-token", "org-token");
        var tools = await source.DiscoverToolsAsync();
        await tools.Single(tool => tool.Name == "nyxid_service_operation__ping_personal")
            .ExecuteAsync("""{ "user_service_id": "us-personal-7" }""");
        await tools.Single(tool => tool.Name == "nyxid_service_operation__ping_organization")
            .ExecuteAsync("""{ "user_service_id": "us-org-9" }""");

        handler.ProxyRequests.Single(request => request.Path.Contains("svc-personal", StringComparison.Ordinal))
            .Token.Should().Be("user-token");
        handler.ProxyRequests.Single(request => request.Path.Contains("svc-organization", StringComparison.Ordinal))
            .Token.Should().Be("org-token");
        handler.ProxyRequests.Select(static request => request.Query).Should().BeEquivalentTo(
            "?_nyxid_via=us-personal-7",
            "?_nyxid_via=us-org-9");
    }

    [Fact]
    public async Task DiscoverToolsAsync_DualTokenConflictingIdentity_ShouldExposeNoToolsForThatId()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-shared", "api-shop", "svc-personal"));
        handler.KeysByToken["org-token"] = Keys(
            Instance("us-shared", "api-shop", "svc-organization", OrganizationCredentialSource(true)));
        handler.SpecsByServiceId["us-shared"] = SpecWithPing("ping_shared");
        var source = CreateSource(handler);

        using var scope = PushContext("user-token", "org-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_OneSpecFails_ShouldKeepFixedToolsAndOtherOperations()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-bad", "api-shop", "svc-shop"),
            Instance("us-good", "api-shop", "svc-shop"));
        handler.FailingSpecIds.Add("us-bad");
        handler.SpecsByServiceId["us-good"] = SpecWithPing("ping_good");
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(
            FixedToolNames.Append("nyxid_service_operation__ping_good"));
        var inventory = await tools.Single(tool => tool.Name == "nyxid_service_inventory")
            .ExecuteAsync("{}");
        inventory.Should().Contain("us-bad").And.Contain("us-good");
    }

    [Fact]
    public async Task DiscoverToolsAsync_CallerCancellationDuringSpecFetch_ShouldPropagate()
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            Instance("us-personal-7", "api-shop", "svc-shop"));
        handler.CancelledSpecIds.Add("us-personal-7");
        var source = CreateSource(handler);
        using var cts = new CancellationTokenSource();
        handler.SpecCancellationSource = cts;

        using var scope = PushContext("user-token");
        var action = () => source.DiscoverToolsAsync(cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{ \"unknown\": [] }")]
    [InlineData("{ \"keys\": {} }")]
    [InlineData("true")]
    public async Task DiscoverToolsAsync_InvalidKeysResponse_FailsClosedWithoutDownstreamRequests(string keysResponse)
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = keysResponse;
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.SpecRequests.Should().BeEmpty();
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ftp://nyx.test/api/v1/proxy/services/us-personal-7/openapi.json")]
    [InlineData("https://nyx.test/api/v1/services/us-personal-7/openapi.json")]
    [InlineData("https://nyx.test/api/v1/proxy/services/us-other/openapi.json")]
    public async Task DiscoverToolsAsync_InvalidOpenApiBinding_FailsClosedWithoutDownstreamRequests(
        string openApiUrl)
    {
        var handler = new FakeNyxIdHandler();
        handler.KeysByToken["user-token"] = Keys(
            InstanceWithOpenApiUrl("us-personal-7", "api-shop", "svc-shop", openApiUrl));
        var source = CreateSource(handler);

        using var scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
        handler.SpecRequests.Should().BeEmpty();
        handler.ExactReads.Should().BeEmpty();
        handler.ProxyRequests.Should().BeEmpty();
    }

    private static string SpecWithPing(string operationId) => $$"""
        { "paths": { "/ping": { "get": { "operationId": "{{operationId}}", "x-aevatar-tool": true } } } }
        """;

    private static NyxIdConnectedServiceToolSource CreateSource(
        FakeNyxIdHandler handler,
        string? baseUrl = "https://nyx.test")
    {
        var options = new NyxIdToolOptions { BaseUrl = baseUrl };
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler));
        return new NyxIdConnectedServiceToolSource(options, new NyxIdServiceInstanceClient(client));
    }

    private static AgentToolContextScope PushContext(
        string userToken,
        string? organizationToken = null,
        string? idempotencyKey = null) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(userToken, organizationToken, null),
            Request = new AgentToolRequestIdentity("request-1", "call-1", idempotencyKey),
        });

    private static string Instance(
        string id,
        string slug,
        string catalogServiceId,
        string credentialSource = PersonalCredentialSource) =>
        InstanceWithOpenApiUrl(
            id,
            slug,
            catalogServiceId,
            $"https://nyx.test/api/v1/proxy/services/{id}/openapi.json",
            credentialSource);

    private static string InstanceWithOpenApiUrl(
        string id,
        string slug,
        string catalogServiceId,
        string openApiUrl,
        string credentialSource = PersonalCredentialSource) => $$"""
        {
          "id": "{{id}}",
          "slug": "{{slug}}",
          "label": "Shop",
          "catalog_service_id": "{{catalogServiceId}}",
          "endpoint_id": "endpoint-1",
          "endpoint_url": "https://shop.test",
          "openapi_url": "{{openApiUrl}}",
          "is_active": true,
          "credential_source": {{credentialSource}}
        }
        """;

    private static string CustomInstance(string id, string slug) => $$"""
        {
          "id": "{{id}}",
          "slug": "{{slug}}",
          "label": "Custom API",
          "endpoint_id": "endpoint-custom",
          "endpoint_url": "https://custom.test",
          "openapi_url": "https://nyx.test/api/v1/proxy/services/{{id}}/openapi.json",
          "source": "custom",
          "is_active": true,
          "credential_source": {{PersonalCredentialSource}}
        }
        """;

    private static string OrganizationCredentialSource(bool allowed) => $$"""
        {
          "type": "org",
          "org_id": "org-1",
          "org_name": "Example Org",
          "avatar_url": null,
          "role": "member",
          "allowed": {{allowed.ToString().ToLowerInvariant()}}
        }
        """;

    private static string Keys(params string[] instances) =>
        $$"""{ "keys": [{{string.Join(',', instances)}}] }""";

    private sealed record ProxyRequestRecord(
        string Method,
        string Path,
        string Query,
        string Body,
        string Token,
        string? Accept,
        string? ContentType,
        string? IfMatch,
        string? IfNoneMatch,
        string? IdempotencyKey);

    private sealed class FakeNyxIdHandler : HttpMessageHandler
    {
        public Dictionary<string, string> KeysByToken { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ExactKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SpecsByServiceId { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailingSpecIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CancelledSpecIds { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource? SpecCancellationSource { get; set; }
        public List<string> SpecRequests { get; } = [];
        public List<string> ExactReads { get; } = [];
        public List<ProxyRequestRecord> ProxyRequests { get; } = [];
        public int DiscoveryRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/v1/keys")
            {
                DiscoveryRequests++;
                return Json(KeysByToken.GetValueOrDefault(token, "[]"));
            }

            if (path.StartsWith("/api/v1/keys/", StringComparison.Ordinal))
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
                SpecRequests.Add(id);
                if (CancelledSpecIds.Contains(id))
                {
                    SpecCancellationSource?.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                if (FailingSpecIds.Contains(id))
                    throw new HttpRequestException("spec_fetch_failed");
                return SpecsByServiceId.TryGetValue(id, out var spec)
                    ? Json(spec)
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            if (path.StartsWith("/api/v1/proxy/", StringComparison.Ordinal))
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
                ProxyRequests.Add(new ProxyRequestRecord(
                    request.Method.Method,
                    path,
                    request.RequestUri?.Query ?? string.Empty,
                    body,
                    token,
                    ReadHeader(request, "Accept"),
                    request.Content?.Headers.ContentType?.MediaType,
                    ReadHeader(request, "If-Match"),
                    ReadHeader(request, "If-None-Match"),
                    ReadHeader(request, "Idempotency-Key")));
                return Json("""{ "ok": true }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        private static string? ReadHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    }
}
