using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationProjectorTests
{
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
                    NyxProviderSlug = "lark-provider",
                    ScopeId = "scope-x",
                    WebhookUrl = "https://example.com/callback/bot-reg-1",
                    NyxChannelBotId = "nyx-bot-1",
                    NyxAgentApiKeyId = "api-key-1",
                    NyxConversationRouteId = "route-1",
                    LegacyDirectBinding = new ChannelBotLegacyDirectBinding
                    {
                        NyxUserToken = "token-abc",
                        NyxRefreshToken = "refresh-abc",
                        VerificationToken = "verify-123",
                        CredentialRef = "secrets://lark/bot-reg-1",
                        EncryptKey = "encrypt-123",
                    },
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-bot-1", 2, state), CancellationToken.None);

        dispatcher.Upserts.Should().HaveCount(1);
        var doc = dispatcher.Upserts[0];
        doc.Id.Should().Be("bot-reg-1");
        doc.Platform.Should().Be("lark");
        doc.NyxProviderSlug.Should().Be("lark-provider");
        doc.ScopeId.Should().Be("scope-x");
        doc.WebhookUrl.Should().Be("https://example.com/callback/bot-reg-1");
        doc.NyxChannelBotId.Should().Be("nyx-bot-1");
        doc.NyxAgentApiKeyId.Should().Be("api-key-1");
        doc.NyxConversationRouteId.Should().Be("route-1");
        doc.StateVersion.Should().Be(2);
        doc.LastEventId.Should().Be("evt-bot-1");
        doc.ActorId.Should().Be("bot-reg-actor-1");
    }

    [Fact]
    public async Task LegacyBindingProjector_UpsertsRuntimeOnlySecretDocument()
    {
        var dispatcher = new RecordingLegacyBindingWriteDispatcher();
        var projector = new ChannelBotLegacyDirectBindingProjector(dispatcher, _clock);
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry
                {
                    Id = "bot-secret-1",
                    Platform = "lark",
                    LegacyDirectBinding = new ChannelBotLegacyDirectBinding
                    {
                        NyxUserToken = "token-abc",
                        NyxRefreshToken = "refresh-abc",
                        VerificationToken = "verify-123",
                        CredentialRef = "secrets://lark/bot-secret-1",
                        EncryptKey = "encrypt-456",
                    },
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-secret-1", 3, state), CancellationToken.None);

        dispatcher.Upserts.Should().HaveCount(1);
        var doc = dispatcher.Upserts[0];
        doc.Id.Should().Be("bot-secret-1");
        doc.NyxUserToken.Should().Be("token-abc");
        doc.NyxRefreshToken.Should().Be("refresh-abc");
        doc.VerificationToken.Should().Be("verify-123");
        doc.CredentialRef.Should().Be("secrets://lark/bot-secret-1");
        doc.EncryptKey.Should().Be("encrypt-456");
        doc.StateVersion.Should().Be(3);
        doc.LastEventId.Should().Be("evt-secret-1");
        doc.ActorId.Should().Be("bot-reg-actor-1");
    }

    [Fact]
    public async Task LegacyBindingProjector_DeletesDocument_WhenBindingIsMissing()
    {
        var dispatcher = new RecordingLegacyBindingWriteDispatcher();
        var projector = new ChannelBotLegacyDirectBindingProjector(dispatcher, _clock);
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry
                {
                    Id = "bot-public-only",
                    Platform = "lark",
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-public-only", 4, state), CancellationToken.None);

        dispatcher.Upserts.Should().BeEmpty();
        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("bot-public-only");
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
    public async Task PublicProjector_UpsertsAllLiveRegistrations()
    {
        var dispatcher = new RecordingRegistrationWriteDispatcher();
        var projector = new ChannelBotRegistrationProjector(dispatcher, _clock);
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry { Id = "bot-1", Platform = "lark" },
                new ChannelBotRegistrationEntry { Id = "bot-2", Platform = "telegram" },
                new ChannelBotRegistrationEntry { Id = "bot-3", Platform = "discord" },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-multi", 4, state), CancellationToken.None);

        dispatcher.Upserts.Should().HaveCount(3);
        dispatcher.Upserts.Select(static d => d.Id).Should().BeEquivalentTo("bot-1", "bot-2", "bot-3");
    }

    [Fact]
    public async Task Projectors_IgnoreUnrelatedEvents()
    {
        var registrationDispatcher = new RecordingRegistrationWriteDispatcher();
        var legacyDispatcher = new RecordingLegacyBindingWriteDispatcher();
        var registrationProjector = new ChannelBotRegistrationProjector(registrationDispatcher, _clock);
        var legacyProjector = new ChannelBotLegacyDirectBindingProjector(legacyDispatcher, _clock);
        var envelope = new EventEnvelope
        {
            Id = "evt-unrelated",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new Int32Value { Value = 42 }),
        };

        await registrationProjector.ProjectAsync(_context, envelope, CancellationToken.None);
        await legacyProjector.ProjectAsync(_context, envelope, CancellationToken.None);

        registrationDispatcher.Upserts.Should().BeEmpty();
        registrationDispatcher.Deletes.Should().BeEmpty();
        legacyDispatcher.Upserts.Should().BeEmpty();
        legacyDispatcher.Deletes.Should().BeEmpty();
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

    private sealed class RecordingLegacyBindingWriteDispatcher : IProjectionWriteDispatcher<ChannelBotLegacyDirectBindingDocument>
    {
        public List<ChannelBotLegacyDirectBindingDocument> Upserts { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ChannelBotLegacyDirectBindingDocument readModel,
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
