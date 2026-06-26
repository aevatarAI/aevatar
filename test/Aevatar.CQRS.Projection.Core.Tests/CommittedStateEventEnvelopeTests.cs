using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class CommittedStateEventEnvelopeTests
{
    [Fact]
    public void ProjectionDispatchRouteFilter_ShouldRejectDirectRoute()
    {
        var envelope = new EventEnvelope
        {
            Id = "evt-direct",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            Route = EnvelopeRouteSemantics.CreateDirect("actor-1", "actor-2"),
        };

        ProjectionDispatchRouteFilter.ShouldDispatch(envelope).Should().BeFalse();
    }

    [Fact]
    public void TryCreateObservedEnvelope_ShouldUnwrapCommittedStateEventPayload()
    {
        var occurredAt = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var envelope = new EventEnvelope
        {
            Id = "outer-envelope",
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-1",
                    Version = 7,
                    Timestamp = Timestamp.FromDateTimeOffset(occurredAt),
                    EventData = Any.Pack(new Int32Value { Value = 42 }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "STATE" }),
            }),
        };

        var ok = CommittedStateEventEnvelope.TryCreateObservedEnvelope(envelope, out var observed);

        ok.Should().BeTrue();
        observed.Should().NotBeNull();
        observed!.Id.Should().Be("evt-1");
        observed.Payload.Should().NotBeNull();
        observed.Payload!.Is(Int32Value.Descriptor).Should().BeTrue();
        observed.Payload.Unpack<Int32Value>().Value.Should().Be(42);
        CommittedStateEventEnvelope.ResolveTimestamp(envelope, DateTimeOffset.MinValue).Should().Be(occurredAt);
    }

    [Fact]
    public void TryUnpackState_ShouldReturnTypedState()
    {
        var envelope = new EventEnvelope
        {
            Id = "outer-envelope",
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-2",
                    Version = 8,
                    EventData = Any.Pack(new StringValue { Value = "fact" }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "STATE-ROOT" }),
            }),
        };

        var ok = CommittedStateEventEnvelope.TryUnpackState<StringValue>(
            envelope,
            out var published,
            out var stateEvent,
            out var state);

        ok.Should().BeTrue();
        published.Should().NotBeNull();
        stateEvent.Should().NotBeNull();
        state.Should().NotBeNull();
        state!.Value.Should().Be("STATE-ROOT");
        stateEvent!.Version.Should().Be(8);
    }

    [Fact]
    public void ResolveTimestamp_ShouldUseEnvelopeTimestamp_WhenStateEventTimestampIsMissing()
    {
        var envelopeOccurredAt = new DateTimeOffset(2026, 3, 15, 13, 30, 0, TimeSpan.Zero);
        var fallback = new DateTimeOffset(2026, 3, 15, 14, 0, 0, TimeSpan.Zero);
        var envelope = new EventEnvelope
        {
            Id = "outer-envelope",
            Timestamp = Timestamp.FromDateTimeOffset(envelopeOccurredAt),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-3",
                    Version = 9,
                    EventData = Any.Pack(new StringValue { Value = "fact" }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "STATE-ROOT" }),
            }),
        };

        CommittedStateEventEnvelope.ResolveTimestamp(envelope, fallback).Should().Be(envelopeOccurredAt);
    }

    [Fact]
    public void ResolveTimestamp_ShouldUseFallback_WhenStateEventAndEnvelopeTimestampAreMissing()
    {
        var fallback = new DateTimeOffset(2026, 3, 15, 14, 0, 0, TimeSpan.Zero);
        var envelope = new EventEnvelope
        {
            Id = "outer-envelope",
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-4",
                    Version = 10,
                    EventData = Any.Pack(new StringValue { Value = "fact" }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "STATE-ROOT" }),
            }),
        };

        CommittedStateEventEnvelope.ResolveTimestamp(envelope, fallback).Should().Be(fallback);
    }

    [Fact]
    public void GetOriginActorId_ShouldReturnCommittedEventAgentId()
    {
        var envelope = new EventEnvelope
        {
            Id = "outer-envelope",
            Route = EnvelopeRouteSemantics.CreateObserverPublication("relayed-to-parent"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-origin",
                    Version = 5,
                    EventData = Any.Pack(new StringValue { Value = "fact" }),
                    AgentId = "workflow-definition:studio:run:abc",
                },
                StateRoot = Any.Pack(new StringValue { Value = "STATE-ROOT" }),
            }),
        };

        CommittedStateEventEnvelope.GetOriginActorId(envelope)
            .Should().Be("workflow-definition:studio:run:abc");
    }

    [Fact]
    public void GetOriginActorId_ShouldReturnEmpty_WhenAgentIdMissingOrNotCommitted()
    {
        var committedWithoutAgentId = new EventEnvelope
        {
            Id = "outer-envelope",
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-no-origin",
                    Version = 6,
                    EventData = Any.Pack(new StringValue { Value = "fact" }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "STATE-ROOT" }),
            }),
        };
        var rawEnvelope = new EventEnvelope
        {
            Id = "raw-envelope",
            Payload = Any.Pack(new Int32Value { Value = 42 }),
        };

        CommittedStateEventEnvelope.GetOriginActorId(committedWithoutAgentId).Should().BeEmpty();
        CommittedStateEventEnvelope.GetOriginActorId(rawEnvelope).Should().BeEmpty();
    }

    [Fact]
    public void TryCreateObservedEnvelope_ShouldRejectRawEnvelopeFallback()
    {
        var envelope = new EventEnvelope
        {
            Id = "raw-envelope",
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-1"),
            Payload = Any.Pack(new Int32Value { Value = 42 }),
        };

        var ok = CommittedStateEventEnvelope.TryCreateObservedEnvelope(envelope, out var observed);

        ok.Should().BeFalse();
        observed.Should().BeNull();
        CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out _, out _, out _).Should().BeFalse();
        CommittedStateEventEnvelope.ResolveTimestamp(envelope, DateTimeOffset.MinValue).Should().Be(DateTimeOffset.MinValue);
    }
}
