using Aevatar.Workflow.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowActorBindingProjectorTests
{
    [Fact]
    public void WorkflowActorBindingDocumentContract_ShouldCarryBoundWorkflowRevisionIdentity()
    {
        WorkflowActorBindingDocument.Descriptor.FindFieldByName("workflow_id")!.FieldNumber.Should().Be(16);
        WorkflowActorBindingDocument.Descriptor.FindFieldByName("revision_id")!.FieldNumber.Should().Be(17);
        WorkflowActorBindingDocument.Descriptor.FindFieldByName("expected_execution_mode")!.FieldNumber.Should().Be(18);
        WorkflowActorBindingDocument.Descriptor.FindFieldByName("catalog_publication_contract_version")!
            .FieldNumber.Should().Be(20);
    }

    [Fact]
    public async Task ProjectAsync_ShouldCaptureDefinitionBinding()
    {
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(
            dispatcher,
            new StaticClock(new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero)));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-binding",
        };
        var capabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: direct",
            new Dictionary<string, string> { [" child "] = "yaml-child" },
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = " direct ",
                    WorkflowYaml = "name: direct",
                    SourceKind = "service_revision",
                    WorkflowId = "wf-alpha",
                    RevisionId = "rev-alpha",
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    CapabilityAdmissionPlan = capabilityAdmissionPlan,
                    CatalogPublicationContractVersion = WorkflowCatalogPublicationContracts.CurrentVersion,
                    InlineWorkflowYamls =
                    {
                        [" child "] = "yaml-child",
                    },
                },
                version: 1,
                id: "evt-definition",
                utcTimestamp: new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        var document = dispatcher.Documents["actor-1"];
        document.ActorKind.Should().Be(WorkflowActorKind.Definition);
        document.DefinitionActorId.Should().Be("actor-1");
        document.RunId.Should().BeEmpty();
        document.WorkflowName.Should().Be("direct");
        document.WorkflowYaml.Should().Be("name: direct");
        document.InlineWorkflowYamls.Should().ContainKey("child").WhoseValue.Should().Be("yaml-child");
        document.SourceKind.Should().Be("service_revision");
        document.WorkflowId.Should().Be("wf-alpha");
        document.RevisionId.Should().Be("rev-alpha");
        document.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        document.CatalogPublicationContractVersion.Should().Be(WorkflowCatalogPublicationContracts.CurrentVersion);
        document.CapabilityAdmissionPlan.AdmissionDigest.Should().Be(capabilityAdmissionPlan.AdmissionDigest);
        document.LastEventId.Should().Be("evt-definition");
    }

    [Fact]
    public async Task ProjectAsync_ShouldCaptureRunBinding_AndNormalizeRunId()
    {
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-2",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new BindWorkflowRunDefinitionEvent
                {
                    DefinitionActorId = "definition-2",
                    RunId = " run-2 ",
                    WorkflowName = " auto ",
                    WorkflowYaml = "name: auto",
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Durable,
                    InlineWorkflowYamls =
                    {
                        [" child "] = "yaml-child",
                    },
                },
                version: 2,
                id: "evt-run"),
            CancellationToken.None);

        var document = dispatcher.Documents["actor-2"];
        document.ActorKind.Should().Be(WorkflowActorKind.Run);
        document.DefinitionActorId.Should().Be("definition-2");
        document.RunId.Should().Be("run-2");
        document.WorkflowName.Should().Be("auto");
        document.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        document.InlineWorkflowYamls.Should().ContainKey("child");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreUnrelatedEvents()
    {
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(dispatcher, new StaticClock(DateTimeOffset.UtcNow));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-3",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new WorkflowCompletedEvent
                {
                    WorkflowName = "ignored",
                    Success = true,
                },
                version: 3,
                id: "evt-ignored"),
            CancellationToken.None);

        dispatcher.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldOverwriteBindingDocument_FromCommittedPayloadOnly()
    {
        var dispatcher = new FakeStoreDispatcher();
        var projector = new WorkflowActorBindingProjector(
            dispatcher,
            new StaticClock(new DateTimeOffset(2026, 3, 14, 13, 0, 0, TimeSpan.Zero)));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-4",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = " first ",
                    WorkflowYaml = "name: first",
                    ScopeId = " scope-a ",
                    InlineWorkflowYamls =
                    {
                        ["kept-only-if-replayed"] = "old-yaml",
                    },
                },
                version: 4,
                id: "evt-first",
                utcTimestamp: new DateTime(2026, 3, 14, 13, 0, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = " second ",
                    WorkflowYaml = "name: second",
                    ScopeId = " scope-b ",
                },
                version: 5,
                id: "evt-second",
                utcTimestamp: new DateTime(2026, 3, 14, 13, 1, 0, DateTimeKind.Utc)),
            CancellationToken.None);

        var document = dispatcher.Documents["actor-4"];
        document.WorkflowName.Should().Be("second");
        document.WorkflowYaml.Should().Be("name: second");
        document.ScopeId.Should().Be("scope-b");
        document.InlineWorkflowYamls.Should().BeEmpty();
        document.CreatedAt.Should().Be(new DateTimeOffset(2026, 3, 14, 13, 1, 0, TimeSpan.Zero));
        document.UpdatedAt.Should().Be(new DateTimeOffset(2026, 3, 14, 13, 1, 0, TimeSpan.Zero));
        document.LastEventId.Should().Be("evt-second");
        dispatcher.ReadCount.Should().Be(0);
    }

    private sealed class FakeStoreDispatcher
        : IProjectionWriteDispatcher<WorkflowActorBindingDocument>
    {
        public Dictionary<string, WorkflowActorBindingDocument> Documents { get; } = new(StringComparer.Ordinal);
        public int ReadCount { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(WorkflowActorBindingDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Documents[readModel.Id] = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var removed = Documents.Remove(id);
            return Task.FromResult(removed
                ? ProjectionWriteResult.Applied()
                : ProjectionWriteResult.Duplicate());
        }
    }

    private sealed class StaticClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static EventEnvelope WrapCommitted(
        IMessage evt,
        long version,
        string id,
        DateTime? utcTimestamp = null)
    {
        var occurredAt = Timestamp.FromDateTime((utcTimestamp ?? DateTime.UtcNow).ToUniversalTime());
        return new EventEnvelope
        {
            Id = id,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("binding-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = id,
                    Version = version,
                    Timestamp = occurredAt,
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new Empty()),
            }),
        };
    }
}
