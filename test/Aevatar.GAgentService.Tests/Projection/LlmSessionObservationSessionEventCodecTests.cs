using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class LlmSessionObservationSessionEventCodecTests
{
    private static readonly LlmSessionObservationSessionEventCodec Codec = new();

    [Fact]
    public void Channel_IsLlmSessionObservationChannel()
    {
        Codec.Channel.Should().Be("llm-session-observation");
    }

    [Fact]
    public void GetEventType_NullEnvelope_Throws()
    {
        var act = () => Codec.GetEventType(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetEventType_WithoutPayload_FallsBackToEnvelopeDescriptor()
    {
        var envelope = new EventEnvelope { Id = "no-payload" };

        Codec.GetEventType(envelope).Should().Be(EventEnvelope.Descriptor.FullName);
    }

    [Fact]
    public void GetEventType_WithPayload_ReturnsPayloadTypeUrl()
    {
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "x" }) };

        Codec.GetEventType(envelope).Should().Be(envelope.Payload.TypeUrl);
        Codec.GetEventType(envelope).Should().Contain("StringValue");
    }

    [Fact]
    public void Serialize_NullEnvelope_Throws()
    {
        var act = () => Codec.Serialize(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTripsEnvelope()
    {
        var envelope = new EventEnvelope
        {
            Id = "evt-1",
            Payload = Any.Pack(new StringValue { Value = "hello" }),
        };
        var eventType = Codec.GetEventType(envelope);

        var bytes = Codec.Serialize(envelope);
        var roundTripped = Codec.Deserialize(eventType, bytes);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be("evt-1");
        roundTripped.Should().Be(envelope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Deserialize_BlankEventType_ReturnsNull(string eventType)
    {
        var bytes = Codec.Serialize(new EventEnvelope { Id = "evt" });

        Codec.Deserialize(eventType, bytes).Should().BeNull();
    }

    [Fact]
    public void Deserialize_EmptyPayload_ReturnsNull()
    {
        Codec.Deserialize("any-type", ByteString.Empty).Should().BeNull();
    }

    [Fact]
    public void Deserialize_EventTypeMismatch_ReturnsNull()
    {
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "v" }) };
        var bytes = Codec.Serialize(envelope);

        Codec.Deserialize("type.googleapis.com/some.other.Type", bytes).Should().BeNull();
    }

    [Fact]
    public void Deserialize_MalformedBytes_ReturnsNull()
    {
        // Tag for field 1 / length-delimited declaring 5 bytes but supplying none -> truncated wire data.
        var malformed = ByteString.CopyFrom(new byte[] { 0x0A, 0x05 });

        Codec.Deserialize("any-type", malformed).Should().BeNull();
    }
}
