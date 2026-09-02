using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationDeliveryCurrentStateProjectorTests
{
    private readonly FixedProjectionClock _clock = new(new DateTimeOffset(2026, 6, 16, 8, 0, 0, TimeSpan.Zero));
    private readonly ConversationDeliveryMaterializationContext _context = new()
    {
        RootActorId = "conversation-actor-1",
        ProjectionKind = ConversationDeliveryCommittedStateProjectionActivationPlanProvider.ProjectionKind,
    };

    [Fact]
    public async Task ProjectAsync_WithCommittedConversationState_UpsertsDeliveryCurrentState()
    {
        var dispatcher = new RecordingDeliveryWriteDispatcher();
        var projector = new ConversationDeliveryCurrentStateProjector(dispatcher, _clock);
        var state = new ConversationGAgentState
        {
            Conversation = new ConversationReference
            {
                Channel = ChannelId.From("lark"),
                CanonicalKey = "lark:tenant:thread",
            },
            LastSuccessfulDelivery = DeliveryEntry("last-success", DeliveryStatus.Succeeded, 11),
            RecentDeliveries =
            {
                DeliveryEntry("failed", DeliveryStatus.FailedPostSend, 10),
                DeliveryEntry("last-success", DeliveryStatus.Succeeded, 11),
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-delivery", 12, state), CancellationToken.None);

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Id.Should().Be("conversation-actor-1");
        document.ActorId.Should().Be("conversation-actor-1");
        document.StateVersion.Should().Be(12);
        document.LastEventId.Should().Be("evt-delivery");
        document.Conversation.Channel.Value.Should().Be("lark");
        document.Conversation.CanonicalKey.Should().Be("lark:tenant:thread");
        document.RecentDeliveries.Select(entry => entry.RequestId)
            .Should().Equal("failed", "last-success");
        document.LastSuccessfulDelivery.Should().NotBeNull();
        document.LastSuccessfulDelivery!.RequestId.Should().Be("last-success");
        document.UpdatedAt.Should().Be(new DateTimeOffset(2026, 6, 16, 8, 1, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ProjectAsync_WithUnrelatedEnvelope_DoesNotWrite()
    {
        var dispatcher = new RecordingDeliveryWriteDispatcher();
        var projector = new ConversationDeliveryCurrentStateProjector(dispatcher, _clock);
        var envelope = new EventEnvelope
        {
            Id = "unrelated",
            Timestamp = Timestamp.FromDateTimeOffset(_clock.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new Empty()),
        };

        await projector.ProjectAsync(_context, envelope, CancellationToken.None);

        dispatcher.Upserts.Should().BeEmpty();
        dispatcher.Deletes.Should().BeEmpty();
    }

    private static DeliveryLedgerEntry DeliveryEntry(string requestId, DeliveryStatus status, long version) => new()
    {
        DeliveryKind = DeliveryKind.StreamingCard,
        Status = status,
        Target = new DeliveryTarget
        {
            Channel = ChannelId.From("lark"),
            ConversationKey = "lark:tenant:thread",
            ReplyMessageId = $"om_{requestId}",
        },
        ProviderMessageId = $"om_{requestId}",
        RequestId = requestId,
        SourceEventId = $"event-{requestId}",
        ProducedAtVersion = version,
    };

    private static EventEnvelope BuildCommittedEnvelope(
        string eventId,
        long version,
        ConversationGAgentState state)
    {
        var occurredAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 16, 8, 1, 0, TimeSpan.Zero));
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("conversation-delivery-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = occurredAt.Clone(),
                    EventData = Any.Pack(new DeliveryProducedEvent()),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingDeliveryWriteDispatcher
        : IProjectionWriteDispatcher<ConversationDeliveryCurrentStateDocument>
    {
        public List<ConversationDeliveryCurrentStateDocument> Upserts { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ConversationDeliveryCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
