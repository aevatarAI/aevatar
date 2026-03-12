// ─── EventEnvelope Proto serialization roundtrip tests ───

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public class EventEnvelopeTests
{
    [Fact]
    public void Roundtrip_SerializeDeserialize()
    {
        var original = new EventEnvelope
        {
            Id = "evt-001",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = new EnvelopeRoute
            {
                PublisherActorId = "agent-a",
                Direction = EventDirection.Up,
                TargetActorId = "agent-b",
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-123",
            },
        };
        original.Propagation.Baggage["key1"] = "value1";

        var bytes = original.ToByteArray();
        var restored = EventEnvelope.Parser.ParseFrom(bytes);

        restored.Id.ShouldBe("evt-001");
        restored.Route.PublisherActorId.ShouldBe("agent-a");
        restored.Route.Direction.ShouldBe(EventDirection.Up);
        restored.Propagation.CorrelationId.ShouldBe("corr-123");
        restored.Route.TargetActorId.ShouldBe("agent-b");
        restored.Propagation.Baggage["key1"].ShouldBe("value1");
    }

    [Fact]
    public void StateEvent_Roundtrip()
    {
        var original = new StateEvent
        {
            EventId = "se-001",
            Version = 42,
            EventType = "MyEvent",
            AgentId = "agent-x",
        };

        var bytes = original.ToByteArray();
        var restored = StateEvent.Parser.ParseFrom(bytes);

        restored.EventId.ShouldBe("se-001");
        restored.Version.ShouldBe(42);
        restored.EventType.ShouldBe("MyEvent");
        restored.AgentId.ShouldBe("agent-x");
    }

    [Fact]
    public void EventDirection_HasExpectedValues()
    {
        // Ensure Proto-generated enum values are consistent with design
        ((int)EventDirection.Unspecified).ShouldBe(0);
        ((int)EventDirection.Down).ShouldBe(1);
        ((int)EventDirection.Up).ShouldBe(2);
        ((int)EventDirection.Both).ShouldBe(3);
        ((int)EventDirection.Self).ShouldBe(4);
        ((int)EventDirection.Observe).ShouldBe(5);
    }
}
