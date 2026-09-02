using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the read-side invariants for the StudioMember query port:
///
/// - GetAsync uses the canonical actor-id key and is scope-pinned (a member
///   from another scope must not leak).
/// - ListAsync filters by scope_id and surfaces the denormalized roster
///   fields the projector wrote (publishedServiceId, lifecycle, etc.).
/// - Detail unpacks the typed implementation_ref and last_binding from the
///   state_root rather than re-deriving them.
/// </summary>
public sealed class ProjectionStudioMemberQueryPortTests
{
    // Refactor (iter74/cluster-074-studio-team-members-query-fanout):
    //   Old pattern: Host loops scope roster pages + Host-side TeamId filter
    //   New principle: ReadModel query port owns scope_id+team_id filter before pagination
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task GetAsync_ShouldReturnDetail_WhenDocumentExists()
    {
        var document = NewDocument(
            scopeId: ScopeId,
            memberId: "m-1",
            implementationKind: StudioMemberImplementationKind.Workflow,
            lifecycle: StudioMemberLifecycleStage.BuildReady,
            includeImplementationRef: true,
            includeLastBinding: true,
            includeBindingStatus: true);
        document.StateVersion = 7;

        var reader = new StubDocumentReader([document]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var detail = await port.GetAsync(ScopeId, "m-1");

        detail.Should().NotBeNull();
        detail!.Summary.MemberId.Should().Be("m-1");
        detail.Summary.PublishedServiceId.Should().Be("member-m-1");
        detail.Summary.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        detail.Summary.LifecycleStage.Should().Be(MemberLifecycleStageNames.BuildReady);
        detail.ImplementationRef.Should().NotBeNull();
        detail.ImplementationRef!.WorkflowId.Should().Be("wf-1");
        detail.ImplementationRef.WorkflowRevision.Should().Be("v2");
        detail.Summary.ImplementationRef.Should().BeEquivalentTo(detail.ImplementationRef);
        detail.LastBinding.Should().NotBeNull();
        detail.LastBinding!.RevisionId.Should().Be("rev-bind");
        detail.CurrentBindingRun.Should().NotBeNull();
        detail.CurrentBindingRun!.BindingRunId.Should().Be("bind-1");
        detail.CurrentBindingRun.Status.Should().Be(StudioMemberBindingRunStatusNames.PlatformBindingPending);
        detail.CurrentBindingRun.StateVersion.Should().Be(7);
        detail.CurrentBindingRun.Result.Should().NotBeNull();
        detail.CurrentBindingRun.Result!.ExpectedActorId.Should().Be("scope-workflow:scope-1:m-1");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenDocumentMissing()
    {
        var reader = new StubDocumentReader([]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var detail = await port.GetAsync(ScopeId, "m-missing");

        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenDocumentExistsInDifferentScope()
    {
        // The document with this id exists, but its scope_id is different.
        // Read port must reject so callers cannot probe across scopes by
        // guessing the actor-id layout.
        var foreign = NewDocument(scopeId: "scope-other", memberId: "m-1");
        // Stub reader lookups by id, so the lookup will succeed here but the
        // port should still filter by the scope_id field.
        var reader = new StubDocumentReader([foreign]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var detail = await port.GetAsync(ScopeId, "m-1");

        detail.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnOnlyMembersInScope()
    {
        var inScopeA = NewDocument(scopeId: ScopeId, memberId: "m-1", includeImplementationRef: true);
        inScopeA.DisplayName = "Renamed Member";
        var inScopeB = NewDocument(scopeId: ScopeId, memberId: "m-2");
        var inOtherScope = NewDocument(scopeId: "scope-other", memberId: "m-3");

        var reader = new StubDocumentReader([inScopeA, inScopeB, inOtherScope]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var roster = await port.ListAsync(ScopeId);

        roster.ScopeId.Should().Be(ScopeId);
        roster.Members.Select(m => m.MemberId).Should().BeEquivalentTo("m-1", "m-2");
        var workflowMember = roster.Members.Single(m => m.MemberId == "m-1");
        workflowMember.DisplayName.Should().Be("Renamed Member");
        workflowMember.ImplementationRef.Should().NotBeNull();
        workflowMember.ImplementationRef!.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        workflowMember.ImplementationRef.WorkflowId.Should().Be("wf-1");
        workflowMember.ImplementationRef.WorkflowRevision.Should().Be("v2");
        reader.QueryCallCount.Should().Be(1);
        reader.GetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_ShouldClampInvalidPageSizeAndForwardCursor()
    {
        var inScopeA = NewDocument(scopeId: ScopeId, memberId: "m-1");
        var inScopeB = NewDocument(scopeId: ScopeId, memberId: "m-2");
        var reader = new StubDocumentReader([inScopeA, inScopeB])
        {
            NextCursor = "cursor-next",
        };
        var port = new ProjectionStudioMemberQueryPort(reader);

        var roster = await port.ListAsync(
            ScopeId,
            new StudioMemberRosterPageRequest(PageSize: -1, PageToken: "cursor-1"));

        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(ProjectionStudioMemberQueryPort.MaxRosterPageSize);
        reader.LastQuery.Cursor.Should().Be("cursor-1");
        roster.NextPageToken.Should().Be("cursor-next");
        roster.Members.Select(m => m.MemberId).Should().BeEquivalentTo("m-1", "m-2");
    }

    [Fact]
    public async Task ListAsync_ShouldApplyTeamFilterBeforePagination()
    {
        var inOtherTeamA = NewDocument(scopeId: ScopeId, memberId: "m-other-1", teamId: "other-team");
        var inTeamA = NewDocument(
            scopeId: ScopeId,
            memberId: "m-team-1",
            includeImplementationRef: true,
            teamId: "team-1");
        var inOtherScope = NewDocument(scopeId: "scope-other", memberId: "m-foreign", teamId: "team-1");
        var inOtherTeamB = NewDocument(scopeId: ScopeId, memberId: "m-other-2", teamId: "other-team");
        var inTeamB = NewDocument(scopeId: ScopeId, memberId: "m-team-2", teamId: "team-1");
        var reader = new StubDocumentReader([inOtherTeamA, inTeamA, inOtherScope, inOtherTeamB, inTeamB])
        {
            NextCursor = "team-cursor-2",
        };
        var port = new ProjectionStudioMemberQueryPort(reader);

        var roster = await port.ListAsync(
            ScopeId,
            new StudioMemberRosterPageRequest(PageSize: 2, PageToken: "team-cursor-1", TeamId: " team-1 "));

        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Cursor.Should().Be("team-cursor-1");
        reader.LastQuery.Take.Should().Be(2);
        reader.LastQuery.Filters.Any(f =>
            string.Equals(f.FieldPath, "scope_id", StringComparison.Ordinal) &&
            f.Value.RawValue is string scope &&
            string.Equals(scope, ScopeId, StringComparison.Ordinal))
            .Should().BeTrue();
        reader.LastQuery.Filters.Any(f =>
            string.Equals(f.FieldPath, "team_id", StringComparison.Ordinal) &&
            f.Value.RawValue is string team &&
            string.Equals(team, "team-1", StringComparison.Ordinal))
            .Should().BeTrue();
        roster.Members.Select(m => m.MemberId).Should().ContainInOrder("m-team-1", "m-team-2");
        var workflowMember = roster.Members.Single(m => m.MemberId == "m-team-1");
        workflowMember.ImplementationRef.Should().NotBeNull();
        workflowMember.ImplementationRef!.WorkflowId.Should().Be("wf-1");
        workflowMember.ImplementationRef.WorkflowRevision.Should().Be("v2");
        roster.NextPageToken.Should().Be("team-cursor-2");
        reader.QueryCallCount.Should().Be(1);
        reader.GetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmpty_WhenScopeHasNoMembers()
    {
        var reader = new StubDocumentReader([]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var roster = await port.ListAsync(ScopeId);

        roster.ScopeId.Should().Be(ScopeId);
        roster.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_ShouldOnlyReadStudioMemberCurrentStateDocuments()
    {
        var reader = new StubDocumentReader([NewDocument(scopeId: ScopeId, memberId: "workflow-1")]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var roster = await port.ListAsync(ScopeId);

        roster.Members.Should().ContainSingle(m => m.MemberId == "workflow-1");
        reader.GetCallCount.Should().Be(0);
        reader.QueryCallCount.Should().Be(1);
        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Filters.Any(f =>
            string.Equals(f.FieldPath, "scope_id", StringComparison.Ordinal) &&
            f.Value.RawValue is string value &&
            string.Equals(value, ScopeId, StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_ShouldSurfaceScriptImplementationRef()
    {
        var document = NewDocumentWithImplementation(
            implementationKind: StudioMemberImplementationKind.Script,
            implementationRef: new StudioMemberImplementationRef
            {
                Script = new StudioMemberScriptRef
                {
                    ScriptId = "s-1",
                    ScriptRevision = "v9",
                },
            });

        var reader = new StubDocumentReader([document]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var detail = await port.GetAsync(ScopeId, "m-1");

        detail!.ImplementationRef!.ScriptId.Should().Be("s-1");
        detail.ImplementationRef.ScriptRevision.Should().Be("v9");
        detail.ImplementationRef.ImplementationKind.Should().Be(MemberImplementationKindNames.Script);
    }

    [Fact]
    public async Task GetAsync_ShouldSurfaceGAgentImplementationRef()
    {
        var document = NewDocumentWithImplementation(
            implementationKind: StudioMemberImplementationKind.Gagent,
            implementationRef: new StudioMemberImplementationRef
            {
                Gagent = new StudioMemberGAgentRef
                {
                    ActorTypeName = "MyActor",
                },
            });

        var reader = new StubDocumentReader([document]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var detail = await port.GetAsync(ScopeId, "m-1");

        detail!.ImplementationRef!.DiagnosticActorTypeName.Should().Be("MyActor");
        detail.ImplementationRef.ImplementationKind.Should().Be(MemberImplementationKindNames.GAgent);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNullImplementationRef_WhenMissing()
    {
        var document = NewDocument(
            scopeId: ScopeId,
            memberId: "m-1",
            includeImplementationRef: false,
            includeLastBinding: false);
        document.DisplayName = "Renamed Detail";

        var reader = new StubDocumentReader([document]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var detail = await port.GetAsync(ScopeId, "m-1");

        detail!.Summary.DisplayName.Should().Be("Renamed Detail");
        detail!.ImplementationRef.Should().BeNull();
        detail.LastBinding.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldNormalizeUnknownWireValuesToEmptyStrings()
    {
        var document = NewDocument(scopeId: ScopeId, memberId: "m-1");
        document.ImplementationKind = "worker";
        document.LifecycleStage = "archived";
        document.LastBoundPublishedServiceId = "svc-1";
        document.LastBoundRevisionId = "rev-1";
        document.LastBoundImplementationKind = "worker";
        document.BindingCurrentRunId = "bind-1";
        document.BindingCurrentStatus = "waiting-for-magic";
        document.BindingFailureCode = "BIND_FAILED";
        document.BindingFailureMessage = "Nope";
        document.BindingFailureAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-30T08:00:00Z"));

        var port = new ProjectionStudioMemberQueryPort(new StubDocumentReader([document]));

        var detail = await port.GetAsync(ScopeId, "m-1");

        detail!.Summary.ImplementationKind.Should().BeEmpty();
        detail.Summary.LifecycleStage.Should().BeEmpty();
        detail.LastBinding!.ImplementationKind.Should().BeEmpty();
        detail.CurrentBindingRun!.Status.Should().BeEmpty();
        detail.CurrentBindingRun.Failure!.Code.Should().Be("BIND_FAILED");
        detail.CurrentBindingRun.Failure.Message.Should().Be("Nope");
        detail.CurrentBindingRun.Failure.FailedAt.Should().Be(DateTimeOffset.Parse("2026-04-30T08:00:00Z"));
    }

    private static StudioMemberCurrentStateDocument NewDocument(
        string scopeId,
        string memberId,
        StudioMemberImplementationKind implementationKind = StudioMemberImplementationKind.Workflow,
        StudioMemberLifecycleStage lifecycle = StudioMemberLifecycleStage.Created,
        bool includeImplementationRef = false,
        bool includeLastBinding = false,
        bool includeBindingStatus = false,
        string? teamId = null)
    {
        var actorId = StudioMemberConventions.BuildActorId(scopeId, memberId);
        var publishedServiceId = StudioMemberConventions.BuildPublishedServiceId(memberId);
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var doc = new StudioMemberCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = now,
            MemberId = memberId,
            ScopeId = scopeId,
            DisplayName = $"Member {memberId}",
            Description = string.Empty,
            ImplementationKind = ToWireKind(implementationKind),
            LifecycleStage = ToWireStage(lifecycle),
            PublishedServiceId = publishedServiceId,
            CreatedAt = now,
        };

        if (includeImplementationRef)
        {
            doc.ImplementationWorkflowId = "wf-1";
            doc.ImplementationWorkflowRevision = "v2";
        }

        if (includeLastBinding)
        {
            doc.LastBoundPublishedServiceId = publishedServiceId;
            doc.LastBoundRevisionId = "rev-bind";
            doc.LastBoundImplementationKind = ToWireKind(implementationKind);
            doc.LastBoundAt = now;
        }

        if (includeBindingStatus)
        {
            doc.BindingCurrentRunId = "bind-1";
            doc.BindingCurrentStatus = StudioMemberBindingRunStatusNames.PlatformBindingPending;
            doc.BindingUpdatedAt = now;
            doc.LastBoundExpectedActorId = "scope-workflow:scope-1:m-1";
        }

        if (teamId != null)
            doc.TeamId = teamId;

        return doc;
    }

    private static StudioMemberCurrentStateDocument NewDocumentWithImplementation(
        StudioMemberImplementationKind implementationKind,
        StudioMemberImplementationRef implementationRef)
    {
        var doc = NewDocument(ScopeId, "m-1", implementationKind);
        // Reset implementation_ref fields and apply the supplied one.
        doc.ImplementationWorkflowId = string.Empty;
        doc.ImplementationWorkflowRevision = string.Empty;
        doc.ImplementationScriptId = string.Empty;
        doc.ImplementationScriptRevision = string.Empty;
        doc.ImplementationActorTypeName = string.Empty;
        if (implementationRef.Workflow != null)
        {
            doc.ImplementationWorkflowId = implementationRef.Workflow.WorkflowId;
            doc.ImplementationWorkflowRevision = implementationRef.Workflow.WorkflowRevision;
        }
        if (implementationRef.Script != null)
        {
            doc.ImplementationScriptId = implementationRef.Script.ScriptId;
            doc.ImplementationScriptRevision = implementationRef.Script.ScriptRevision;
        }
        if (implementationRef.Gagent != null)
        {
            doc.ImplementationActorTypeName = implementationRef.Gagent.ActorTypeName;
        }
        return doc;
    }

    private static string ToWireKind(StudioMemberImplementationKind kind) => kind switch
    {
        StudioMemberImplementationKind.Workflow => MemberImplementationKindNames.Workflow,
        StudioMemberImplementationKind.Script => MemberImplementationKindNames.Script,
        StudioMemberImplementationKind.Gagent => MemberImplementationKindNames.GAgent,
        _ => string.Empty,
    };

    private static string ToWireStage(StudioMemberLifecycleStage stage) => stage switch
    {
        StudioMemberLifecycleStage.Created => MemberLifecycleStageNames.Created,
        StudioMemberLifecycleStage.BuildReady => MemberLifecycleStageNames.BuildReady,
        StudioMemberLifecycleStage.BindReady => MemberLifecycleStageNames.BindReady,
        _ => string.Empty,
    };

    private sealed class StubDocumentReader
        : IProjectionDocumentReader<StudioMemberCurrentStateDocument, string>
    {
        // Refactor (iter74/cluster-074-studio-team-members-query-fanout):
        //   Old pattern: Host loops scope roster pages + Host-side TeamId filter
        //   New principle: ReadModel query port owns scope_id+team_id filter before pagination
        private readonly Dictionary<string, StudioMemberCurrentStateDocument> _byId;
        public ProjectionDocumentQuery? LastQuery { get; private set; }
        public string? NextCursor { get; init; }
        public int GetCallCount { get; private set; }
        public int QueryCallCount { get; private set; }

        public StubDocumentReader(IReadOnlyList<StudioMemberCurrentStateDocument> documents)
        {
            _byId = documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
        }

        public Task<StudioMemberCurrentStateDocument?> GetAsync(
            string key, CancellationToken ct = default)
        {
            GetCallCount++;
            return Task.FromResult(_byId.TryGetValue(key, out var doc) ? doc : null);
        }

        public Task<ProjectionDocumentQueryResult<StudioMemberCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query, CancellationToken ct = default)
        {
            QueryCallCount++;
            LastQuery = query;

            // Honor the readmodel filters before pagination, matching store
            // semantics that the query port relies on.
            var scopeFilter = query.Filters.FirstOrDefault(
                f => string.Equals(f.FieldPath, "scope_id", StringComparison.Ordinal));
            var teamFilter = query.Filters.FirstOrDefault(
                f => string.Equals(f.FieldPath, "team_id", StringComparison.Ordinal));

            IEnumerable<StudioMemberCurrentStateDocument> items = _byId.Values;
            if (scopeFilter != null && scopeFilter.Value.RawValue is string scope)
            {
                items = items.Where(d => string.Equals(d.ScopeId, scope, StringComparison.Ordinal));
            }
            if (teamFilter != null && teamFilter.Value.RawValue is string team)
            {
                items = items.Where(d => d.HasTeamId && string.Equals(d.TeamId, team, StringComparison.Ordinal));
            }

            return Task.FromResult(new ProjectionDocumentQueryResult<StudioMemberCurrentStateDocument>
            {
                Items = items.Take(query.Take).ToList(),
                NextCursor = NextCursor,
            });
        }
    }
}
