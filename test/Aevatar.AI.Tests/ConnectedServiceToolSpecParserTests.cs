using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class ConnectedServiceToolSpecParserTests
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
                  "content": {
                    "application/json": { "schema": { "$ref": "#/components/schemas/SearchQuery" } }
                  }
                }
              }
            },
            "/secret": {
              "get": { "operationId": "secret_op", "summary": "Unmarked" }
            }
          },
          "components": {
            "schemas": {
              "SearchQuery": {
                "type": "object",
                "properties": { "q": { "type": "string" } },
                "required": ["q"]
              }
            }
          }
        }
        """;

    [Fact]
    public void AdmittedOperations_OnlyMarkedOperationsAreEligible()
    {
        var result = OpenApiToolSpecParser.Parse(ShopSpec);

        var admitted = result.AdmittedOperations().Select(o => o.OperationId).ToArray();

        admitted.Should().BeEquivalentTo(["get_order", "search_orders"]);
        admitted.Should().NotContain("secret_op", "operations without an x-aevatar-tool marker are not eligible");
    }

    [Fact]
    public void AdmittedOperations_ServiceLevelMarker_AdmitsAllExceptExplicitOptOut()
    {
        var spec = """
            {
              "x-aevatar-tool": true,
              "paths": {
                "/a": { "get": { "operationId": "a" } },
                "/b": { "get": { "operationId": "b", "x-aevatar-tool": { "enabled": false } } },
                "/c": { "post": { "operationId": "c" } }
              }
            }
            """;

        var admitted = OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Select(o => o.OperationId).ToArray();

        admitted.Should().BeEquivalentTo(["a", "c"]);
        admitted.Should().NotContain("b", "an operation-level enabled:false overrides a service-level allow");
    }

    [Fact]
    public void AdmittedOperations_NoMarkerAnywhere_AdmitsNothing()
    {
        var spec = """
            { "paths": { "/a": { "get": { "operationId": "a" } } } }
            """;

        OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Should().BeEmpty();
    }

    [Fact]
    public void BuildParametersSchema_CoversPathQueryAndInlinedBody()
    {
        var operations = OpenApiToolSpecParser.Parse(ShopSpec).AdmittedOperations().ToArray();

        var getOrder = operations.Single(o => o.OperationId == "get_order");
        using var getSchema = JsonDocument.Parse(getOrder.BuildParametersSchema());
        var getProps = getSchema.RootElement.GetProperty("properties");
        getProps.TryGetProperty("orderId", out _).Should().BeTrue();
        getProps.TryGetProperty("expand", out _).Should().BeTrue();
        getSchema.RootElement.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("orderId");

        var search = operations.Single(o => o.OperationId == "search_orders");
        using var searchSchema = JsonDocument.Parse(search.BuildParametersSchema());
        var searchProps = searchSchema.RootElement.GetProperty("properties");
        searchProps.TryGetProperty("body", out var body).Should().BeTrue();
        // The $ref to components/schemas/SearchQuery is inlined into a self-contained schema.
        body.GetProperty("type").GetString().Should().Be("object");
        body.GetProperty("properties").TryGetProperty("q", out _).Should().BeTrue();
        searchSchema.RootElement.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("body");
    }

    [Fact]
    public void Parse_RecursiveSchemaRef_DoesNotLoopForever()
    {
        var spec = """
            {
              "paths": {
                "/node": {
                  "post": {
                    "operationId": "node",
                    "x-aevatar-tool": true,
                    "requestBody": {
                      "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Node" } } }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Node": {
                    "type": "object",
                    "properties": { "child": { "$ref": "#/components/schemas/Node" } }
                  }
                }
              }
            }
            """;

        var op = OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Single();
        var schema = op.BuildParametersSchema();

        schema.Should().Contain("\"child\"");
        // The cycle collapses to an open object rather than recursing forever.
        schema.Should().NotContain("$ref");
    }

    [Fact]
    public void MarkerParsing_BooleanAndObjectForms()
    {
        AevatarToolMarker.Parse(JsonDocument.Parse("true").RootElement)!.Enabled.Should().BeTrue();
        AevatarToolMarker.Parse(JsonDocument.Parse("false").RootElement)!.Enabled.Should().BeFalse();

        var obj = AevatarToolMarker.Parse(JsonDocument.Parse(
            """{ "enabled": true, "name": "x", "readOnly": true, "destructive": true, "approval": "always" }""").RootElement)!;
        obj.Name.Should().Be("x");
        obj.ReadOnly.Should().BeTrue();
        obj.Destructive.Should().BeTrue();
        obj.Approval.Should().Be("always");
    }

    [Fact]
    public void ToolNaming_IsStableAndProviderSafe()
    {
        var first = ConnectedServiceToolNaming.Build("search_orders");
        var second = ConnectedServiceToolNaming.Build("search_orders");

        first.Should().Be("nyxid_service_operation__search_orders");
        first.Should().StartWith("nyxid_service_operation__");
        first.Should().NotBe("nyxid_service_request");
        first.Should().Be(second, "naming must be deterministic for a given operation");
    }

    [Fact]
    public void ToolNaming_OverlongNameTruncatesWithStableHashSuffix()
    {
        var longOp = new string('x', 200);

        var name = ConnectedServiceToolNaming.Build(longOp);

        name.Length.Should().BeLessThanOrEqualTo(64);
        name.Should().Be(ConnectedServiceToolNaming.Build(longOp), "truncated names stay stable");
        name.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("connect")]
    public void Parse_UnsupportedMethod_ShouldNotAdmitOperation(string method)
    {
        var spec = $$"""
            { "paths": { "/unsafe": { "{{method}}": { "operationId": "unsafe", "x-aevatar-tool": true } } } }
            """;

        OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Should().BeEmpty();
    }

    [Fact]
    public void Parse_RequiredUnapprovedHeader_ShouldNotAdmitOperation()
    {
        const string spec = """
            {
              "paths": {
                "/unsafe": {
                  "get": {
                    "operationId": "unsafe",
                    "x-aevatar-tool": true,
                    "parameters": [
                      { "name": "Authorization", "in": "header", "required": true, "schema": { "type": "string" } }
                    ]
                  }
                }
              }
            }
            """;

        OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Should().BeEmpty();
    }

    [Fact]
    public void Parse_OptionalUnapprovedHeader_ShouldAttenuateHeaderAndKeepOperation()
    {
        const string spec = """
            {
              "paths": {
                "/safe": {
                  "get": {
                    "operationId": "safe",
                    "x-aevatar-tool": true,
                    "parameters": [
                      { "name": "Authorization", "in": "header", "required": false, "schema": { "type": "string" } },
                      { "name": "Accept", "in": "header", "required": false, "schema": { "type": "string" } }
                    ]
                  }
                }
              }
            }
            """;

        var operation = OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Single();

        operation.Parameters.Should().ContainSingle(parameter => parameter.Name == "Accept");
        operation.Parameters.Should().NotContain(parameter => parameter.Name == "Authorization");
        operation.BuildParametersSchema().Should().Contain("Accept").And.NotContain("Authorization");
    }

    [Fact]
    public void Parse_RequiredNonExactJsonBody_ShouldNotAdmitOperation()
    {
        const string spec = """
            {
              "paths": {
                "/unsafe": {
                  "post": {
                    "operationId": "unsafe",
                    "x-aevatar-tool": true,
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/merge-patch+json": { "schema": { "type": "object" } }
                      }
                    }
                  }
                }
              }
            }
            """;

        OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Should().BeEmpty();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "required": true }""")]
    [InlineData("""{ "required": true, "content": [] }""")]
    public void Parse_MalformedOrUnsupportedRequiredRequestBody_ShouldNotAdmitOperation(
        string requestBodyJson)
    {
        var operation = new JsonObject
        {
            ["operationId"] = "unsafe",
            ["x-aevatar-tool"] = true,
            ["requestBody"] = JsonNode.Parse(requestBodyJson),
        };
        var spec = new JsonObject
        {
            ["paths"] = new JsonObject
            {
                ["/unsafe"] = new JsonObject
                {
                    ["post"] = operation,
                },
            },
        };

        OpenApiToolSpecParser.Parse(spec.ToJsonString()).AdmittedOperations().Should().BeEmpty();
    }

    [Fact]
    public void SafeOperation_DestructiveOrAlwaysMarker_ShouldOnlyTightenApproval()
    {
        const string spec = """
            {
              "paths": {
                "/danger": {
                  "get": {
                    "operationId": "danger",
                    "x-aevatar-tool": {
                      "enabled": true,
                      "readOnly": true,
                      "destructive": true,
                      "approval": "always"
                    }
                  }
                }
              }
            }
            """;

        var operation = OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Single();

        operation.IsReadOnly.Should().BeFalse();
        operation.IsDestructive.Should().BeTrue();
        operation.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
    }
}
