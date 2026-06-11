using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Tiny helper for materializing committed-state envelopes in projector tests.
/// Mirrors the in-test builder used by the workflow projection tests so the
/// envelope shape stays consistent with what the production projection
/// pipeline produces.
/// </summary>
internal static class TestEnvelopeBuilder
{
    public static EventEnvelope BuildCommittedEnvelope(IMessage state, long version, string? eventId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var timestamp = DateTimeOffset.Parse($"2026-04-29T10:{version:00}:00+00:00");
        return new EventEnvelope
        {
            Id = $"outer-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId ?? $"evt-{version}",
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(timestamp),
                    EventData = Any.Pack(state),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }
}
