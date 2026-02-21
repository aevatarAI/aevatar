using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public class FoundationAbstractionsProtoCoverageTests
{
    [Fact]
    public void HierarchyEvents_ShouldCoverMergeCloneNullAndDescriptors()
    {
        var added = new ChildAddedEvent { ChildId = "child-1" };
        var removed = new ChildRemovedEvent { ChildId = "child-1" };
        var parent = new ParentChangedEvent
        {
            OldParent = "p-old",
            NewParent = "p-new",
        };

        var addedMerged = new ChildAddedEvent();
        addedMerged.MergeFrom(added);
        addedMerged.ShouldBe(added);
        addedMerged.Clone().ShouldBe(added);
        ((IMessage)addedMerged).Descriptor.Name.ShouldBe(nameof(ChildAddedEvent));
        addedMerged.CalculateSize().ShouldBeGreaterThan(0);
        addedMerged.Equals((object?)null).ShouldBeFalse();

        var removedMerged = new ChildRemovedEvent();
        removedMerged.MergeFrom(removed);
        removedMerged.ShouldBe(removed);
        removedMerged.Clone().ShouldBe(removed);
        ((IMessage)removedMerged).Descriptor.Name.ShouldBe(nameof(ChildRemovedEvent));
        removedMerged.CalculateSize().ShouldBeGreaterThan(0);
        removedMerged.Equals((object?)null).ShouldBeFalse();

        var parentMerged = new ParentChangedEvent();
        parentMerged.MergeFrom(parent);
        parentMerged.ShouldBe(parent);
        parentMerged.Clone().ShouldBe(parent);
        ((IMessage)parentMerged).Descriptor.Name.ShouldBe(nameof(ParentChangedEvent));
        parentMerged.CalculateSize().ShouldBeGreaterThan(0);
        parentMerged.Equals((object?)null).ShouldBeFalse();

        // Cover null-other merge branch in generated protobuf code.
        addedMerged.MergeFrom((ChildAddedEvent)null!);
        removedMerged.MergeFrom((ChildRemovedEvent)null!);
        parentMerged.MergeFrom((ParentChangedEvent)null!);

        Action setAddedNull = () => added.ChildId = null!;
        Action setRemovedNull = () => removed.ChildId = null!;
        Action setOldParentNull = () => parent.OldParent = null!;
        Action setNewParentNull = () => parent.NewParent = null!;

        setAddedNull.ShouldThrow<ArgumentNullException>();
        setRemovedNull.ShouldThrow<ArgumentNullException>();
        setOldParentNull.ShouldThrow<ArgumentNullException>();
        setNewParentNull.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void StateAndEnvelope_ShouldCoverRoundtripAndNullGuards()
    {
        var state = new StateEvent
        {
            EventId = "evt-1",
            Version = 7,
            EventType = "type.demo",
            AgentId = "actor-1",
            EventData = Any.Pack(new StringValue { Value = "payload" }),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        var stateParsed = StateEvent.Parser.ParseFrom(state.ToByteArray());
        stateParsed.ShouldBe(state);
        stateParsed.CalculateSize().ShouldBeGreaterThan(0);
        ((IMessage)stateParsed).Descriptor.Name.ShouldBe(nameof(StateEvent));

        var stateMerged = new StateEvent();
        stateMerged.MergeFrom(state);
        stateMerged.ShouldBe(state);
        stateMerged.MergeFrom((StateEvent)null!);
        stateMerged.Equals((object?)null).ShouldBeFalse();

        var envelope = new EventEnvelope
        {
            Id = "envelope-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            PublisherId = "actor-1",
            Direction = EventDirection.Both,
            Payload = Any.Pack(new StringValue { Value = "message" }),
            CorrelationId = "corr-1",
            TargetActorId = "actor-2",
        };
        envelope.Metadata["trace"] = "t-1";

        var envelopeParsed = EventEnvelope.Parser.ParseFrom(envelope.ToByteArray());
        envelopeParsed.ShouldBe(envelope);
        envelopeParsed.CalculateSize().ShouldBeGreaterThan(0);
        envelopeParsed.Metadata["trace"].ShouldBe("t-1");
        envelopeParsed.Payload.Unpack<StringValue>().Value.ShouldBe("message");
        ((IMessage)envelopeParsed).Descriptor.Name.ShouldBe(nameof(EventEnvelope));

        var envelopeMerged = new EventEnvelope();
        envelopeMerged.MergeFrom(envelope);
        envelopeMerged.ShouldBe(envelope);
        envelopeMerged.MergeFrom((EventEnvelope)null!);
        envelopeMerged.Equals((object?)null).ShouldBeFalse();

        Action setEnvelopeIdNull = () => envelope.Id = null!;
        Action setPublisherNull = () => envelope.PublisherId = null!;
        Action setStateAgentNull = () => state.AgentId = null!;
        Action setStateTypeNull = () => state.EventType = null!;
        Action setStateEventIdNull = () => state.EventId = null!;

        setEnvelopeIdNull.ShouldThrow<ArgumentNullException>();
        setPublisherNull.ShouldThrow<ArgumentNullException>();
        setStateAgentNull.ShouldThrow<ArgumentNullException>();
        setStateTypeNull.ShouldThrow<ArgumentNullException>();
        setStateEventIdNull.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void AgentMessagesReflection_ShouldExposeHierarchyMessages()
    {
        AgentMessagesReflection.Descriptor.ShouldNotBeNull();
        var names = AgentMessagesReflection.Descriptor.MessageTypes.Select(x => x.Name).ToList();

        names.ShouldContain(nameof(EventEnvelope));
        names.ShouldContain(nameof(StateEvent));
        names.ShouldContain(nameof(ChildAddedEvent));
        names.ShouldContain(nameof(ChildRemovedEvent));
        names.ShouldContain(nameof(ParentChangedEvent));
    }

    [Fact]
    public void ChildEvents_ShouldPreserveUnknownFieldsAndCoverEmptyBranches()
    {
        // tag=1 (child_id), then an unknown varint field (field=99, wire=0).
        var addedBytes = new byte[] { 10, 2, (byte)'a', (byte)'1', 0x98, 0x06, 0x01 };
        var removedBytes = new byte[] { 10, 2, (byte)'b', (byte)'2', 0x98, 0x06, 0x01 };

        var added = ChildAddedEvent.Parser.ParseFrom(addedBytes);
        var removed = ChildRemovedEvent.Parser.ParseFrom(removedBytes);

        added.ChildId.ShouldBe("a1");
        removed.ChildId.ShouldBe("b2");

        // Cover ReferenceEquals(this) path.
        added.Equals(added).ShouldBeTrue();
        removed.Equals(removed).ShouldBeTrue();

        // Cover empty string branches.
        var emptyAdded = new ChildAddedEvent();
        var emptyRemoved = new ChildRemovedEvent();
        emptyAdded.CalculateSize().ShouldBe(0);
        emptyRemoved.CalculateSize().ShouldBe(0);
        emptyAdded.GetHashCode().ShouldBeGreaterThan(0);
        emptyRemoved.GetHashCode().ShouldBeGreaterThan(0);

        // Cover MergeFrom(other.ChildId.Length == 0) branch.
        var addedWithValue = new ChildAddedEvent { ChildId = "x" };
        var removedWithValue = new ChildRemovedEvent { ChildId = "y" };
        addedWithValue.MergeFrom(new ChildAddedEvent());
        removedWithValue.MergeFrom(new ChildRemovedEvent());
        addedWithValue.ChildId.ShouldBe("x");
        removedWithValue.ChildId.ShouldBe("y");

        // Unknown fields should survive re-serialization.
        var addedRoundtrip = added.ToByteArray();
        var removedRoundtrip = removed.ToByteArray();
        addedRoundtrip.Length.ShouldBeGreaterThan(4);
        removedRoundtrip.Length.ShouldBeGreaterThan(4);
    }
}
