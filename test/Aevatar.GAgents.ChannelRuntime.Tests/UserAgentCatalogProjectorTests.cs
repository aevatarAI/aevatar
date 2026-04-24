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
            RootActorId = UserAgentCatalogGAgent.WellKnownId,
            ProjectionKind = UserAgentCatalogProjectionPort.ProjectionKind,
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
    public async Task NyxCredentialProjector_WithValidCommittedEvent_UpsertsRuntimeCredentialDocument()
    {
        var dispatcher = new RecordingCredentialWriteDispatcher();
        var projector = new UserAgentCatalogNyxCredentialProjector(dispatcher, _clock);
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-1",
                    NyxApiKey = "nyx-key-1",
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-cred", 4, state), CancellationToken.None);

        dispatcher.Upserts.Should().ContainSingle();
        var document = dispatcher.Upserts[0];
        document.Id.Should().Be("agent-1");
        document.NyxApiKey.Should().Be("nyx-key-1");
        document.StateVersion.Should().Be(4);
        document.LastEventId.Should().Be("evt-agent-cred");
        document.ActorId.Should().Be("agent-registry-store");
        document.UpdatedAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task NyxCredentialProjector_DeletesDocument_WhenCredentialMissing()
    {
        var dispatcher = new RecordingCredentialWriteDispatcher();
        var projector = new UserAgentCatalogNyxCredentialProjector(dispatcher, _clock);
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-public",
                    Platform = "lark",
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-public", 5, state), CancellationToken.None);

        dispatcher.Upserts.Should().BeEmpty();
        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-public");
    }

    [Fact]
    public async Task ProjectAsync_WithTombstonedEntry_DeletesDocument()
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

        _dispatcher.Upserts.Should().BeEmpty();
        _dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-2");
    }

    [Fact]
    public async Task ProjectAsync_WithMixedLiveAndTombstonedEntries_DispatchesBothVerdicts()
    {
        // Verifies the watermark-coordination contract: live and tombstoned entries
        // in the same committed snapshot dispatch upserts + deletes in one pass so
        // the read model stays aligned with the authoritative state version.
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry { AgentId = "agent-live", Platform = "lark" },
                new UserAgentCatalogEntry { AgentId = "agent-dead", Platform = "lark", Tombstoned = true },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-mixed", 9, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle().Which.Id.Should().Be("agent-live");
        _dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-dead");
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

    private sealed class RecordingCredentialWriteDispatcher : IProjectionWriteDispatcher<UserAgentCatalogNyxCredentialDocument>
    {
        public List<UserAgentCatalogNyxCredentialDocument> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            UserAgentCatalogNyxCredentialDocument readModel,
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
