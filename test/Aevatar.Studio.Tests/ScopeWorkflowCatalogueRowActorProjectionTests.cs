using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ScopeWorkflowCatalogueRowActorProjectionTests
{
    [Fact]
    public async Task ProjectAsync_ShouldUpsertRowFromActorCommittedStateVersion()
    {
        var dispatcher = new RecordingRowDispatcher();
        var projector = new ScopeWorkflowCatalogueRowCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-06T12:00:00Z")));
        var state = new ScopeWorkflowCatalogueRowState
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            LastEventId = "evt-row-2",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T00:00:00Z")),
            DraftSource = new ScopeWorkflowCatalogueSourceSnapshot
            {
                SourceKind = ScopeWorkflowCatalogueSourceDocument.DraftSourceKind,
                Name = "Draft Display",
                Description = "draft desc",
                SourceUpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-04T00:00:00Z")),
            },
            ServiceSource = new ScopeWorkflowCatalogueSourceSnapshot
            {
                SourceKind = ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind,
                Name = "Published Workflow",
                SourceUpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T00:00:00Z")),
                ServiceKey = "svc-key",
                WorkflowName = "Published Workflow",
                CommittedActorId = "workflow-actor-live",
                ActiveRevisionId = "rev-live",
                DeploymentId = "dep-1",
                DeploymentStatus = "Active",
                PublishedServiceId = "published-service-1",
            },
        };

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = ScopeWorkflowCatalogueRowMaterializer.BuildRowActorId("scope-1", "wf-shared"),
                ProjectionKind = "scope-workflow-catalogue-row",
            },
            WrapCommitted(state, version: 2, eventId: "evt-row-2"));

        var row = dispatcher.Upserts.Should().ContainSingle().Subject;
        row.Id.Should().Be("scope-1:workflow:wf-shared");
        row.ActorId.Should().Be("scope-workflow-catalogue-row:scope-1:wf-shared");
        row.StateVersion.Should().Be(2);
        row.LastEventId.Should().Be("evt-row-2");
        row.Name.Should().Be("Draft Display");
        row.Description.Should().Be("draft desc");
        row.HasDraftSource.Should().BeTrue();
        row.HasPublishedSource.Should().BeTrue();
        row.SourceWatermarkUtc.Should().Be(DateTimeOffset.Parse("2026-08-05T00:00:00Z"));
        row.UpdatedAtSource.Should().Be(ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);
        row.PublishedServiceId.Should().Be("published-service-1");
    }

    [Fact]
    public void Transition_ShouldIgnoreStaleSourceSnapshot_WhenNewerSourceAlreadyApplied()
    {
        var current = new ScopeWorkflowCatalogueRowState
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            DraftSource = Source(ScopeWorkflowCatalogueSourceDocument.DraftSourceKind, "new draft", "2026-08-06T00:00:00Z"),
            DraftWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T00:00:00Z")),
        };

        var next = ScopeWorkflowCatalogueRowGAgent.Transition(current, new ScopeWorkflowCatalogueRowSourcesObservedEvent
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            DraftSource = Source(ScopeWorkflowCatalogueSourceDocument.DraftSourceKind, "old draft", "2026-08-05T00:00:00Z"),
            DraftWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T00:00:00Z")),
            ObservationEventId = "evt-old",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T01:00:00Z")),
        });

        next.DraftSource.Should().NotBeNull();
        next.DraftSource!.Name.Should().Be("new draft");
        next.DraftWatermarkUtc.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-08-06T00:00:00Z"));
    }

    [Fact]
    public void Transition_ShouldIgnoreStaleSourceTombstone_WhenNewerSourceAlreadyApplied()
    {
        var current = new ScopeWorkflowCatalogueRowState
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            ServiceSource = Source(ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind, "published", "2026-08-06T00:00:00Z"),
            ServiceWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T00:00:00Z")),
        };

        var next = ScopeWorkflowCatalogueRowGAgent.Transition(current, new ScopeWorkflowCatalogueRowSourcesObservedEvent
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            ServiceWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T00:00:00Z")),
            ObservationEventId = "evt-old-delete",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T01:00:00Z")),
        });

        next.ServiceSource.Should().NotBeNull();
        next.ServiceSource!.Name.Should().Be("published");
        next.ServiceWatermarkUtc.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-08-06T00:00:00Z"));
    }

    [Fact]
    public void Transition_ShouldApplyFreshSourceTombstone_WhenDeleteWatermarkIsNewer()
    {
        var current = new ScopeWorkflowCatalogueRowState
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            ServiceSource = Source(ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind, "published", "2026-08-05T00:00:00Z"),
            ServiceWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T00:00:00Z")),
        };

        var next = ScopeWorkflowCatalogueRowGAgent.Transition(current, new ScopeWorkflowCatalogueRowSourcesObservedEvent
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            ServiceWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T00:00:00Z")),
            ObservationEventId = "evt-delete",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T01:00:00Z")),
        });

        next.ServiceSource.Should().BeNull();
        next.ServiceWatermarkUtc.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-08-06T00:00:00Z"));
    }

    [Fact]
    public void RepresentsCurrentState_ShouldRequireMatchingWatermarks_WhenSourcesAreUnchanged()
    {
        var current = new ScopeWorkflowCatalogueRowState
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            DraftSource = Source(ScopeWorkflowCatalogueSourceDocument.DraftSourceKind, "draft", "2026-08-06T00:00:00Z"),
            DraftWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T00:00:00Z")),
            ServiceWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T00:00:00Z")),
        };

        var command = CurrentStateCommand(current);
        command.ServiceWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-07T00:00:00Z"));

        ScopeWorkflowCatalogueRowGAgent.RepresentsCurrentState(current, command).Should().BeFalse();
    }

    [Fact]
    public void Transition_ShouldBuildStableEventId_WhenObservationEventIdIsMissing()
    {
        var next = ScopeWorkflowCatalogueRowGAgent.Transition(new ScopeWorkflowCatalogueRowState(), new ScopeWorkflowCatalogueRowSourcesObservedEvent
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            DraftSource = Source(ScopeWorkflowCatalogueSourceDocument.DraftSourceKind, "draft", "2026-08-06T00:00:00Z"),
            DraftWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T00:00:00Z")),
            ObservationEventId = " ",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T01:00:00Z")),
        });

        next.LastAppliedEventVersion.Should().Be(1);
        next.LastEventId.Should().Be("scope-1:wf-shared:catalogue-row:1");
    }

    [Fact]
    public void RepresentsCurrentState_ShouldMatch_WhenSourcesAndWatermarksAreUnchanged()
    {
        var current = new ScopeWorkflowCatalogueRowState
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            DraftSource = Source(ScopeWorkflowCatalogueSourceDocument.DraftSourceKind, "draft", "2026-08-06T00:00:00Z"),
            DraftWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T00:00:00Z")),
            ServiceSource = Source(ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind, "published", "2026-08-05T00:00:00Z"),
            ServiceWatermarkUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T00:00:00Z")),
        };

        ScopeWorkflowCatalogueRowGAgent.RepresentsCurrentState(current, CurrentStateCommand(current)).Should().BeTrue();
    }

    [Fact]
    public async Task ProjectAsync_ShouldDeleteRow_WhenActorStateHasNoSources()
    {
        var dispatcher = new RecordingRowDispatcher();
        var projector = new ScopeWorkflowCatalogueRowCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-06T12:00:00Z")));
        var observedAt = DateTimeOffset.Parse("2026-08-06T02:00:00Z");
        var state = new ScopeWorkflowCatalogueRowState
        {
            ScopeId = "scope-1",
            WorkflowId = "wf-shared",
            LastEventId = "evt-row-empty",
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
        };

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = ScopeWorkflowCatalogueRowMaterializer.BuildRowActorId("scope-1", "wf-shared"),
                ProjectionKind = "scope-workflow-catalogue-row",
            },
            WrapCommitted(state, version: 3, eventId: "evt-row-empty"));

        dispatcher.Upserts.Should().BeEmpty();
        dispatcher.DeleteMarkers.Should().ContainSingle().Which.Should().Be(
            new ProjectionDocumentDeleteMarker(
                "scope-1:workflow:wf-shared",
                "scope-workflow-catalogue-row:scope-1:wf-shared",
                3,
                "evt-row-empty",
                observedAt));
    }

    private static ScopeWorkflowCatalogueSourceSnapshot Source(
        string sourceKind,
        string name,
        string sourceUpdatedAtUtc) =>
        new()
        {
            SourceKind = sourceKind,
            Name = name,
            SourceUpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse(sourceUpdatedAtUtc)),
        };

    private static ObserveScopeWorkflowCatalogueSourcesCommand CurrentStateCommand(
        ScopeWorkflowCatalogueRowState state) =>
        new()
        {
            ScopeId = state.ScopeId,
            WorkflowId = state.WorkflowId,
            DraftSource = state.DraftSource?.Clone(),
            ServiceSource = state.ServiceSource?.Clone(),
            DraftWatermarkUtc = state.DraftWatermarkUtc?.Clone(),
            ServiceWatermarkUtc = state.ServiceWatermarkUtc?.Clone(),
        };

    private static EventEnvelope WrapCommitted(
        ScopeWorkflowCatalogueRowState state,
        long version,
        string eventId) =>
        new()
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T03:00:00Z")),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(new ScopeWorkflowCatalogueRowSourcesObservedEvent
                    {
                        ScopeId = state.ScopeId,
                        WorkflowId = state.WorkflowId,
                        DraftSource = state.DraftSource?.Clone(),
                        ServiceSource = state.ServiceSource?.Clone(),
                        ObservationEventId = eventId,
                        ObservedAt = state.ObservedAt?.Clone(),
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class RecordingRowDispatcher
        : IProjectionWriteDispatcher<ScopeWorkflowCatalogueRowDocument>
    {
        public List<ScopeWorkflowCatalogueRowDocument> Upserts { get; } = [];

        public List<ProjectionDocumentDeleteMarker> DeleteMarkers { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ScopeWorkflowCatalogueRowDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());

        public Task<ProjectionWriteResult> DeleteAsync(
            ProjectionDocumentDeleteMarker marker,
            CancellationToken ct = default)
        {
            DeleteMarkers.Add(marker);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
