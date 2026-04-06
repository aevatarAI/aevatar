using System.ComponentModel;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class AgentToolSchemaGeneratorTests
{
    // ─── Test parameter types ───

    private class SimpleParams
    {
        public string Query { get; set; } = "";
        public int MaxResults { get; set; }
    }

    private class OptionalParams
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int? Limit { get; set; }
    }

    private class NestedParams
    {
        public string Id { get; set; } = "";
        public InnerConfig Config { get; set; } = new();

        public class InnerConfig
        {
            public bool Enabled { get; set; }
            public double Threshold { get; set; }
        }
    }

    private class EmptyParams { }

    private class ArrayParams
    {
        public string[] Tags { get; set; } = [];
        public List<int> Scores { get; set; } = [];
    }

    private class DescribedParams
    {
        [Description("The search query to execute")]
        public string Query { get; set; } = "";

        [Description("Maximum number of results to return")]
        public int MaxResults { get; set; }
    }

    // ─── GenerateSchemaString tests ───

    [Fact]
    public void GenerateSchemaString_SimpleType_ReturnsValidJsonSchema()
    {
        var schema = AgentToolSchemaGenerator.GenerateSchemaString<SimpleParams>();

        schema.Should().NotBeNullOrWhiteSpace();
        var doc = JsonDocument.Parse(schema);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("object");
        root.GetProperty("properties").EnumerateObject().Should().HaveCount(2);
    }

    [Fact]
    public void GenerateSchemaString_UsesSnakeCaseNaming()
    {
        var schema = AgentToolSchemaGenerator.GenerateSchemaString<SimpleParams>();
        var doc = JsonDocument.Parse(schema);
        var props = doc.RootElement.GetProperty("properties");

        props.TryGetProperty("query", out _).Should().BeTrue();
        props.TryGetProperty("max_results", out _).Should().BeTrue();
        // Original PascalCase should not appear
        props.TryGetProperty("Query", out _).Should().BeFalse();
        props.TryGetProperty("MaxResults", out _).Should().BeFalse();
    }

    [Fact]
    public void GenerateSchemaString_EmptyType_ReturnsObjectSchema()
    {
        var schema = AgentToolSchemaGenerator.GenerateSchemaString<EmptyParams>();
        var doc = JsonDocument.Parse(schema);

        doc.RootElement.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void GenerateSchemaString_ArrayProperties_IncludesArrayType()
    {
        var schema = AgentToolSchemaGenerator.GenerateSchemaString<ArrayParams>();
        var doc = JsonDocument.Parse(schema);
        var props = doc.RootElement.GetProperty("properties");

        props.GetProperty("tags").GetProperty("type").GetString().Should().Be("array");
        props.GetProperty("scores").GetProperty("type").GetString().Should().Be("array");
    }

    [Fact]
    public void GenerateSchemaString_NestedType_IncludesNestedProperties()
    {
        var schema = AgentToolSchemaGenerator.GenerateSchemaString<NestedParams>();
        var doc = JsonDocument.Parse(schema);
        var props = doc.RootElement.GetProperty("properties");

        props.TryGetProperty("id", out _).Should().BeTrue();
        props.TryGetProperty("config", out _).Should().BeTrue();
    }

    [Fact]
    public void GenerateSchemaString_DescribedType_GeneratesValidSchema()
    {
        var schema = AgentToolSchemaGenerator.GenerateSchemaString<DescribedParams>();
        var doc = JsonDocument.Parse(schema);
        var props = doc.RootElement.GetProperty("properties");

        // Properties exist with correct snake_case naming
        props.TryGetProperty("query", out var queryProp).Should().BeTrue();
        queryProp.GetProperty("type").GetString().Should().Be("string");
        props.TryGetProperty("max_results", out var maxProp).Should().BeTrue();
        maxProp.GetProperty("type").GetString().Should().Be("integer");
    }

    // ─── GenerateSchema (JsonElement) tests ───

    [Fact]
    public void GenerateSchema_ReturnsUsableJsonElement()
    {
        var schema = AgentToolSchemaGenerator.GenerateSchema<SimpleParams>();

        schema.ValueKind.Should().Be(JsonValueKind.Object);
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").EnumerateObject().Should().HaveCount(2);
    }

    [Fact]
    public void GenerateSchema_TypeOverload_MatchesGenericOverload()
    {
        var generic = AgentToolSchemaGenerator.GenerateSchemaString<SimpleParams>();
        var typed = AgentToolSchemaGenerator.GenerateSchemaString(typeof(SimpleParams));

        generic.Should().Be(typed);
    }
}
