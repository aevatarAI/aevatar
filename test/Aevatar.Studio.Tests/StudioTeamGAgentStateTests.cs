using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Tests the StudioTeam state machine in isolation by feeding events directly
/// into the GAgent's <c>TransitionState</c>. Reflection bridges to the
/// protected method so we can lock in the ADR-0017 invariants (lifecycle
/// monotonicity, idempotent roster set ops, derived member_count) without
/// standing up the full actor runtime.
/// </summary>
public sealed class StudioTeamGAgentStateTests
{
    private readonly StudioTeamStateApplier _agent = new();

    [Fact]
    public void Created_ShouldInitializeActiveLifecycle()
    {
        var initial = new StudioTeamState();
        var createdAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var afterCreate = _agent.Apply(initial, new StudioTeamCreatedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            DisplayName = "Platform",
            Description = "Platform team",
            CreatedAtUtc = createdAt,
        });

        afterCreate.TeamId.Should().Be("team-1");
        afterCreate.ScopeId.Should().Be("scope-1");
        afterCreate.DisplayName.Should().Be("Platform");
        afterCreate.LifecycleStage.Should().Be(StudioTeamLifecycleStage.Active);
        afterCreate.CreatedAtUtc.Should().Be(createdAt);
        afterCreate.UpdatedAtUtc.Should().Be(createdAt);
        afterCreate.MemberIds.Should().BeEmpty();
    }

    [Fact]
    public void Updated_WithDisplayNameOnly_ShouldNotTouchDescription()
    {
        var created = _agent.Apply(new StudioTeamState(), new StudioTeamCreatedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            DisplayName = "Platform",
            Description = "Original description",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var updated = _agent.Apply(created, new StudioTeamUpdatedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            DisplayName = "Platform Renamed",
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        updated.DisplayName.Should().Be("Platform Renamed");
        updated.Description.Should().Be("Original description");
    }

    [Fact]
    public void Updated_WithDescriptionOnly_ShouldNotTouchDisplayName()
    {
        var created = _agent.Apply(new StudioTeamState(), new StudioTeamCreatedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            DisplayName = "Platform",
            Description = "Original",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var updated = _agent.Apply(created, new StudioTeamUpdatedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            Description = "New description",
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        updated.DisplayName.Should().Be("Platform");
        updated.Description.Should().Be("New description");
    }

    [Fact]
    public void EntryMemberChanged_WithMemberId_ShouldPersistEntryMemberId()
    {
        var created = CreateActiveTeam();
        var withMember = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var changedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));
        var withEntry = _agent.Apply(withMember, new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = changedAt,
        });

        withEntry.EntryMemberId.Should().Be("m-1");
        withEntry.UpdatedAtUtc.Should().Be(changedAt);
    }

    [Fact]
    public void EntryMemberChanged_WithoutMemberId_ShouldClearEntryMemberId()
    {
        var created = CreateActiveTeam();
        var withMember = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var withEntry = _agent.Apply(withMember, new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        var changedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));
        var cleared = _agent.Apply(withEntry, new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            ChangedAtUtc = changedAt,
        });

        cleared.HasEntryMemberId.Should().BeFalse();
        cleared.EntryMemberId.Should().BeEmpty();
        cleared.UpdatedAtUtc.Should().Be(changedAt);
    }

    [Fact]
    public void RosterChanged_RemovingEntryMember_ShouldClearEntryMemberId()
    {
        var created = CreateActiveTeam();
        var withMember = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var withEntry = _agent.Apply(withMember, new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        var afterRemove = _agent.Apply(withEntry, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Removed,
            MemberCount = 0,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        afterRemove.MemberIds.Should().BeEmpty();
        afterRemove.HasEntryMemberId.Should().BeFalse();
    }

    [Fact]
    public async Task HandleMemberReassigned_ShouldPersistEntryClear_WhenEntryMemberLeavesTeam()
    {
        var created = CreateActiveTeam();
        var withMember = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var withEntry = _agent.Apply(withMember, new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
        var eventSourcing = new RecordingEventSourcing(withEntry);
        var agent = NewHandlerAgent(withEntry, eventSourcing);

        await agent.HandleMemberReassigned(new StudioMemberReassignedEvent
        {
            ScopeId = "scope-1",
            MemberId = "m-1",
            FromTeamId = "team-1",
            ToTeamId = "team-2",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        eventSourcing.RaisedEvents.Should().HaveCount(2);
        eventSourcing.RaisedEvents[0].Should().BeOfType<StudioTeamMemberRosterChangedEvent>()
            .Which.Effect.Should().Be(StudioTeamRosterEffect.Removed);
        eventSourcing.RaisedEvents[1].Should().BeOfType<StudioTeamEntryMemberChangedEvent>()
            .Which.HasEntryMemberId.Should().BeFalse();
    }

    [Fact]
    public async Task HandleMemberReassigned_ShouldOnlyPersistNoop_WhenDuplicateEntryMemberRemovalArrives()
    {
        var created = CreateActiveTeam();
        var eventSourcing = new RecordingEventSourcing(created);
        var agent = NewHandlerAgent(created, eventSourcing);

        await agent.HandleMemberReassigned(new StudioMemberReassignedEvent
        {
            ScopeId = "scope-1",
            MemberId = "m-1",
            FromTeamId = "team-1",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        eventSourcing.RaisedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<StudioTeamMemberRosterChangedEvent>()
            .Which.Effect.Should().Be(StudioTeamRosterEffect.Noop);
    }

    [Fact]
    public async Task HandleMemberReassigned_ShouldClearEntryMember_WhenDeleteDerivedRemovalArrives()
    {
        var created = CreateActiveTeam();
        var withMember = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var withEntry = _agent.Apply(withMember, new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
        var eventSourcing = new RecordingEventSourcing(withEntry);
        var agent = NewHandlerAgent(withEntry, eventSourcing);

        await agent.HandleMemberReassigned(new StudioMemberReassignedEvent
        {
            ScopeId = "scope-1",
            MemberId = "m-1",
            FromTeamId = "team-1",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        eventSourcing.RaisedEvents.Should().HaveCount(2);
        eventSourcing.RaisedEvents[0].Should().BeOfType<StudioTeamMemberRosterChangedEvent>()
            .Which.Effect.Should().Be(StudioTeamRosterEffect.Removed);
        eventSourcing.RaisedEvents[1].Should().BeOfType<StudioTeamEntryMemberChangedEvent>()
            .Which.HasEntryMemberId.Should().BeFalse();
    }

    [Fact]
    public async Task HandleEntryMemberChanged_ShouldReject_WhenTeamNotCreated()
    {
        var state = new StudioTeamState();
        var agent = NewHandlerAgent(state, new RecordingEventSourcing(state));

        var act = () => agent.HandleEntryMemberChanged(new StudioTeamEntryMemberChangedEvent
        {
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*team not yet created*");
    }

    [Fact]
    public async Task HandleEntryMemberChanged_ShouldReject_WhenTeamArchived()
    {
        var archived = _agent.Apply(CreateActiveTeam(), new StudioTeamArchivedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            ArchivedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var agent = NewHandlerAgent(archived, new RecordingEventSourcing(archived));

        var act = () => agent.HandleEntryMemberChanged(new StudioTeamEntryMemberChangedEvent
        {
            ScopeId = "scope-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*archived*");
    }

    [Fact]
    public async Task HandleEntryMemberChanged_ShouldReject_WhenScopeDoesNotMatch()
    {
        var created = CreateActiveTeam();
        var agent = NewHandlerAgent(created, new RecordingEventSourcing(created));

        var act = () => agent.HandleEntryMemberChanged(new StudioTeamEntryMemberChangedEvent
        {
            ScopeId = "other-scope",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot update entry member in scope other-scope*");
    }

    [Fact]
    public async Task HandleEntryMemberChanged_ShouldReject_WhenEntryMemberIsNotInRoster()
    {
        var created = CreateActiveTeam();
        var agent = NewHandlerAgent(created, new RecordingEventSourcing(created));

        var act = () => agent.HandleEntryMemberChanged(new StudioTeamEntryMemberChangedEvent
        {
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must belong to team*");
    }

    [Fact]
    public async Task HandleEntryMemberChanged_ShouldPersist_WhenEntryMemberChanges()
    {
        var withMember = _agent.Apply(CreateActiveTeam(), new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var eventSourcing = new RecordingEventSourcing(withMember);
        var agent = NewHandlerAgent(withMember, eventSourcing);
        var changedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));

        await agent.HandleEntryMemberChanged(new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = changedAt,
        });

        eventSourcing.RaisedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<StudioTeamEntryMemberChangedEvent>()
            .Which.Should().Match<StudioTeamEntryMemberChangedEvent>(evt =>
                evt.EntryMemberId == "m-1"
                && evt.HasEntryMemberId
                && evt.ChangedAtUtc.Equals(changedAt));
    }

    [Fact]
    public async Task HandleEntryMemberChanged_ShouldPersist_WhenEntryMemberIsCleared()
    {
        var withMember = _agent.Apply(CreateActiveTeam(), new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var withEntry = _agent.Apply(withMember, new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
        var eventSourcing = new RecordingEventSourcing(withEntry);
        var agent = NewHandlerAgent(withEntry, eventSourcing);
        var changedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        await agent.HandleEntryMemberChanged(new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            ChangedAtUtc = changedAt,
        });

        eventSourcing.RaisedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<StudioTeamEntryMemberChangedEvent>()
            .Which.Should().Match<StudioTeamEntryMemberChangedEvent>(evt =>
                !evt.HasEntryMemberId
                && evt.ChangedAtUtc.Equals(changedAt));
    }

    [Fact]
    public async Task HandleEntryMemberChanged_ShouldSkipPersist_WhenEntryMemberUnchanged()
    {
        var withMember = _agent.Apply(CreateActiveTeam(), new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var withEntry = _agent.Apply(withMember, new StudioTeamEntryMemberChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
        var eventSourcing = new RecordingEventSourcing(withEntry);
        var agent = NewHandlerAgent(withEntry, eventSourcing);

        await agent.HandleEntryMemberChanged(new StudioTeamEntryMemberChangedEvent
        {
            ScopeId = "scope-1",
            EntryMemberId = "m-1",
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        eventSourcing.RaisedEvents.Should().BeEmpty();
    }

    [Fact]
    public void Archived_ShouldMarkLifecycleArchived()
    {
        var created = _agent.Apply(new StudioTeamState(), new StudioTeamCreatedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            DisplayName = "Platform",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var archived = _agent.Apply(created, new StudioTeamArchivedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            ArchivedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        archived.LifecycleStage.Should().Be(StudioTeamLifecycleStage.Archived);
    }

    [Fact]
    public void RosterChanged_AddedEffect_ShouldAppendMemberId()
    {
        var created = CreateActiveTeam();

        var afterAdd = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        afterAdd.MemberIds.Should().ContainSingle().And.Contain("m-1");
    }

    [Fact]
    public void RosterChanged_AddedEffect_IsIdempotent()
    {
        var created = CreateActiveTeam();
        var addedOnce = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        // Re-applying the same Added event must not double-add (state-level
        // idempotency, mirrors the actor's "add if not present" rule).
        var addedTwice = _agent.Apply(addedOnce, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        addedTwice.MemberIds.Should().ContainSingle().And.Contain("m-1");
    }

    [Fact]
    public void RosterChanged_RemovedEffect_ShouldDropMemberId()
    {
        var created = CreateActiveTeam();
        var withMember = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var afterRemove = _agent.Apply(withMember, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Removed,
            MemberCount = 0,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        afterRemove.MemberIds.Should().BeEmpty();
    }

    [Fact]
    public void RosterChanged_NoopEffect_ShouldLeaveRosterUnchanged()
    {
        var created = CreateActiveTeam();
        var withMember = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        // A NOOP event still bumps updated_at_utc but does not mutate roster.
        var laterStamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));
        var afterNoop = _agent.Apply(withMember, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-2",
            Effect = StudioTeamRosterEffect.Noop,
            MemberCount = 1,
            ChangedAtUtc = laterStamp,
        });

        afterNoop.MemberIds.Should().BeEquivalentTo(new[] { "m-1" });
        afterNoop.UpdatedAtUtc.Should().Be(laterStamp);
    }

    [Fact]
    public void RosterChanged_RetainsInsertionOrder_ForDirectoryDisplay()
    {
        // Roster ordering is not a hard contract today, but adopting insertion
        // order (rather than e.g. sorted) keeps "newest member last" so
        // directory listings can render a stable timeline if the wire ever
        // mirrors the roster. Lock the current behaviour so a regression
        // (e.g. introducing a sorted internal collection) shows up here.
        var created = CreateActiveTeam();
        var s1 = _agent.Apply(created, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-2",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 1,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var s2 = _agent.Apply(s1, new StudioTeamMemberRosterChangedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Effect = StudioTeamRosterEffect.Added,
            MemberCount = 2,
            ChangedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        s2.MemberIds.Should().Equal("m-2", "m-1");
    }

    private StudioTeamState CreateActiveTeam()
    {
        return _agent.Apply(new StudioTeamState(), new StudioTeamCreatedEvent
        {
            TeamId = "team-1",
            ScopeId = "scope-1",
            DisplayName = "Platform",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    private sealed class StudioTeamStateApplier
    {
        private static readonly MethodInfo TransitionStateMethod =
            typeof(StudioTeamGAgent).GetMethod(
                "TransitionState",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransitionState method not found.");

        private readonly StudioTeamGAgent _agent = new();

        public StudioTeamState Apply(StudioTeamState current, IMessage evt)
        {
            var result = TransitionStateMethod.Invoke(_agent, [current, evt])
                ?? throw new InvalidOperationException("TransitionState returned null.");
            return (StudioTeamState)result;
        }
    }

    private static StudioTeamGAgent NewHandlerAgent(
        StudioTeamState state,
        RecordingEventSourcing eventSourcing)
    {
        var agent = new StudioTeamGAgent
        {
            EventSourcing = eventSourcing,
        };
        StudioTeamStateSetter.Set(agent, state);
        return agent;
    }

    private static class StudioTeamStateSetter
    {
        private static readonly FieldInfo StateField =
            typeof(StudioTeamGAgent).BaseType!
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GAgent state field not found.");

        public static void Set(StudioTeamGAgent agent, StudioTeamState state) =>
            StateField.SetValue(agent, state.Clone());
    }

    private sealed class RecordingEventSourcing(StudioTeamState replayState)
        : IEventSourcingBehavior<StudioTeamState>
    {
        private readonly List<IMessage> _pending = [];
        public List<IMessage> RaisedEvents { get; } = [];
        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            RaisedEvents.Add(evt);
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(
            CancellationToken ct = default)
        {
            var result = EventSourcingTestCommit.From(_pending, CurrentVersion);
            CurrentVersion = result.LatestVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(StudioTeamState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<StudioTeamState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<StudioTeamState?>(replayState.Clone());

        public void DiscardPendingEvents()
        {
            RaisedEvents.Clear();
            _pending.Clear();
        }

        public StudioTeamState TransitionState(StudioTeamState current, IMessage evt) =>
            _agent.Apply(current, evt);

        private readonly StudioTeamStateApplier _agent = new();
    }
}
