using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdOpenApiReferenceResolverTests
{
    [Fact]
    public void ResolveSchema_PreservesPresenceConstraintsAndLiteralExamples()
    {
        using var document = JsonDocument.Parse("""
            {"components":{"schemas":{"Update":{
              "type":"object","required":["bot_id"],"additionalProperties":false,
              "properties":{
                "bot_id":{"type":"string"},
                "service_ids":{"type":"array","items":{"type":"string"}},
                "nullable":{"type":["string","null"]}},
              "example":{"$ref":"literal-example"},"default":{"service_ids":[]}
            }}},"schema":{"$ref":"#/components/schemas/Update","description":"Use the exact Bot ID."}}
            """);

        var resolved = new NyxIdOpenApiReferenceResolver(document.RootElement)
            .ResolveSchema(document.RootElement.GetProperty("schema"));

        resolved["required"]!.ToJsonString().Should().Be("[\"bot_id\"]");
        resolved["properties"]!["service_ids"]!["default"].Should().BeNull();
        resolved["properties"]!["nullable"]!["type"]!.ToJsonString().Should().Be("[\"string\",\"null\"]");
        resolved["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        resolved["example"]!["$ref"]!.GetValue<string>().Should().Be("literal-example");
        resolved["default"]!.ToJsonString().Should().Be("{\"service_ids\":[]}");
        resolved["description"]!.GetValue<string>().Should().Be("Use the exact Bot ID.");
    }

    [Fact]
    public void ResolveSchema_RejectsConflictingValidationSibling()
    {
        using var document = JsonDocument.Parse("""
            {"components":{"schemas":{"Id":{"type":"string","enum":["allowed"]}}},
             "schema":{"$ref":"#/components/schemas/Id","enum":["other"]}}
            """);
        var resolver = new NyxIdOpenApiReferenceResolver(document.RootElement);

        var act = () => resolver.ResolveSchema(document.RootElement.GetProperty("schema"));

        act.Should().Throw<NyxIdOperationSchemaUnsupportedException>();
    }

    [Fact]
    public void ResolveObject_ResolvesReferencedParameterAndItsSchema()
    {
        using var document = JsonDocument.Parse("""
            {"components":{
              "parameters":{"Alias":{"$ref":"#/components/parameters/Id"},
                "Id":{"name":"registrationId","in":"path","required":true,"schema":{"$ref":"#/components/schemas/Id"}}},
              "schemas":{"Id":{"type":"string"}}},
             "parameter":{"$ref":"#/components/parameters/Alias"}}
            """);
        var resolver = new NyxIdOpenApiReferenceResolver(document.RootElement);

        var parameter = resolver.ResolveObject(document.RootElement.GetProperty("parameter"));

        parameter.GetProperty("required").GetBoolean().Should().BeTrue();
        parameter.GetProperty("name").GetString().Should().Be("registrationId");
        resolver.ResolveSchema(parameter.GetProperty("schema"))["type"]!.GetValue<string>().Should().Be("string");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveSchema_BoundsDepthAndExpansion(bool branching)
    {
        var schemas = new JsonObject { ["End"] = new JsonObject { ["type"] = "string" } };
        var target = "End";
        for (var i = 0; i < (branching ? 16 : 60); i++)
        {
            var reference = new JsonObject { ["$ref"] = $"#/components/schemas/{target}" };
            schemas[$"Level{i}"] = branching
                ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["left"] = reference, ["right"] = reference.DeepClone() } }
                : reference;
            target = $"Level{i}";
        }
        using var document = JsonDocument.Parse(new JsonObject { ["components"] = new JsonObject { ["schemas"] = schemas } }.ToJsonString());
        using var schema = JsonDocument.Parse(new JsonObject { ["$ref"] = $"#/components/schemas/{target}" }.ToJsonString());

        var act = () => new NyxIdOpenApiReferenceResolver(document.RootElement).ResolveSchema(schema.RootElement);

        act.Should().Throw<NyxIdOperationSchemaUnsupportedException>();
    }

    [Fact]
    public void ResolveSchema_BoundsRepeatedLargeReferences()
    {
        var properties = new JsonObject();
        for (var i = 0; i < 128; i++)
            properties[$"field{i}"] = new JsonObject { ["$ref"] = "#/components/schemas/Large" };
        var root = new JsonObject
        {
            ["components"] = new JsonObject { ["schemas"] = new JsonObject
            {
                ["Large"] = new JsonObject { ["type"] = "string", ["description"] = new string('x', 16384) },
            } },
            ["schema"] = new JsonObject { ["type"] = "object", ["properties"] = properties },
        };
        using var document = JsonDocument.Parse(root.ToJsonString());

        var act = () => new NyxIdOpenApiReferenceResolver(document.RootElement)
            .ResolveSchema(document.RootElement.GetProperty("schema"));

        act.Should().Throw<NyxIdOperationSchemaUnsupportedException>();
    }
}
