using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationProjectorTests
{
    private static Aevatar.Foundation.Abstractions.Credentials.SecretReference TestDeliverySecretReference(string registrationId) =>
        new()
        {
            Ref = $"sec_delivery_{registrationId}",
            Purpose = Aevatar.Foundation.Abstractions.Credentials.CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = "scope-x",
        };

    private readonly FixedProjectionClock _clock = new(new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly ChannelBotRegistrationMaterializationContext _context = new()
    {
        RootActorId = "bot-reg-actor-1",
        ProjectionKind = "channel-bot-registration-read-model",
    };

    [Fact]
    public async Task PublicProjector_UpsertsNonSecretRegistrationDocument()
    {
        var dispatcher = new RecordingRegistrationWriteDispatcher();
        var projector = new ChannelBotRegistrationProjector(dispatcher, _clock);
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry
                {
                    Id = "bot-reg-1",
                    Platform = "lark",
                    NyxProviderSlug = "api-lark-bot",
                    ScopeId = "scope-x",
                    WebhookUrl = "https://example.com/callback/bot-reg-1",
                    NyxChannelBotId = "nyx-bot-1",
                    NyxAgentApiKeyId = "api-key-1",
                    NyxConversationRouteId = "route-1",
                    WorkflowResultDeliveryCredential = TestDeliverySecretReference("bot-reg-1"),
                    LastInboundAtUtc = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 10, 11, 0, 0, TimeSpan.Zero)),
                    DefaultSkillName = "whatsapp-reply-draft",
                    WorkflowResultDeliveryRepair = FailedRepair(),
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-bot-1", 2, state), CancellationToken.None);

        dispatcher.Upserts.Should().ContainSingle();
        var doc = dispatcher.Upserts[0];
        doc.Id.Should().Be("bot-reg-1");
        doc.Platform.Should().Be("lark");
        doc.NyxProviderSlug.Should().Be("api-lark-bot");
        doc.ScopeId.Should().Be("scope-x");
        doc.WebhookUrl.Should().Be("https://example.com/callback/bot-reg-1");
        doc.NyxChannelBotId.Should().Be("nyx-bot-1");
        doc.NyxAgentApiKeyId.Should().Be("api-key-1");
        doc.NyxConversationRouteId.Should().Be("route-1");
        doc.WorkflowResultDeliveryCredential.Should().Be(TestDeliverySecretReference("bot-reg-1"));
        doc.StateVersion.Should().Be(2);
        doc.LastEventId.Should().Be("evt-bot-1");
        doc.ActorId.Should().Be("bot-reg-actor-1");
        doc.LastInboundAtUtc.Should().Be(Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 10, 11, 0, 0, TimeSpan.Zero)));
        doc.DefaultSkillName.Should().Be("whatsapp-reply-draft");
        doc.WorkflowResultDeliveryRepair.Should().Be(FailedRepair());
        doc.WorkflowResultDeliveryRepair.Should().NotBeSameAs(
            state.Registrations[0].WorkflowResultDeliveryRepair);
    }

    [Fact]
    public async Task PublicProjector_DeletesDocument_WhenEntryIsTombstoned()
    {
        var dispatcher = new RecordingRegistrationWriteDispatcher();
        var projector = new ChannelBotRegistrationProjector(dispatcher, _clock);
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry
                {
                    Id = "bot-dead",
                    Platform = "lark",
                    Tombstoned = true,
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-tomb", 7, state), CancellationToken.None);

        dispatcher.Upserts.Should().BeEmpty();
        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("bot-dead");
    }

    [Fact]
    public async Task Projector_IgnoresUnrelatedEvents()
    {
        var dispatcher = new RecordingRegistrationWriteDispatcher();
        var projector = new ChannelBotRegistrationProjector(dispatcher, _clock);
        var envelope = new EventEnvelope
        {
            Id = "evt-unrelated",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new Int32Value { Value = 42 }),
        };

        await projector.ProjectAsync(_context, envelope, CancellationToken.None);

        dispatcher.Upserts.Should().BeEmpty();
        dispatcher.Deletes.Should().BeEmpty();
    }

    private static EventEnvelope BuildCommittedEnvelope(
        string eventId,
        long version,
        ChannelBotRegistrationStoreState state)
    {
        var occurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = occurredAt.Clone(),
                    EventData = Any.Pack(new Empty()),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private static ChannelWorkflowResultDeliveryRepairState FailedRepair() =>
        new()
        {
            RequestId = "repair-1",
            Status = ChannelWorkflowResultDeliveryRepairStatus.Failed,
            ExpectedApiKeyId = "api-key-1",
            ExpectedConversationRouteId = "route-1",
            RotatedApiKeyId = "api-key-2",
            PreparedSecretReference = TestDeliverySecretReference("bot-reg-1"),
            FailurePhase = ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding,
            FailureReason = ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed,
            RequestedBySubjectId = "user-1",
            RequestedAtUnixMs = 1784563200000,
            UpdatedAtUnixMs = 1784563201000,
        };

    private sealed class RecordingRegistrationWriteDispatcher : IProjectionWriteDispatcher<ChannelBotRegistrationDocument>
    {
        public List<ChannelBotRegistrationDocument> Upserts { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ChannelBotRegistrationDocument readModel,
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
