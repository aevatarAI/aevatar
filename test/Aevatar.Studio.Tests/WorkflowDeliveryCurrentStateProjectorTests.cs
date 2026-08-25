using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryCurrentStateProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldCopyImmutablePackageAndInstallationAtCommittedActorVersion()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var observedAt = DateTimeOffset.Parse("2026-08-16T03:00:00Z");
        var projector = new WorkflowDeliveryCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(observedAt));
        var state = BuildState();

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = WorkflowDeliveryConventions.BuildActorId("delivery-alpha"),
                ProjectionKind = WorkflowDeliveryGAgent.ProjectionKind,
            },
            WrapCommitted(state, version: 17));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.StateVersion.Should().Be(17);
        document.LastEventId.Should().Be("delivery-event-17");
        document.UpdatedAt.ToDateTimeOffset().Should().Be(At(1).ToDateTimeOffset());
        document.DeliveryId.Should().Be("delivery-alpha");
        document.TargetScopeId.Should().Be("scope-alpha");
        document.Package.SourceYaml.Should().Be("name: workflow-alpha\n");
        document.Package.SourceHash.Should().Be("sha256-alpha");
        document.Package.Should().NotBeSameAs(state.Package);
        document.Connections.Should().ContainSingle();
        document.Connections[0].UserServiceId.Should().Be("user-service-alpha");
        document.Installation.InstallationId.Should().Be("installation-alpha");
        document.Installation.Status.Should().Be(WorkflowInstallationStatus.Ready);
        document.Installation.ContinuationClaim.Should().NotBeNull();
        document.Installation.ContinuationClaim.ClaimId.Should().Be("claim-readiness-a1");
        document.Installation.ContinuationClaim.Should().NotBeSameAs(state.Installation.ContinuationClaim);
        document.Installation.ReadinessEvidence.Should().NotBeNull();
        document.Installation.ReadinessEvidence.Should().NotBeSameAs(state.Installation.ReadinessEvidence);
        document.Installation.ReadinessEvidence.AcceptanceRun.AcceptanceRunId.Should()
            .Be("acceptance-run-alpha");
        document.Installation.ReadinessEvidence.Artifacts.Should().ContainSingle().Which
            .ArtifactId.Should().Be("artifact-alpha");
        document.Installation.Should().NotBeSameAs(state.Installation);
    }

    private static WorkflowDeliveryState BuildState()
    {
        var state = new WorkflowDeliveryState
        {
            DeliveryId = "delivery-alpha",
            Package = new WorkflowPackageVersionSnapshot
            {
                PackageId = "package-alpha",
                PackageVersionId = "package-alpha@sha256-alpha",
                WorkflowName = "workflow-alpha",
                Version = "1",
                DisplayName = "Workflow Alpha",
                SourceYaml = "name: workflow-alpha\n",
                SourceHash = "sha256-alpha",
                CreatedBy = "admin-alpha",
                CreatedAtUtc = At(0),
            },
            TargetScopeId = "scope-alpha",
            ExpiresAtUtc = At(8),
            LifecycleStatus = WorkflowDeliveryLifecycleStatus.Active,
            CreatedBy = "admin-alpha",
            CreatedAtUtc = At(0),
            Installation = new WorkflowInstallationState
            {
                InstallationId = "installation-alpha",
                IdempotencyKey = "publish-alpha",
                ScopeId = "scope-alpha",
                TeamId = "team-alpha",
                TriggerIntent = new WorkflowDeliveryTriggerIntent
                {
                    Kind = WorkflowDeliveryTriggerKind.None,
                },
                SourceHash = "sha256-alpha",
                ResolvedHash = "resolved-alpha",
                ResolvedYaml = "name: workflow-alpha\n",
                Status = WorkflowInstallationStatus.Ready,
                Stage = "ready",
                ReadinessEvidence = ReadyEvidence(),
                Attempt = 1,
                CreatedAtUtc = At(1),
                UpdatedAtUtc = At(1),
                ContinuationClaim = new WorkflowInstallationContinuationClaim
                {
                    ClaimId = "claim-readiness-a1",
                    ClaimantId = "worker-alpha",
                    ExpectedStatus = WorkflowInstallationStatus.ProvisioningAccepted,
                    Attempt = 1,
                    OperationId = "installation-alpha:provision:a1",
                    ClaimedAtUtc = At(1),
                    ExpiresAtUtc = At(2),
                },
            },
        };
        state.Connections.Add(new WorkflowDeliveryConnectionState
        {
            SlotKey = "mail",
            ServiceSlug = "lark",
            LinkId = "link-alpha",
            Status = WorkflowDeliveryConnectionStatus.Completed,
            UserServiceId = "user-service-alpha",
            UpdatedAtUtc = At(1),
        });
        return state;
    }

    private static WorkflowInstallationReadinessEvidence ReadyEvidence()
    {
        var evidence = new WorkflowInstallationReadinessEvidence
        {
            PublishedService = new WorkflowPublishedServiceReadinessEvidence
            {
                PublishedServiceId = "service-alpha",
                Committed = true,
                Runnable = true,
                CommittedStateVersion = 20,
            },
            BoundRevision = new WorkflowBoundRevisionReadinessEvidence
            {
                RevisionId = "revision-alpha",
                BindingRunId = "binding-run-alpha",
                Bound = true,
                CommittedStateVersion = 21,
            },
            Trigger = new WorkflowTriggerReadinessEvidence
            {
                Intent = new WorkflowDeliveryTriggerIntent
                {
                    Kind = WorkflowDeliveryTriggerKind.None,
                },
                NoTrigger = new WorkflowNoTriggerReadinessEvidence { Ready = true },
            },
            AcceptanceRun = new WorkflowAcceptanceRunReadinessEvidence
            {
                AcceptanceRunId = "acceptance-run-alpha",
                Status = WorkflowAcceptanceRunStatus.TerminalSuccess,
                CommittedStateVersion = 22,
            },
        };
        evidence.Artifacts.Add(new WorkflowInstallationArtifactEvidence
        {
            Kind = WorkflowInstallationArtifactKind.RunOutput,
            ArtifactId = "artifact-alpha",
            VerificationStatus = WorkflowInstallationArtifactVerificationStatus.Verified,
            VerificationReference = "verification-alpha",
            ContentDigest = "sha256-artifact-alpha",
        });
        return evidence;
    }

    private static EventEnvelope WrapCommitted(WorkflowDeliveryState state, long version) =>
        new()
        {
            Id = "delivery-event-17",
            Route = EnvelopeRouteSemantics.CreateObserverPublication(
                WorkflowDeliveryConventions.BuildActorId("delivery-alpha")),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "delivery-event-17",
                    Version = version,
                    EventData = Any.Pack(new WorkflowDeliveryAccessRecordedEvent
                    {
                        AccessedAtUtc = At(1),
                    }),
                    Timestamp = At(1),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private static Timestamp At(int hours) => Timestamp.FromDateTimeOffset(
        DateTimeOffset.Parse("2026-08-16T01:00:00Z").AddHours(hours));

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<WorkflowDeliveryCurrentStateDocument>
    {
        public List<WorkflowDeliveryCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkflowDeliveryCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
