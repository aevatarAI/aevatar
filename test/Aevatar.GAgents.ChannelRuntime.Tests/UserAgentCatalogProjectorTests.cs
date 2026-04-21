using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogProjectorTests
{
    private readonly RecordingWriteDispatcher _dispatcher = new();
    private readonly FixedProjectionClock _clock = new(new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero));
    private readonly UserAgentCatalogProjector _projector;
    private readonly UserAgentCatalogMaterializationContext _context;

    public UserAgentCatalogProjectorTests()
    {
        _projector = new UserAgentCatalogProjector(_dispatcher, _clock);
        _context = new UserAgentCatalogMaterializationContext
        {
            RootActorId = "agent-registry-store",
            ProjectionKind = "user-agent-catalog-read-model",
        };
    }

    [Fact]
    public async Task ProjectAsync_WithValidCommittedEvent_UpsertsDocument()
    {
        var createdAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 9, 30, 0, TimeSpan.Zero));
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-1",
                    Platform = "lark",
                    ConversationId = "oc_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    NyxApiKey = "nyx-key-1",
                    OwnerNyxUserId = "user-1",
                    AgentType = "skill_runner",
                    TemplateName = "daily_report",
                    ScopeId = "scope-1",
                    ApiKeyId = "key-1",
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                    Status = "running",
                    LastRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 8, 0, 0, TimeSpan.Zero)),
                    NextRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero)),
                    ErrorCount = 1,
                    LastError = "last-error",
                    CreatedAt = createdAt,
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-1", 3, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle();
        var document = _dispatcher.Upserts[0];
        document.Id.Should().Be("agent-1");
        document.Platform.Should().Be("lark");
        document.ConversationId.Should().Be("oc_chat_1");
        document.NyxProviderSlug.Should().Be("api-lark-bot");
        document.NyxApiKey.Should().Be("nyx-key-1");
        document.OwnerNyxUserId.Should().Be("user-1");
        document.AgentType.Should().Be("skill_runner");
        document.TemplateName.Should().Be("daily_report");
        document.ScopeId.Should().Be("scope-1");
        document.ApiKeyId.Should().Be("key-1");
        document.ScheduleCron.Should().Be("0 9 * * *");
        document.ScheduleTimezone.Should().Be("UTC");
        document.Status.Should().Be("running");
        document.LastRunAtUtc.Should().Be(Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 8, 0, 0, TimeSpan.Zero)));
        document.NextRunAtUtc.Should().Be(Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero)));
        document.ErrorCount.Should().Be(1);
        document.LastError.Should().Be("last-error");
        document.StateVersion.Should().Be(3);
        document.LastEventId.Should().Be("evt-agent-1");
        document.ActorId.Should().Be("agent-registry-store");
        document.CreatedAt.Should().Be(createdAt.ToDateTimeOffset());
        document.UpdatedAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task ProjectAsync_WithTombstonedEntry_UpsertsTombstoneState()
    {
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-2",
                    Platform = "lark",
                    Tombstoned = true,
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-2", 4, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle();
        _dispatcher.Upserts[0].Id.Should().Be("agent-2");
        _dispatcher.Upserts[0].Tombstoned.Should().BeTrue();
        _dispatcher.Upserts[0].StateVersion.Should().Be(4);
    }

    [Fact]
    public async Task ProjectAsync_SkipsBlankAgentId()
    {
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry { AgentId = "", Platform = "lark" },
                new UserAgentCatalogEntry { AgentId = "agent-3", Platform = "lark" },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-3", 5, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle();
        _dispatcher.Upserts[0].Id.Should().Be("agent-3");
    }

    private static EventEnvelope BuildCommittedEnvelope(string eventId, long version, UserAgentCatalogState state)
    {
        var occurredAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero));
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("user-agent-catalog-projector-test"),
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

    private sealed class RecordingWriteDispatcher : IProjectionWriteDispatcher<UserAgentCatalogDocument>
    {
        public List<UserAgentCatalogDocument> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            UserAgentCatalogDocument readModel,
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
