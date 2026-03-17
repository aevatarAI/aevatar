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
