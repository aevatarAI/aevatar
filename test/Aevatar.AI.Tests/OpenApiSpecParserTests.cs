using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class OpenApiSpecParserTests
{
    private const string MinimalSpec = """
        {
          "openapi": "3.1.0",
          "paths": {
            "/api/v1/users": {
              "get": {
                "operationId": "list_users",
                "summary": "List all users"
              },
              "post": {
                "operationId": "create_user",
                "summary": "Create a new user",
                "requestBody": {
                  "content": { "application/json": { "schema": { "type": "object" } } }
                }
              }
            },
            "/api/v1/users/{id}": {
              "get": {
                "operationId": "get_user",
                "summary": "Get user by ID",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public void ParseSpec_ValidSpec_ReturnsAllOperations()
    {
        var ops = OpenApiSpecParser.ParseSpec(MinimalSpec, "test-service");

        ops.Should().HaveCount(3);
        ops.Should().OnlyContain(o => o.Service == "test-service");
    }

    [Fact]
    public void ParseSpec_ExtractsOperationDetails()
    {
        var ops = OpenApiSpecParser.ParseSpec(MinimalSpec);

        var listUsers = ops.Single(o => o.OperationId == "list_users");
        listUsers.Method.Should().Be("GET");
        listUsers.Path.Should().Be("/api/v1/users");
        listUsers.Summary.Should().Be("List all users");
        listUsers.Parameters.Should().BeNull();
        listUsers.RequestBodySchema.Should().BeNull();
    }

    [Fact]
    public void ParseSpec_ExtractsParameters()
    {
        var ops = OpenApiSpecParser.ParseSpec(MinimalSpec);

        var getUser = ops.Single(o => o.OperationId == "get_user");
        getUser.Parameters.Should().NotBeNull();
        getUser.Parameters.Should().Contain("\"name\"");
    }

    [Fact]
    public void ParseSpec_ExtractsRequestBody()
    {
        var ops = OpenApiSpecParser.ParseSpec(MinimalSpec);

        var createUser = ops.Single(o => o.OperationId == "create_user");
        createUser.RequestBodySchema.Should().NotBeNull();
        createUser.RequestBodySchema.Should().Contain("application/json");
    }

    [Fact]
    public void ParseSpec_FallsBackToMethodPath_WhenNoOperationId()
    {
        var spec = """
            {
              "paths": {
                "/health": {
                  "get": {
                    "summary": "Health check"
                  }
                }
              }
            }
            """;

        var ops = OpenApiSpecParser.ParseSpec(spec);
        ops.Should().HaveCount(1);
        ops[0].OperationId.Should().Be("GET_/health");
    }

    [Fact]
    public void ParseSpec_UsesDescriptionFallback_WhenNoSummary()
    {
        var spec = """
            {
              "paths": {
                "/test": {
                  "get": {
                    "operationId": "test_op",
                    "description": "First line of description\nSecond line"
                  }
                }
              }
            }
            """;

        var ops = OpenApiSpecParser.ParseSpec(spec);
        ops[0].Summary.Should().Be("First line of description");
    }

    [Fact]
    public void ParseSpec_SkipsNonHttpMethods()
    {
        var spec = """
            {
              "paths": {
                "/test": {
                  "parameters": [{ "name": "shared", "in": "query" }],
                  "summary": "Path-level summary",
                  "get": {
                    "operationId": "actual_op",
                    "summary": "Real operation"
                  }
                }
              }
            }
            """;

        var ops = OpenApiSpecParser.ParseSpec(spec);
        ops.Should().HaveCount(1);
        ops[0].OperationId.Should().Be("actual_op");
    }

    [Fact]
    public void ParseSpec_EmptyPaths_ReturnsEmpty()
    {
        var spec = """{ "paths": {} }""";
        OpenApiSpecParser.ParseSpec(spec).Should().BeEmpty();
    }

    [Fact]
    public void ParseSpec_NoPaths_ReturnsEmpty()
    {
        var spec = """{ "openapi": "3.1.0", "info": {} }""";
        OpenApiSpecParser.ParseSpec(spec).Should().BeEmpty();
    }

    [Fact]
    public void ParseSpec_DefaultService_IsNyxid()
    {
        var ops = OpenApiSpecParser.ParseSpec(MinimalSpec);
        ops.Should().OnlyContain(o => o.Service == "nyxid");
    }
}
