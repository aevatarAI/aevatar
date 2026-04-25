using System.Text.Json;
using Aevatar.GAgentService.Hosting.Serialization;
using Aevatar.GAgentService.Integration.Tests.Protos;
using Google.Protobuf;

namespace Aevatar.GAgentService.Integration.Tests;

public class JsonToProtoTests
{
    private static byte[] Encode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonToProto.WriteMessage(JsonToProtoSample.Descriptor, doc.RootElement);
    }

    private static JsonToProtoSample Decode(string json) =>
        JsonToProtoSample.Parser.ParseFrom(Encode(json));

    [Fact]
    public void WriteMessage_RoundTripsAllScalarTypes()
    {
        var sample = Decode(
            """
            {
                "int32Field": 42,
                "int64Field": "9876543210",
                "uint32Field": 7,
                "uint64Field": "18000000000000000000",
                "sint32Field": -7,
                "sint64Field": "-9876543210",
                "fixed32Field": 1234,
                "fixed64Field": "9999999999",
                "sfixed32Field": -100,
                "sfixed64Field": "-1234567890",
                "floatField": 1.5,
                "doubleField": 2.5,
                "boolField": true,
                "stringField": "hello",
                "bytesField": "AQID"
            }
            """);

        sample.Int32Field.Should().Be(42);
        sample.Int64Field.Should().Be(9876543210L);
        sample.Uint32Field.Should().Be(7U);
        sample.Uint64Field.Should().Be(18000000000000000000UL);
        sample.Sint32Field.Should().Be(-7);
        sample.Sint64Field.Should().Be(-9876543210L);
        sample.Fixed32Field.Should().Be(1234U);
        sample.Fixed64Field.Should().Be(9999999999UL);
        sample.Sfixed32Field.Should().Be(-100);
        sample.Sfixed64Field.Should().Be(-1234567890L);
        sample.FloatField.Should().Be(1.5f);
        sample.DoubleField.Should().Be(2.5);
        sample.BoolField.Should().BeTrue();
        sample.StringField.Should().Be("hello");
        sample.BytesField.ToBase64().Should().Be("AQID");
    }

    [Fact]
    public void WriteMessage_AcceptsBoolFalseAndDefaults()
    {
        var sample = Decode("""{"boolField": false}""");
        sample.BoolField.Should().BeFalse();
    }

    [Fact]
    public void WriteMessage_AcceptsDoubleSpecialStrings()
    {
        Decode("""{"doubleField": "NaN"}""").DoubleField.Should().Be(double.NaN);
        Decode("""{"doubleField": "Infinity"}""").DoubleField.Should().Be(double.PositiveInfinity);
        Decode("""{"doubleField": "-Infinity"}""").DoubleField.Should().Be(double.NegativeInfinity);
        Decode("""{"doubleField": "1.5"}""").DoubleField.Should().Be(1.5);
    }

    [Fact]
    public void WriteMessage_AcceptsStringEncodedInt32()
    {
        Decode("""{"int32Field": "123"}""").Int32Field.Should().Be(123);
    }

    [Fact]
    public void WriteMessage_AcceptsStringEncodedUInt32()
    {
        Decode("""{"uint32Field": "456"}""").Uint32Field.Should().Be(456U);
    }

    [Fact]
    public void WriteMessage_RejectsOutOfRangeInt32()
    {
        var act = () => Encode("""{"int32Field": 3000000000}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32-bit signed integer*");
    }

    [Fact]
    public void WriteMessage_RejectsOutOfRangeUInt32()
    {
        var act = () => Encode("""{"uint32Field": 5000000000}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32-bit unsigned integer*");
    }

    [Fact]
    public void WriteMessage_RejectsNegativeForUInt32()
    {
        var act = () => Encode("""{"uint32Field": -1}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32-bit unsigned integer*");
    }

    [Fact]
    public void WriteMessage_RejectsOutOfRangeStringForInt32()
    {
        var act = () => Encode("""{"int32Field": "3000000000"}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32-bit signed integer*");
    }

    [Fact]
    public void WriteMessage_RejectsInvalidIntegerString()
    {
        var act = () => Encode("""{"int64Field": "not-a-number"}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*integer*");
    }

    [Fact]
    public void WriteMessage_RejectsInvalidUInt64String()
    {
        var act = () => Encode("""{"uint64Field": "neg-1"}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unsigned integer*");
    }

    [Fact]
    public void WriteMessage_RejectsDoubleWithBoolean()
    {
        var act = () => Encode("""{"doubleField": true}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*number*");
    }

    [Fact]
    public void WriteMessage_RejectsBoolWithString()
    {
        var act = () => Encode("""{"boolField": "yes"}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*boolean*");
    }

    [Fact]
    public void WriteMessage_RejectsBoolWithNumber()
    {
        var act = () => Encode("""{"boolField": 1}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*boolean*");
    }

    [Fact]
    public void WriteMessage_RejectsStringFieldWithNumber()
    {
        var act = () => Encode("""{"stringField": 1}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*string*");
    }

    [Fact]
    public void WriteMessage_RejectsBytesNotBase64()
    {
        var act = () => Encode("""{"bytesField": "!!!not-base64!!!"}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not valid base64*");
    }

    [Fact]
    public void WriteMessage_RejectsBytesNonString()
    {
        var act = () => Encode("""{"bytesField": 12}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*base64*");
    }

    [Fact]
    public void WriteMessage_AcceptsEnumByNumberAndName()
    {
        Decode("""{"color": 2}""").Color.Should().Be(JsonToProtoSample.Types.Color.Green);
        Decode("""{"color": "BLUE"}""").Color.Should().Be(JsonToProtoSample.Types.Color.Blue);
    }

    [Fact]
    public void WriteMessage_RejectsUnknownEnumName()
    {
        var act = () => Encode("""{"color": "PURPLE"}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no enum value named*");
    }

    [Fact]
    public void WriteMessage_RejectsEnumWithBoolean()
    {
        var act = () => Encode("""{"color": true}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*enum*");
    }

    [Fact]
    public void WriteMessage_RejectsEnumNumberOutOfInt32Range()
    {
        var act = () => Encode("""{"color": 5000000000}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*enum*");
    }

    [Fact]
    public void WriteMessage_RoundTripsRepeatedScalars()
    {
        var sample = Decode("""{"int32List": [1, 2, 3], "stringList": ["a", "b"]}""");
        sample.Int32List.Should().Equal(1, 2, 3);
        sample.StringList.Should().Equal("a", "b");
    }

    [Fact]
    public void WriteMessage_OmitsEmptyPackedRepeated()
    {
        var bytes = Encode("""{"int32List": []}""");
        bytes.Should().BeEmpty();
    }

    [Fact]
    public void WriteMessage_RoundTripsRepeatedMessages()
    {
        var sample = Decode(
            """{"nestedList": [{"note": "x", "weight": 1}, {"note": "y", "weight": 2}]}""");
        sample.NestedList.Should().HaveCount(2);
        sample.NestedList[0].Note.Should().Be("x");
        sample.NestedList[0].Weight.Should().Be(1);
        sample.NestedList[1].Note.Should().Be("y");
        sample.NestedList[1].Weight.Should().Be(2);
    }

    [Fact]
    public void WriteMessage_RejectsRepeatedNonArray()
    {
        var act = () => Encode("""{"int32List": 5}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JSON array*");
    }

    [Fact]
    public void WriteMessage_RoundTripsNestedMessage()
    {
        var sample = Decode("""{"nested": {"note": "deep", "weight": 99}}""");
        sample.Nested.Note.Should().Be("deep");
        sample.Nested.Weight.Should().Be(99);
    }

    [Fact]
    public void WriteMessage_RejectsMessageNonObject()
    {
        var act = () => Encode("""{"nested": 5}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JSON object*");
    }

    [Fact]
    public void WriteMessage_AcceptsStringMap()
    {
        var sample = Decode("""{"stringMap": {"hi": "hello", "yo": "yo"}}""");
        sample.StringMap.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["hi"] = "hello",
            ["yo"] = "yo",
        });
    }

    [Fact]
    public void WriteMessage_AcceptsMessageValuedMap()
    {
        var sample = Decode(
            """{"messageMap": {"a": {"note": "alpha", "weight": 1}, "b": {"note": "beta", "weight": 2}}}""");
        sample.MessageMap.Should().HaveCount(2);
        sample.MessageMap["a"].Note.Should().Be("alpha");
        sample.MessageMap["a"].Weight.Should().Be(1);
        sample.MessageMap["b"].Note.Should().Be("beta");
        sample.MessageMap["b"].Weight.Should().Be(2);
    }

    [Fact]
    public void WriteMessage_AcceptsIntKeyedMap()
    {
        var sample = Decode("""{"intKeyedMap": {"42": "answer", "7": "lucky"}}""");
        sample.IntKeyedMap.Should().HaveCount(2);
        sample.IntKeyedMap[42].Should().Be("answer");
        sample.IntKeyedMap[7].Should().Be("lucky");
    }

    [Fact]
    public void WriteMessage_SkipsNullMapEntries()
    {
        var sample = Decode("""{"stringMap": {"keep": "ok", "drop": null}}""");
        sample.StringMap.Should().ContainKey("keep");
        sample.StringMap.Should().NotContainKey("drop");
    }

    [Fact]
    public void WriteMessage_RejectsMapNonObject()
    {
        var act = () => Encode("""{"stringMap": [1, 2]}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*map field*JSON object*");
    }

    [Fact]
    public void WriteMessage_HonorsJsonNameAndFieldNameFallback()
    {
        Decode("""{"label": "via-json"}""").LabelValue.Should().Be("via-json");
        Decode("""{"label_value": "via-name"}""").LabelValue.Should().Be("via-name");
        Decode("""{"int32_field": 7}""").Int32Field.Should().Be(7);
    }

    [Fact]
    public void WriteMessage_SkipsNullsAndUnknownProperties()
    {
        var sample = Decode("""{"unknown": "ignored", "stringField": null}""");
        sample.StringField.Should().BeEmpty();
    }

    [Fact]
    public void WriteMessage_RejectsRootNonObject()
    {
        var act = () => Encode("[1, 2, 3]");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JSON object*");
    }
}
