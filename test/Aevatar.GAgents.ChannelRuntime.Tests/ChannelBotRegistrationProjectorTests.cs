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
    private readonly RecordingWriteDispatcher _dispatcher = new();
    private readonly FixedProjectionClock _clock = new(new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly ChannelBotRegistrationProjector _projector;
    private readonly ChannelBotRegistrationMaterializationContext _context;

    public ChannelBotRegistrationProjectorTests()
    {
        _projector = new ChannelBotRegistrationProjector(_dispatcher, _clock);
        _context = new ChannelBotRegistrationMaterializationContext
        {
            RootActorId = "bot-reg-actor-1",
            ProjectionKind = "channel-bot-registration-read-model",
        };
    }

    [Fact]
    public async Task ProjectAsync_WithValidCommittedEvent_UpsertsDocument()
    {
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry
                {
                    Id = "bot-reg-1",
                    Platform = "lark",
                    NyxProviderSlug = "lark-provider",
                    NyxUserToken = "token-abc",
                    VerificationToken = "verify-123",
                    ScopeId = "scope-x",
                    WebhookUrl = "https://example.com/callback/bot-reg-1",
                },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-bot-1", version: 2, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(1);
        var doc = _dispatcher.Upserts[0];
        doc.Id.Should().Be("bot-reg-1");
        doc.Platform.Should().Be("lark");
        doc.NyxProviderSlug.Should().Be("lark-provider");
        doc.NyxUserToken.Should().Be("token-abc");
        doc.VerificationToken.Should().Be("verify-123");
        doc.ScopeId.Should().Be("scope-x");
        doc.WebhookUrl.Should().Be("https://example.com/callback/bot-reg-1");
        doc.StateVersion.Should().Be(2);
        doc.LastEventId.Should().Be("evt-bot-1");
        doc.ActorId.Should().Be("bot-reg-actor-1");
    }

    [Fact]
    public async Task ProjectAsync_DoesNotProjectLegacyEncryptKey()
    {
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry
                {
                    Id = "bot-enc-1",
                    Platform = "lark",
                    NyxProviderSlug = "lark-provider",
                    NyxUserToken = "token-abc",
                    EncryptKey = "my-encrypt-key-456",
                },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-enc-1", version: 3, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(1);
        _dispatcher.Upserts[0].EncryptKey.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_PropagatesCredentialRef()
    {
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry
                {
                    Id = "bot-credref-1",
                    Platform = "lark",
                    CredentialRef = "secrets://lark/encrypt-key/bot-credref-1",
                },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-credref-1", version: 4, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(1);
        _dispatcher.Upserts[0].CredentialRef.Should().Be("secrets://lark/encrypt-key/bot-credref-1");
    }

    [Fact]
    public async Task ProjectAsync_DefaultsLegacyEncryptKeyToEmpty_WhenNotSet()
    {
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry
                {
                    Id = "bot-no-enc",
                    Platform = "lark",
                    // EncryptKey not set
                },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-no-enc", version: 1, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(1);
        _dispatcher.Upserts[0].EncryptKey.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithMultipleRegistrations_UpsertsAll()
    {
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry { Id = "bot-1", Platform = "lark" },
                new ChannelBotRegistrationEntry { Id = "bot-2", Platform = "telegram" },
                new ChannelBotRegistrationEntry { Id = "bot-3", Platform = "discord" },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-multi", version: 4, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(3);
        _dispatcher.Upserts.Select(d => d.Id).Should().BeEquivalentTo("bot-1", "bot-2", "bot-3");
    }

    [Fact]
    public async Task ProjectAsync_WithUnrelatedEvent_DoesNothing()
    {
        var envelope = new EventEnvelope
        {
            Id = "evt-unrelated",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new Int32Value { Value = 42 }),
        };

        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithEmptyRegistrations_DoesNothing()
    {
        var state = new ChannelBotRegistrationStoreState();

        var envelope = BuildCommittedEnvelope("evt-empty", version: 1, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithTombstonedEntry_DeletesDocument()
    {
        // Channel RFC §7.1.1 — tombstoned entries drive IProjectionWriteDispatcher.DeleteAsync
        // so the read model document is removed under projector watermark coordination.
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

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-tomb", 7, state), CancellationToken.None);

        _dispatcher.Upserts.Should().BeEmpty();
        _dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("bot-dead");
    }

    [Fact]
    public async Task ProjectAsync_WithMixedLiveAndTombstonedEntries_DispatchesBothVerdicts()
    {
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry { Id = "bot-live", Platform = "lark" },
                new ChannelBotRegistrationEntry { Id = "bot-dead", Platform = "lark", Tombstoned = true },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-mixed", 8, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle().Which.Id.Should().Be("bot-live");
        _dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("bot-dead");
    }

    [Fact]
    public async Task ProjectAsync_SkipsEntryWithBlankId()
    {
        var state = new ChannelBotRegistrationStoreState
        {
            Registrations =
            {
                new ChannelBotRegistrationEntry { Id = "", Platform = "lark" },
                new ChannelBotRegistrationEntry { Id = "bot-valid", Platform = "telegram" },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-blank", version: 2, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(1);
        _dispatcher.Upserts[0].Id.Should().Be("bot-valid");
    }

    // ─── Helpers ───

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

    private sealed class RecordingWriteDispatcher : IProjectionWriteDispatcher<ChannelBotRegistrationDocument>
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
