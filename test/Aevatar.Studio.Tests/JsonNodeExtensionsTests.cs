using System.Text.Json.Nodes;
using Aevatar.Studio.Domain.Studio.Utilities;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class JsonNodeExtensionsTests
{
    [Fact]
    public void DeepCloneNode_ShouldReturnNullForNull()
    {
        JsonNode? node = null;
        node.DeepCloneNode().Should().BeNull();
    }

    [Fact]
    public void DeepCloneNode_ShouldCloneObject()
    {
        var original = new JsonObject { ["key"] = "value" };
        var clone = original.DeepCloneNode();
        clone.Should().NotBeNull();
        clone!.ToJsonString().Should().Be(original.ToJsonString());
    }

    [Fact]
    public void IsComplexValue_ShouldReturnTrueForObject()
    {
        var node = new JsonObject();
        node.IsComplexValue().Should().BeTrue();
    }

    [Fact]
    public void IsComplexValue_ShouldReturnTrueForArray()
    {
        var node = new JsonArray();
        node.IsComplexValue().Should().BeTrue();
    }

    [Fact]
    public void IsComplexValue_ShouldReturnFalseForScalar()
    {
        JsonNode node = JsonValue.Create("hello");
        node.IsComplexValue().Should().BeFalse();
    }

    [Fact]
    public void IsComplexValue_ShouldReturnFalseForNull()
    {
        JsonNode? node = null;
        node.IsComplexValue().Should().BeFalse();
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnNullForNull()
    {
        JsonNode? node = null;
        node.ToWorkflowScalarString().Should().BeNull();
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnStringValue()
    {
        JsonNode node = JsonValue.Create("hello");
        node.ToWorkflowScalarString().Should().Be("hello");
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnBoolAsString()
    {
        JsonNode node = JsonValue.Create(true);
        node.ToWorkflowScalarString().Should().Be("true");

        JsonNode falseNode = JsonValue.Create(false);
        falseNode.ToWorkflowScalarString().Should().Be("false");
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnIntAsString()
    {
        JsonNode node = JsonValue.Create(42);
        node.ToWorkflowScalarString().Should().Be("42");
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnLongAsString()
    {
        JsonNode node = JsonValue.Create(9999999999L);
        node.ToWorkflowScalarString().Should().Be("9999999999");
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnDoubleAsString()
    {
        JsonNode node = JsonValue.Create(3.14);
        node.ToWorkflowScalarString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnJsonForObject()
    {
        var node = new JsonObject { ["key"] = "value" };
        var result = node.ToWorkflowScalarString();
        result.Should().Contain("key");
        result.Should().Contain("value");
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnJsonForArray()
    {
        var node = new JsonArray(JsonValue.Create(1), JsonValue.Create(2));
        var result = node.ToWorkflowScalarString();
        result.Should().Contain("1");
        result.Should().Contain("2");
    }

    [Fact]
    public void ToPlainValue_ShouldReturnNullForNull()
    {
        JsonNode? node = null;
        node.ToPlainValue().Should().BeNull();
    }

    [Fact]
    public void ToPlainValue_ShouldReturnDictionaryForObject()
    {
        var node = new JsonObject { ["key"] = "value" };
        var result = node.ToPlainValue();
        result.Should().BeOfType<Dictionary<string, object?>>();
        ((Dictionary<string, object?>)result!).Should().ContainKey("key");
    }

    [Fact]
    public void ToPlainValue_ShouldReturnListForArray()
    {
        var node = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b"));
        var result = node.ToPlainValue();
        result.Should().BeOfType<List<object?>>();
        ((List<object?>)result!).Should().HaveCount(2);
    }

    [Fact]
    public void ToPlainValue_ShouldReturnScalarForValue()
    {
        JsonNode node = JsonValue.Create("hello");
        var result = node.ToPlainValue();
        result.Should().Be("hello");
    }
}
