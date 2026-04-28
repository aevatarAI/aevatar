using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;
using Aevatar.GAgents.Device;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class DeviceRegistrationProjectorTests
{
    private readonly RecordingWriteDispatcher _dispatcher = new();
    private readonly FixedProjectionClock _clock = new(new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly DeviceRegistrationProjector _projector;
    private readonly DeviceRegistrationMaterializationContext _context;

    public DeviceRegistrationProjectorTests()
    {
        _projector = new DeviceRegistrationProjector(_dispatcher, _clock);
        _context = new DeviceRegistrationMaterializationContext
        {
            RootActorId = "device-reg-actor-1",
            ProjectionKind = "device-registration-read-model",
        };
    }

    [Fact]
    public async Task ProjectAsync_WithValidCommittedEvent_UpsertsDocument()
    {
        var state = new DeviceRegistrationState
        {
            Registrations =
            {
                new DeviceRegistrationEntry
                {
                    Id = "reg-1",
                    ScopeId = "scope-a",
                    HmacKey = "key-abc",
                    NyxConversationId = "conv-42",
                    Description = "Living room sensor",
                },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-1", version: 3, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(1);
        var doc = _dispatcher.Upserts[0];
        doc.Id.Should().Be("reg-1");
        doc.ScopeId.Should().Be("scope-a");
        doc.HmacKey.Should().Be("key-abc");
        doc.NyxConversationId.Should().Be("conv-42");
        doc.Description.Should().Be("Living room sensor");
        doc.StateVersion.Should().Be(3);
        doc.LastEventId.Should().Be("evt-1");
        doc.ActorId.Should().Be("device-reg-actor-1");
    }

    [Fact]
    public async Task ProjectAsync_WithMultipleRegistrations_UpsertsAll()
    {
        var state = new DeviceRegistrationState
        {
            Registrations =
            {
                new DeviceRegistrationEntry { Id = "reg-1", ScopeId = "scope-a", HmacKey = "k1" },
                new DeviceRegistrationEntry { Id = "reg-2", ScopeId = "scope-b", HmacKey = "k2" },
                new DeviceRegistrationEntry { Id = "reg-3", ScopeId = "scope-c", HmacKey = "k3" },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-multi", version: 5, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(3);
        _dispatcher.Upserts.Select(d => d.Id).Should().BeEquivalentTo("reg-1", "reg-2", "reg-3");
    }

    [Fact]
    public async Task ProjectAsync_WithUnrelatedEvent_DoesNothing()
    {
        // Envelope with a raw Int32Value payload — not a CommittedStateEventPublished
        var envelope = new EventEnvelope
        {
            Id = "evt-unrelated",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new Int32Value { Value = 99 }),
        };

        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithEmptyRegistrations_DoesNothing()
    {
        var state = new DeviceRegistrationState();

        var envelope = BuildCommittedEnvelope("evt-empty", version: 1, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithTombstonedEntry_DeletesDocument()
    {
        // Channel RFC §7.1.1 — tombstoned entries drive IProjectionWriteDispatcher.DeleteAsync
        // so the read model document is removed under projector watermark coordination.
        var state = new DeviceRegistrationState
        {
            Registrations =
            {
                new DeviceRegistrationEntry
                {
                    Id = "reg-dead",
                    ScopeId = "scope-a",
                    HmacKey = "k",
                    Tombstoned = true,
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-tomb", 4, state), CancellationToken.None);

        _dispatcher.Upserts.Should().BeEmpty();
        _dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("reg-dead");
    }

    [Fact]
    public async Task ProjectAsync_WithMixedLiveAndTombstonedEntries_DispatchesBothVerdicts()
    {
        var state = new DeviceRegistrationState
        {
            Registrations =
            {
                new DeviceRegistrationEntry { Id = "reg-live", ScopeId = "scope-a", HmacKey = "k1" },
                new DeviceRegistrationEntry { Id = "reg-dead", ScopeId = "scope-b", HmacKey = "k2", Tombstoned = true },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-mixed", 5, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle().Which.Id.Should().Be("reg-live");
        _dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("reg-dead");
    }

    [Fact]
    public async Task ProjectAsync_SkipsEntryWithBlankId()
    {
        var state = new DeviceRegistrationState
        {
            Registrations =
            {
                new DeviceRegistrationEntry { Id = "", ScopeId = "scope-a", HmacKey = "k" },
                new DeviceRegistrationEntry { Id = "reg-valid", ScopeId = "scope-b", HmacKey = "k" },
            },
        };

        var envelope = BuildCommittedEnvelope("evt-blank", version: 2, state);
        await _projector.ProjectAsync(_context, envelope, CancellationToken.None);

        _dispatcher.Upserts.Should().HaveCount(1);
        _dispatcher.Upserts[0].Id.Should().Be("reg-valid");
    }

    // ─── Helpers ───

    private static EventEnvelope BuildCommittedEnvelope(
        string eventId,
        long version,
        DeviceRegistrationState state)
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

    private sealed class RecordingWriteDispatcher : IProjectionWriteDispatcher<DeviceRegistrationDocument>
    {
        public List<DeviceRegistrationDocument> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            DeviceRegistrationDocument readModel,
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
