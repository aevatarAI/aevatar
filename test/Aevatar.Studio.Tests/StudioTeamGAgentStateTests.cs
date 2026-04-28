using System.Reflection;
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
}
