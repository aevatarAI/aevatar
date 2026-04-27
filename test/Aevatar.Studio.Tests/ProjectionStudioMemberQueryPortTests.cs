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
            includeLastBinding: true);

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
        detail.LastBinding.Should().NotBeNull();
        detail.LastBinding!.RevisionId.Should().Be("rev-bind");
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
        var inScopeA = NewDocument(scopeId: ScopeId, memberId: "m-1");
        var inScopeB = NewDocument(scopeId: ScopeId, memberId: "m-2");
        var inOtherScope = NewDocument(scopeId: "scope-other", memberId: "m-3");

        var reader = new StubDocumentReader([inScopeA, inScopeB, inOtherScope]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var roster = await port.ListAsync(ScopeId);

        roster.ScopeId.Should().Be(ScopeId);
        roster.Members.Select(m => m.MemberId).Should().BeEquivalentTo("m-1", "m-2");
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

        detail!.ImplementationRef!.ActorTypeName.Should().Be("MyActor");
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

        var reader = new StubDocumentReader([document]);
        var port = new ProjectionStudioMemberQueryPort(reader);

        var detail = await port.GetAsync(ScopeId, "m-1");

        detail!.ImplementationRef.Should().BeNull();
        detail.LastBinding.Should().BeNull();
    }

    private static StudioMemberCurrentStateDocument NewDocument(
        string scopeId,
        string memberId,
        StudioMemberImplementationKind implementationKind = StudioMemberImplementationKind.Workflow,
        StudioMemberLifecycleStage lifecycle = StudioMemberLifecycleStage.Created,
        bool includeImplementationRef = false,
        bool includeLastBinding = false)
    {
        var actorId = StudioMemberConventions.BuildActorId(scopeId, memberId);
        var publishedServiceId = StudioMemberConventions.BuildPublishedServiceId(memberId);
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var state = new StudioMemberState
        {
            MemberId = memberId,
            ScopeId = scopeId,
            DisplayName = $"Member {memberId}",
            Description = string.Empty,
            ImplementationKind = implementationKind,
            PublishedServiceId = publishedServiceId,
            LifecycleStage = lifecycle,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        if (includeImplementationRef)
        {
            state.ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "wf-1",
                    WorkflowRevision = "v2",
                },
            };
        }

        if (includeLastBinding)
        {
            state.LastBinding = new StudioMemberBindingContract
            {
                PublishedServiceId = publishedServiceId,
                RevisionId = "rev-bind",
                ImplementationKind = implementationKind,
                BoundAtUtc = now,
            };
        }

        return new StudioMemberCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = now,
            StateRoot = Any.Pack(state),
            MemberId = memberId,
            ScopeId = scopeId,
            DisplayName = state.DisplayName,
            Description = state.Description,
            ImplementationKind = (int)implementationKind,
            LifecycleStage = (int)lifecycle,
            PublishedServiceId = publishedServiceId,
            LastBoundRevisionId = state.LastBinding?.RevisionId ?? string.Empty,
            CreatedAt = now,
        };
    }

    private static StudioMemberCurrentStateDocument NewDocumentWithImplementation(
        StudioMemberImplementationKind implementationKind,
        StudioMemberImplementationRef implementationRef)
    {
        var doc = NewDocument(ScopeId, "m-1", implementationKind);
        var state = doc.StateRoot.Unpack<StudioMemberState>();
        state.ImplementationRef = implementationRef;
        doc.StateRoot = Any.Pack(state);
        return doc;
    }

    private sealed class StubDocumentReader
        : IProjectionDocumentReader<StudioMemberCurrentStateDocument, string>
    {
        private readonly Dictionary<string, StudioMemberCurrentStateDocument> _byId;

        public StubDocumentReader(IReadOnlyList<StudioMemberCurrentStateDocument> documents)
        {
            _byId = documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
        }

        public Task<StudioMemberCurrentStateDocument?> GetAsync(
            string key, CancellationToken ct = default)
        {
            return Task.FromResult(_byId.TryGetValue(key, out var doc) ? doc : null);
        }

        public Task<ProjectionDocumentQueryResult<StudioMemberCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query, CancellationToken ct = default)
        {
            // Honor the scope_id filter the query port issues.
            var scopeFilter = query.Filters.FirstOrDefault(
                f => string.Equals(f.FieldPath, "scope_id", StringComparison.Ordinal));

            IEnumerable<StudioMemberCurrentStateDocument> items = _byId.Values;
            if (scopeFilter != null && scopeFilter.Value.RawValue is string scope)
            {
                items = items.Where(d => string.Equals(d.ScopeId, scope, StringComparison.Ordinal));
            }

            return Task.FromResult(new ProjectionDocumentQueryResult<StudioMemberCurrentStateDocument>
            {
                Items = items.Take(query.Take).ToList(),
            });
        }
    }
}
