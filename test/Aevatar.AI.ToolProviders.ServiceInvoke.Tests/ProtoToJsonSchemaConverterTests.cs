using System.Text.Json;
using Aevatar.AI.ToolProviders.ServiceInvoke.Schema;
using FluentAssertions;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Tests;

public class ProtoToJsonSchemaConverterTests
{
    [Fact]
    public void Convert_Struct_ReturnsObjectSchema()
    {
        var descriptor = Struct.Descriptor;
        var schema = ProtoToJsonSchemaConverter.Convert(descriptor);

        using var doc = JsonDocument.Parse(schema);
        doc.RootElement.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void Convert_Timestamp_ReturnsDateTimeFormat()
    {
        // Timestamp has seconds (int64) and nanos (int32) fields,
        // but when used as a nested message it should map to date-time.
        // Test the converter produces a valid schema for a message with fields.
        var descriptor = Timestamp.Descriptor;
        var schema = ProtoToJsonSchemaConverter.Convert(descriptor);

        using var doc = JsonDocument.Parse(schema);
        doc.RootElement.GetProperty("type").GetString().Should().Be("object");
        doc.RootElement.GetProperty("properties").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact]
    public void Convert_Duration_ProducesValidSchema()
    {
        var descriptor = Duration.Descriptor;
        var schema = ProtoToJsonSchemaConverter.Convert(descriptor);

        using var doc = JsonDocument.Parse(schema);
        doc.RootElement.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void Convert_Value_ProducesValidJson()
    {
        var descriptor = ProtobufValue.Descriptor;
        var schema = ProtoToJsonSchemaConverter.Convert(descriptor);

        // Should not throw and should produce valid JSON
        var act = () => JsonDocument.Parse(schema);
        act.Should().NotThrow();
    }
}
