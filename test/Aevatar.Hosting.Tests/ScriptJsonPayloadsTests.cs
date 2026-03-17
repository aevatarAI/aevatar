using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Reflection;

namespace Aevatar.Hosting.Tests;

public sealed class ScriptJsonPayloadsTests
{
    private static readonly System.Type PayloadsType = typeof(Aevatar.Scripting.Hosting.DependencyInjection.ServiceCollectionExtensions)
        .Assembly
        .GetType("Aevatar.Scripting.Hosting.CapabilityApi.ScriptJsonPayloads", throwOnError: true)!;

    [Fact]
    public void PackStruct_ShouldReturnEmptyStruct_WhenJsonIsBlank()
    {
        InvokePackStruct(null).Unpack<Struct>().Fields.Should().BeEmpty();
        InvokePackStruct(" ").Unpack<Struct>().Fields.Should().BeEmpty();
    }

    [Fact]
    public void PackStruct_ShouldParseStruct_WhenJsonIsProvided()
    {
        var packed = InvokePackStruct("""{"name":"alice","count":2}""");
        var parsed = packed.Unpack<Struct>();

        parsed.Fields["name"].StringValue.Should().Be("alice");
        parsed.Fields["count"].NumberValue.Should().Be(2);
    }

    [Fact]
    public void ToJson_ShouldCoverKnownPayloadKinds_AndFallbackToAnyFormatter()
    {
        InvokeToJson(null).Should().Be("{}");
        InvokeToJson(Any.Pack(new Struct
        {
            Fields = { ["name"] = Google.Protobuf.WellKnownTypes.Value.ForString("alice") },
        })).Should().Contain("\"name\": \"alice\"");
        InvokeToJson(Any.Pack(Google.Protobuf.WellKnownTypes.Value.ForString("value"))).Should().Be("\"value\"");
        InvokeToJson(Any.Pack(new ListValue
        {
            Values = { Google.Protobuf.WellKnownTypes.Value.ForString("one"), Google.Protobuf.WellKnownTypes.Value.ForNumber(2) },
        })).Should().Contain("[");
        InvokeToJson(Any.Pack(new StringValue { Value = "text" })).Should().Contain("text");
        InvokeToJson(Any.Pack(new BoolValue { Value = true })).Should().Be("true");
        InvokeToJson(Any.Pack(new Int32Value { Value = 32 })).Should().Be("32");
        InvokeToJson(Any.Pack(new Int64Value { Value = 64 })).Should().Be("\"64\"");
        InvokeToJson(Any.Pack(new UInt32Value { Value = 32 })).Should().Be("32");
        InvokeToJson(Any.Pack(new UInt64Value { Value = 64 })).Should().Be("\"64\"");
        InvokeToJson(Any.Pack(new FloatValue { Value = 1.5f })).Should().Contain("1.5");
        InvokeToJson(Any.Pack(new DoubleValue { Value = 2.5d })).Should().Contain("2.5");
        InvokeToJson(Any.Pack(new BytesValue { Value = ByteString.CopyFromUtf8("abc") })).Should().Contain("YWJj");
        InvokeToJson(Any.Pack(new Empty())).Should().Contain("{").And.Contain("}");

        var unknownPayload = Any.Pack(Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc)));
        Action act = () => InvokeToJson(unknownPayload);
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Type registry has no descriptor*");
    }

    private static Any InvokePackStruct(string? json)
    {
        var method = PayloadsType.GetMethod("PackStruct", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (Any)method!.Invoke(null, [json])!;
    }

    private static string InvokeToJson(Any? payload)
    {
        var method = PayloadsType.GetMethod("ToJson", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (string)method!.Invoke(null, [payload])!;
    }
}
