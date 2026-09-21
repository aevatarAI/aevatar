using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ReplyMessageMultiplicityProtoTests
{
    [Fact]
    public void ChannelCapabilities_ShouldKeepGlobalReplyMultiplicityUnspecified()
    {
        var capabilities = ChannelCapabilities.Parser.ParseFrom(Array.Empty<byte>());

        capabilities.ReplyMessageMultiplicity.ShouldBe(ReplyMessageMultiplicity.Unspecified);
        capabilities.MaxMessageLength.ShouldBe(0);
        capabilities.SupportsEdit.ShouldBeFalse();
        capabilities.Streaming.ShouldBe(StreamingSupport.Unspecified);
    }

    [Theory]
    [InlineData(ReplyMessageMultiplicity.None)]
    [InlineData(ReplyMessageMultiplicity.Single)]
    [InlineData(ReplyMessageMultiplicity.Multiple)]
    public void ChannelCapabilities_ShouldRoundtripReplyMultiplicityAtField18(
        ReplyMessageMultiplicity multiplicity)
    {
        var capabilities = new ChannelCapabilities
        {
            ReplyMessageMultiplicity = multiplicity,
            MaxMessageLength = 4096,
            SupportsEdit = false,
        };

        var parsed = ChannelCapabilities.Parser.ParseFrom(capabilities.ToByteArray());

        parsed.ShouldBe(capabilities);
        var field = ChannelCapabilities.Descriptor.FindFieldByName("reply_message_multiplicity");
        field.ShouldNotBeNull();
        field.FieldNumber.ShouldBe(18);
        field.FieldType.ShouldBe(FieldType.Enum);
        field.EnumType.Name.ShouldBe(nameof(ReplyMessageMultiplicity));
        ChannelCapabilities.Descriptor.FindFieldByName("transport").FieldNumber.ShouldBe(17);
    }
}
