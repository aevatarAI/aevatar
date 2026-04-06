using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class LLMResponseFormatTests
{
    // ─── Static instances ───

    [Fact]
    public void Text_IsSingleton()
    {
        var a = LLMResponseFormat.Text;
        var b = LLMResponseFormat.Text;

        a.Should().BeSameAs(b);
        a.Kind.Should().Be(LLMResponseFormatKind.Text);
    }

    [Fact]
    public void JsonObject_IsSingleton()
    {
        var a = LLMResponseFormat.JsonObject;
        var b = LLMResponseFormat.JsonObject;

        a.Should().BeSameAs(b);
        a.Kind.Should().Be(LLMResponseFormatKind.JsonObject);
    }

    // ─── ForJsonSchema(JsonElement) ───

    [Fact]
    public void ForJsonSchema_WithJsonElement_CreatesJsonSchemaFormat()
    {
        var schemaJson = """{"type":"object","properties":{"score":{"type":"number"}}}""";
        var schema = JsonDocument.Parse(schemaJson).RootElement.Clone();

        var format = LLMResponseFormat.ForJsonSchema(schema, "EvalResult", "Evaluation result");

        format.Kind.Should().Be(LLMResponseFormatKind.JsonSchema);
        format.Should().BeOfType<LLMResponseFormatJsonSchema>();

        var jsonSchema = (LLMResponseFormatJsonSchema)format;
        jsonSchema.Schema.GetProperty("type").GetString().Should().Be("object");
        jsonSchema.SchemaName.Should().Be("EvalResult");
        jsonSchema.SchemaDescription.Should().Be("Evaluation result");
    }

    [Fact]
    public void ForJsonSchema_WithoutOptionalParams_DefaultsToNull()
    {
        var schema = JsonDocument.Parse("{}").RootElement.Clone();
        var format = (LLMResponseFormatJsonSchema)LLMResponseFormat.ForJsonSchema(schema);

        format.SchemaName.Should().BeNull();
        format.SchemaDescription.Should().BeNull();
    }

    // ─── ForJsonSchema<T>() ───

    private class EvalResponse
    {
        public double Score { get; set; }
        public string Reason { get; set; } = "";
    }

    [Fact]
    public void ForJsonSchema_Generic_AutoGeneratesSchema()
    {
        var format = LLMResponseFormat.ForJsonSchema<EvalResponse>();

        format.Kind.Should().Be(LLMResponseFormatKind.JsonSchema);
        var jsonSchema = (LLMResponseFormatJsonSchema)format;

        jsonSchema.SchemaName.Should().Be("EvalResponse");
        jsonSchema.Schema.GetProperty("type").GetString().Should().Be("object");
        jsonSchema.Schema.GetProperty("properties").EnumerateObject().Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void ForJsonSchema_Generic_WithCustomName()
    {
        var format = LLMResponseFormat.ForJsonSchema<EvalResponse>("custom_name", "custom description");

        var jsonSchema = (LLMResponseFormatJsonSchema)format;
        jsonSchema.SchemaName.Should().Be("custom_name");
        jsonSchema.SchemaDescription.Should().Be("custom description");
    }

    // ─── LLMRequest.ResponseFormat propagation ───

    [Fact]
    public void LLMRequest_ResponseFormat_IsOptional()
    {
        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
        };

        request.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public void LLMRequest_ResponseFormat_CanBeSet()
    {
        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            ResponseFormat = LLMResponseFormat.ForJsonSchema<EvalResponse>(),
        };

        request.ResponseFormat.Should().NotBeNull();
        request.ResponseFormat!.Kind.Should().Be(LLMResponseFormatKind.JsonSchema);
    }
}
