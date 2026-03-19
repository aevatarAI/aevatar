using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.Scripting.Core.Tests.Serialization;

public class ScriptMessageSupportCoverageTests
{
    [Fact]
    public void ScriptMessageTypes_CreateMessage_ShouldReturnProtobufInstance_AndRejectNonMessageTypes()
    {
        var message = ScriptMessageTypes.CreateMessage(typeof(StringValue));

        message.Should().BeOfType<StringValue>();

        Action act = () => ScriptMessageTypes.CreateMessage(typeof(NotAProtobufMessage));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Type `*NotAProtobufMessage` must be a protobuf message with a public parameterless constructor.");
    }

    [Fact]
    public void ScriptMessageTypes_Unpack_ShouldHandleNull_Success_AndTypeMismatch()
    {
        ScriptMessageTypes.Unpack<StringValue>(null).Should().BeNull();

        var packed = Any.Pack(new StringValue { Value = "hello" });
        var unpacked = ScriptMessageTypes.Unpack<StringValue>(packed);

        unpacked.Should().NotBeNull();
        unpacked!.Value.Should().Be("hello");

        Action act = () => ScriptMessageTypes.Unpack<Int32Value>(packed);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Expected protobuf payload type `type.googleapis.com/google.protobuf.Int32Value`, but got `type.googleapis.com/google.protobuf.StringValue`.");
    }

    [Fact]
    public void ProtobufMessageCodec_ShouldPackAndUnpack_ByClrTypeAndDescriptor()
    {
        var codec = new ProtobufMessageCodec();
        var message = new StringValue { Value = "world" };

        codec.Pack(null).Should().BeNull();

        var payload = codec.Pack(message);
        payload.Should().NotBeNull();

        codec.Unpack(null, typeof(StringValue)).Should().BeNull();
        codec.Unpack(null, StringValue.Descriptor).Should().BeNull();

        codec.Unpack(payload, typeof(StringValue))
            .Should()
            .BeEquivalentTo(message);

        codec.Unpack(payload, StringValue.Descriptor)
            .Should()
            .BeEquivalentTo(message);

        Action unpackWrongType = () => codec.Unpack(payload, typeof(Int32Value));
        Action unpackWrongDescriptor = () => codec.Unpack(payload, Int32Value.Descriptor);

        unpackWrongType.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Expected protobuf payload type `type.googleapis.com/google.protobuf.Int32Value`, but got `type.googleapis.com/google.protobuf.StringValue`.");
        unpackWrongDescriptor.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Expected protobuf payload type `type.googleapis.com/google.protobuf.Int32Value`, but got `type.googleapis.com/google.protobuf.StringValue`.");
    }

    [Fact]
    public void ScriptMessageFieldAccessor_ShouldReadScalarValues_AsInvariantStrings()
    {
        ScriptMessageFieldAccessor.ReadScalarString(
                new StringValue { Value = "text" },
                "value")
            .Should()
            .Be("text");

        ScriptMessageFieldAccessor.ReadScalarString(
                new Int32Value { Value = 42 },
                "value")
            .Should()
            .Be("42");

        ScriptMessageFieldAccessor.ReadScalarString(
                new BytesValue { Value = ByteString.CopyFromUtf8("raw") },
                "value")
            .Should()
            .Be(ByteString.CopyFromUtf8("raw").ToString());
    }

    [Fact]
    public void ScriptMessageFieldAccessor_ShouldReturnNull_ForBlankMissingRepeatedMapAndMessageFields()
    {
        ScriptMessageFieldAccessor.ReadScalarString(
                new StringValue { Value = "text" },
                "")
            .Should()
            .BeNull();

        ScriptMessageFieldAccessor.ReadScalarString(
                new StringValue { Value = "text" },
                "missing")
            .Should()
            .BeNull();

        ScriptMessageFieldAccessor.ReadScalarString(
                new ListValue
                {
                    Values =
                    {
                        new ProtobufValue { StringValue = "a" },
                    },
                },
                "values")
            .Should()
            .BeNull();

        ScriptMessageFieldAccessor.ReadScalarString(
                new Struct
                {
                    Fields =
                    {
                        ["k"] = new ProtobufValue { StringValue = "v" },
                    },
                },
                "fields")
            .Should()
            .BeNull();

        ScriptMessageFieldAccessor.ReadScalarString(
                new ProtobufValue
                {
                    StructValue = new Struct(),
                },
                "struct_value")
            .Should()
            .BeNull();
    }

    private sealed class NotAProtobufMessage;
}
