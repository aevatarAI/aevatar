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
            Route = EnvelopeRouteSemantics.CreateDirect("agent-a", "agent-b"),
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
        restored.Route.RouteCase.ShouldBe(EnvelopeRoute.RouteOneofCase.Direct);
        restored.Propagation.CorrelationId.ShouldBe("corr-123");
        restored.Route.Direct.TargetActorId.ShouldBe("agent-b");
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
    public void TopologyAudience_HasExpectedValues()
    {
        // Ensure Proto-generated enum values are consistent with design
        ((int)TopologyAudience.Unspecified).ShouldBe(0);
        ((int)TopologyAudience.Self).ShouldBe(1);
        ((int)TopologyAudience.Parent).ShouldBe(2);
        ((int)TopologyAudience.Children).ShouldBe(3);
        ((int)TopologyAudience.ParentAndChildren).ShouldBe(4);
    }
}
