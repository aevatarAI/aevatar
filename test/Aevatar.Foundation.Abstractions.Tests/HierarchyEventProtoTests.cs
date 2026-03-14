using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public class HierarchyEventProtoTests
{
    [Fact]
    public void ParentChangedEvent_ShouldCloneRoundtripAndCompare()
    {
        var evt = new ParentChangedEvent
        {
            OldParent = "p1",
            NewParent = "p2",
        };

        var clone = evt.Clone();
        clone.ShouldNotBeSameAs(evt);
        clone.ShouldBe(evt);

        var bytes = evt.ToByteArray();
        var parsed = ParentChangedEvent.Parser.ParseFrom(bytes);
        parsed.OldParent.ShouldBe("p1");
        parsed.NewParent.ShouldBe("p2");
        parsed.CalculateSize().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ChildEvents_ShouldSupportMergeCloneAndEquality()
    {
        var added = new ChildAddedEvent { ChildId = "c-1" };
        var merged = new ChildAddedEvent();
        merged.MergeFrom(added);
        merged.ShouldBe(added);
        merged.CalculateSize().ShouldBeGreaterThan(0);
        merged.ToString().ShouldContain("c-1");
        merged.GetHashCode().ShouldBe(added.GetHashCode());
        merged.Equals((object?)null).ShouldBeFalse();

        var removed = new ChildRemovedEvent { ChildId = "c-1" };
        removed.Clone().ShouldBe(removed);
        removed.Equals(new ChildRemovedEvent { ChildId = "other" }).ShouldBeFalse();
        removed.CalculateSize().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void EventEnvelope_ShouldPersistPayloadAndBaggage()
    {
        var payload = Any.Pack(new StringValue { Value = "raw-payload" });

        var envelope = new EventEnvelope
        {
            Id = "evt-2",
            Payload = payload,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateDirect("actor-a", "actor-b"),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-2",
            },
        };
        envelope.Propagation.Baggage["k"] = "v";

        var parsed = EventEnvelope.Parser.ParseFrom(envelope.ToByteArray());
        parsed.ShouldBe(envelope);
        parsed.Payload.Is(StringValue.Descriptor).ShouldBeTrue();
        parsed.Payload.Unpack<StringValue>().Value.ShouldBe("raw-payload");
        parsed.Propagation.Baggage["k"].ShouldBe("v");
        parsed.GetHashCode().ShouldBe(envelope.GetHashCode());
    }

    [Fact]
    public void StateEvent_ShouldCarryEventDataAndSupportRoundtrip()
    {
        var state = new StateEvent
        {
            EventId = "e1",
            AgentId = "a1",
            EventType = "demo",
            Version = 3,
            EventData = Any.Pack(new StringValue { Value = "state-payload" }),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        var bytes = state.ToByteArray();
        var parsed = StateEvent.Parser.ParseFrom(bytes);

        parsed.ShouldBe(state);
        parsed.EventData.Is(StringValue.Descriptor).ShouldBeTrue();
        parsed.EventData.Unpack<StringValue>().Value.ShouldBe("state-payload");
        parsed.CalculateSize().ShouldBeGreaterThan(0);
        parsed.ToString().ShouldContain("eventId");
        parsed.Equals((object?)null).ShouldBeFalse();
    }

    [Fact]
    public void FoundationMessagesReflection_ShouldExposeDescriptors()
    {
        AgentMessagesReflection.Descriptor.ShouldNotBeNull();
        AgentMessagesReflection.Descriptor.MessageTypes.Count.ShouldBeGreaterThan(0);
        AgentMessagesReflection.Descriptor.MessageTypes.Any(x => x.Name == nameof(EventEnvelope)).ShouldBeTrue();
        AgentMessagesReflection.Descriptor.MessageTypes.Any(x => x.Name == nameof(StateEvent)).ShouldBeTrue();
    }
}
