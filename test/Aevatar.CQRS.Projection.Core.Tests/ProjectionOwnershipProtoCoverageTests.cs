using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionOwnershipProtoCoverageTests
{
    [Fact]
    public void ProjectionOwnershipMessages_ShouldRoundTripCloneMergeAndReflect()
    {
        var state = new ProjectionOwnershipCoordinatorState
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            Active = true,
            LastUpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            LeaseTtlMs = 30L * 60 * 1000,
        };

        var stateParsed = ProjectionOwnershipCoordinatorState.Parser.ParseFrom(state.ToByteArray());
        stateParsed.Should().BeEquivalentTo(state);

        var stateClone = state.Clone();
        stateClone.Should().BeEquivalentTo(state);
        stateClone.ToString().Should().Contain("scopeId");
        stateClone.CalculateSize().Should().BeGreaterThan(0);
        ((IMessage)stateClone).Descriptor.Name.Should().Be(nameof(ProjectionOwnershipCoordinatorState));

        var stateMerged = new ProjectionOwnershipCoordinatorState();
        stateMerged.MergeFrom(state);
        stateMerged.Should().BeEquivalentTo(state);
        state.Equals((object?)null).Should().BeFalse();

        var acquire = new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            LeaseTtlMs = 30L * 60 * 1000,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        var acquireParsed = ProjectionOwnershipAcquireEvent.Parser.ParseFrom(acquire.ToByteArray());
        acquireParsed.Should().BeEquivalentTo(acquire);
        acquireParsed.GetHashCode().Should().Be(acquire.GetHashCode());
        ((IMessage)acquireParsed).Descriptor.Name.Should().Be(nameof(ProjectionOwnershipAcquireEvent));

        var release = new ProjectionOwnershipReleaseEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        var releaseParsed = ProjectionOwnershipReleaseEvent.Parser.ParseFrom(release.ToByteArray());
        releaseParsed.Should().BeEquivalentTo(release);
        releaseParsed.GetHashCode().Should().Be(release.GetHashCode());
        ((IMessage)releaseParsed).Descriptor.Name.Should().Be(nameof(ProjectionOwnershipReleaseEvent));

        ProjectionOwnershipMessagesReflection.Descriptor.Should().NotBeNull();
        ProjectionOwnershipMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ProjectionOwnershipCoordinatorState));
        ProjectionOwnershipMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ProjectionOwnershipAcquireEvent));
        ProjectionOwnershipMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ProjectionOwnershipReleaseEvent));
    }

    [Fact]
    public void ProjectionOwnershipMessages_ShouldValidateNullAssignments()
    {
        var state = new ProjectionOwnershipCoordinatorState();
        var acquire = new ProjectionOwnershipAcquireEvent();
        var release = new ProjectionOwnershipReleaseEvent();

        Action setStateScope = () => state.ScopeId = null!;
        Action setAcquireSession = () => acquire.SessionId = null!;
        Action setReleaseScope = () => release.ScopeId = null!;

        setStateScope.Should().Throw<ArgumentNullException>();
        setAcquireSession.Should().Throw<ArgumentNullException>();
        setReleaseScope.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProjectionSessionEventTransportMessage_ShouldRoundTripAndValidate()
    {
        var msg = new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "step.completed",
            LegacyPayload = "{\"value\":\"payload\"}",
            Payload = Any.Pack(new StringValue { Value = "payload" }).ToByteString(),
        };

        var parsed = ProjectionSessionEventTransportMessage.Parser.ParseFrom(msg.ToByteArray());
        parsed.Should().BeEquivalentTo(msg);
        parsed.Clone().Should().BeEquivalentTo(msg);
        parsed.CalculateSize().Should().BeGreaterThan(0);
        parsed.ToString().Should().Contain("eventType");
        ((IMessage)parsed).Descriptor.Name.Should().Be(nameof(ProjectionSessionEventTransportMessage));
        parsed.LegacyPayload.Should().Be("{\"value\":\"payload\"}");
        var payload = Any.Parser.ParseFrom(parsed.Payload);
        payload.Is(StringValue.Descriptor).Should().BeTrue();
        payload.Unpack<StringValue>().Value.Should().Be("payload");

        var merged = new ProjectionSessionEventTransportMessage();
        merged.MergeFrom(msg);
        merged.Should().BeEquivalentTo(msg);
        msg.Equals((object?)null).Should().BeFalse();

        msg!.Payload = ByteString.Empty;
        msg.Payload.Should().NotBeNull();
        (msg.Payload ?? throw new InvalidOperationException("Payload should not be null.")).IsEmpty.Should().BeTrue();
        msg.LegacyPayload = string.Empty;
        msg.LegacyPayload.Should().BeEmpty();

        ProjectionSessionEventTransportReflection.Descriptor.Should().NotBeNull();
        ProjectionSessionEventTransportReflection.Descriptor.MessageTypes.Should().ContainSingle(x => x.Name == nameof(ProjectionSessionEventTransportMessage));
    }
}
