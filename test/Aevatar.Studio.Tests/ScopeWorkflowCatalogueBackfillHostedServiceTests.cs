using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Hosting.Backfill;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Workspace;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using IWorkflowYamlDocumentService = Aevatar.Studio.Application.Studio.Abstractions.IWorkflowYamlDocumentService;
using WorkflowParseResult = Aevatar.Studio.Application.Studio.Abstractions.WorkflowParseResult;

namespace Aevatar.Studio.Tests;

public sealed class ScopeWorkflowCatalogueBackfillHostedServiceTests
{
    private static readonly JsonFormatter WorkspaceStateFormatter = new(
        JsonFormatter.Settings.Default
            .WithPreserveProtoFieldNames(true)
            .WithFormatDefaultValues(true));

    [Fact]
    public async Task StartAsync_ShouldBackfillDraftAndServiceSourcesFromWorkflowNativeCurrentStateReadModels()
    {
        var serviceCatalog = new ServiceCatalogReadModel
        {
            Id = "svc-key",
            ActorId = "service-definition:scope-1:published-service-1",
            StateVersion = 3,
            LastEventId = "evt-service-catalog",
            TenantId = "scope-1",
            AppId = "workflow-app",
            Namespace = "user",
            ServiceId = "published-service-1",
            DisplayName = "Published Service",
            UpdatedAt = DateTimeOffset.Parse("2026-08-04T00:00:00Z"),
        };
        var deploymentCatalog = new ServiceDeploymentCatalogReadModel
        {
            Id = "svc-key",
            ActorId = "service-deployment:scope-1:published-service-1",
            StateVersion = 8,
            LastEventId = "evt-deployment",
            UpdatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
            Deployments =
            [
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-1",
                    RevisionId = "rev-live",
                    PrimaryActorId = "workflow-actor-live",
                    Status = ServiceDeploymentStatus.Active.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-05T01:00:00Z"),
                },
            ],
        };
        var revisionCatalog = WorkflowRevisionCatalog("svc-key", "rev-live", "wf-published", "Published Workflow");
        var workspaceState = new StudioWorkspaceState
        {
            WorkspaceId = "studio-workspace-scope-1",
            ScopeId = "scope-1",
        };
        workspaceState.Drafts.Add("wf-draft", new StudioWorkflowDraft
        {
            WorkflowId = "wf-draft",
            Name = "Fallback Draft Name",
            Yaml = "name: Draft From Yaml\ndescription: draft desc\nsteps: []\n",
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
        });
        var workspace = new StudioWorkspaceCurrentStateDocument
        {
            Id = "studio-workspace-scope-1",
            ActorId = "studio-workspace-scope-1",
            StateVersion = 4,
            LastEventId = "evt-workspace",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-02T01:00:00Z")),
            StateRootJson = WorkspaceStateFormatter.Format(workspaceState),
        };
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var service = CreateService(
            [serviceCatalog],
            [deploymentCatalog],
            [revisionCatalog],
            [workspace],
            [],
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.StartAsync(CancellationToken.None);

        sourceWriter.Upserts.Select(static source => source.SourceKind)
            .Should().BeEquivalentTo([
                ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind,
                ScopeWorkflowCatalogueSourceDocument.DraftSourceKind,
            ]);
        var serviceSource = sourceWriter.Upserts.Single(static source => source.SourceKind == ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);
        serviceSource.Id.Should().Be("scope-1:wf-published:service");
        serviceSource.ActorId.Should().Be("scope-workflow-catalogue-source:scope-1:wf-published:service");
        serviceSource.StateVersion.Should().Be(WatermarkStateVersion("2026-08-05T01:00:00Z"));
        serviceSource.WorkflowId.Should().Be("wf-published");
        serviceSource.PublishedServiceId.Should().Be("published-service-1");
        serviceSource.Name.Should().Be("Published Workflow");
        serviceSource.DeploymentId.Should().Be("dep-1");
        serviceSource.CommittedActorId.Should().Be("workflow-actor-live");

        var draftSource = sourceWriter.Upserts.Single(static source => source.SourceKind == ScopeWorkflowCatalogueSourceDocument.DraftSourceKind);
        draftSource.Id.Should().Be("scope-1:wf-draft:draft");
        draftSource.ActorId.Should().Be("scope-workflow-catalogue-source:scope-1:wf-draft:draft");
        draftSource.StateVersion.Should().Be(WatermarkStateVersion("2026-08-02T00:00:00Z"));
        draftSource.Name.Should().Be("Draft From Yaml");
        draftSource.Description.Should().Be("draft desc");

        rowWriter.Upserts.Select(static row => row.Id)
            .Should().Contain(["scope-1:workflow:wf-published", "scope-1:workflow:wf-draft"]);
    }

    [Fact]
    public async Task StartAsync_ShouldDeleteStaleDraftSourcesForParsedWorkspaceScope()
    {
        var workspaceState = new StudioWorkspaceState
        {
            WorkspaceId = "studio-workspace-scope-1",
            ScopeId = "scope-1",
        };
        workspaceState.Drafts.Add("wf-current", new StudioWorkflowDraft
        {
            WorkflowId = "wf-current",
            Name = "Current Draft",
            Yaml = "name: Current Draft\ndescription: current\nsteps: []\n",
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
        });
        var workspace = new StudioWorkspaceCurrentStateDocument
        {
            Id = "studio-workspace-scope-1",
            ActorId = "studio-workspace-scope-1",
            StateVersion = 12,
            LastEventId = "evt-workspace-current",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-02T01:00:00Z")),
            StateRootJson = WorkspaceStateFormatter.Format(workspaceState),
        };
        var existingCurrentDraft = ExistingDraftSource("scope-1", "wf-current");
        var staleDraft = ExistingDraftSource("scope-1", "wf-stale");
        var otherScopeDraft = ExistingDraftSource("scope-2", "wf-other");
        var existingSources = new[] { existingCurrentDraft, staleDraft, otherScopeDraft };
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader(existingSources, sourceWriter);
        var service = CreateService(
            [],
            [],
            [],
            [workspace],
            existingSources,
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.StartAsync(CancellationToken.None);

        sourceWriter.Upserts.Should().ContainSingle(source => source.Id == "scope-1:wf-current:draft");
        sourceWriter.DeleteMarkers.Should().ContainSingle().Which.Should().Be(new ProjectionDocumentDeleteMarker(
            "scope-1:wf-stale:draft",
            "studio-workspace-scope-1",
            WatermarkStateVersion("2026-08-02T01:00:00Z") + 1,
            "evt-workspace-current",
            DateTimeOffset.Parse("2026-08-02T01:00:00Z")));
        rowWriter.DeleteMarkers.Should().ContainSingle(marker => marker.Id == "scope-1:workflow:wf-stale");
    }

    [Fact]
    public async Task StartAsync_ShouldDeleteStaleServiceSourcesByWorkflowKey()
    {
        var staleServiceSource = ExistingServiceSource("scope-1", "wf-old", "published-service-1");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([staleServiceSource], sourceWriter);
        var service = CreateService(
            [new ServiceCatalogReadModel
            {
                Id = "svc-key",
                ActorId = "service-definition:scope-1:published-service-1",
                StateVersion = 3,
                LastEventId = "evt-service-catalog",
                TenantId = "scope-1",
                AppId = "workflow-app",
                Namespace = "user",
                ServiceId = "published-service-1",
                UpdatedAt = DateTimeOffset.Parse("2026-08-04T00:00:00Z"),
            }],
            [],
            [],
            [],
            [staleServiceSource],
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.StartAsync(CancellationToken.None);

        sourceWriter.DeleteMarkers.Should().ContainSingle().Which.Id.Should().Be("scope-1:wf-old:service");
        rowWriter.DeleteMarkers.Should().ContainSingle().Which.Id.Should().Be("scope-1:workflow:wf-old");
    }

    [Fact]
    public async Task StartAsync_ShouldSkipMalformedWorkflowServiceRevisionWithoutFailingBackfill()
    {
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var service = CreateService(
            [new ServiceCatalogReadModel
            {
                Id = "svc-key",
                ActorId = "service-definition:scope-1:published-service-1",
                StateVersion = 3,
                LastEventId = "evt-service-catalog",
                TenantId = "scope-1",
                AppId = "workflow-app",
                Namespace = "user",
                ServiceId = "published-service-1",
                UpdatedAt = DateTimeOffset.Parse("2026-08-04T00:00:00Z"),
            }],
            [new ServiceDeploymentCatalogReadModel
            {
                Id = "svc-key",
                ActorId = "service-deployment:scope-1:published-service-1",
                StateVersion = 8,
                LastEventId = "evt-deployment",
                UpdatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
                Deployments =
                [
                    new ServiceDeploymentReadModel
                    {
                        DeploymentId = "dep-1",
                        RevisionId = "rev-malformed",
                        PrimaryActorId = "workflow-actor-live",
                        Status = ServiceDeploymentStatus.Active.ToString(),
                        UpdatedAt = DateTimeOffset.Parse("2026-08-05T01:00:00Z"),
                    },
                ],
            }],
            [MalformedWorkflowRevisionCatalog("svc-key", "rev-malformed")],
            [],
            [],
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.StartAsync(CancellationToken.None);

        sourceWriter.Upserts.Should().BeEmpty();
        rowWriter.Upserts.Should().BeEmpty();
        rowWriter.DeleteMarkers.Should().BeEmpty();
    }

    [Fact]
    public async Task RevisionSourceProjector_ShouldRefreshCatalogueRow_WhenDeploymentWasMaterializedBeforeRevision()
    {
        var identity = ServiceIdentity("scope-1", "workflow-app", "user", "published-service-1");
        var serviceKey = ServiceKeys.Build(identity);
        var deploymentCatalog = new ServiceDeploymentCatalogReadModel
        {
            Id = serviceKey,
            ActorId = "service-deployment:scope-1:published-service-1",
            StateVersion = 8,
            LastEventId = "evt-deployment",
            UpdatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
            Deployments =
            [
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-1",
                    RevisionId = "rev-live",
                    PrimaryActorId = "workflow-actor-live",
                    Status = ServiceDeploymentStatus.Active.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-05T01:00:00Z"),
                },
            ],
        };
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var serviceProjector = new ScopeWorkflowCatalogueServiceSourceProjector(
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([deploymentCatalog]),
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, rowWriter),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-05T02:00:00Z")));
        var revisionProjector = new ScopeWorkflowCatalogueRevisionSourceProjector(serviceProjector);
        var revisionCatalog = WorkflowRevisionCatalog(serviceKey, "rev-live", "wf-published", "Published Workflow");
        var revisionState = ToRevisionCatalogState(identity, revisionCatalog);

        await revisionProjector.ProjectAsync(
            new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = "service-revisions:svc-key",
                ProjectionKind = "service-revisions",
            },
            BuildRevisionEnvelope(
                new ServiceRevisionPublishedEvent
                {
                    Identity = revisionState.Identity.Clone(),
                    RevisionId = "rev-live",
                    PublishedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T00:30:00Z")),
                },
                revisionState,
                "evt-revision"));

        var source = sourceWriter.Upserts.Should().ContainSingle().Subject;
        source.Id.Should().Be("scope-1:wf-published:service");
        source.WorkflowId.Should().Be("wf-published");
        source.SourceUpdatedAtUtc.Should().Be(DateTimeOffset.Parse("2026-08-05T01:00:00Z"));
        source.DeploymentId.Should().Be("dep-1");

        var row = rowWriter.Upserts.Should().ContainSingle().Subject;
        row.Id.Should().Be("scope-1:workflow:wf-published");
        row.HasPublishedSource.Should().BeTrue();
        row.ActiveRevisionId.Should().Be("rev-live");
    }

    [Fact]
    public async Task ServiceSourceProjector_ShouldUsePublishedServiceId_WhenWorkflowPlanHasNoExplicitBindingIdentity()
    {
        var identity = ServiceIdentity("scope-1", "workflow-app", "user", "published-service-1");
        var serviceKey = ServiceKeys.Build(identity);
        var revisionCatalog = WorkflowRevisionCatalog(
            serviceKey,
            "rev-live",
            identity.ServiceId,
            "Published Workflow",
            includeExplicitBindingIdentity: false);
        var deployment = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-1",
            RevisionId = "rev-live",
            PrimaryActorId = "workflow-actor-live",
            Status = ServiceDeploymentStatus.Active,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T01:00:00Z")),
        };
        var deploymentState = new ServiceDeploymentState
        {
            Identity = identity.Clone(),
        };
        deploymentState.Deployments[deployment.DeploymentId] = deployment;
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var projector = new ScopeWorkflowCatalogueServiceSourceProjector(
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([revisionCatalog]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([]),
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, rowWriter),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-05T02:00:00Z")));

        await projector.ProjectAsync(
            new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = "service-deployment:svc-key",
                ProjectionKind = "service-deployments",
            },
            BuildDeploymentEnvelope(
                new ServiceDeploymentActivatedEvent
                {
                    Identity = identity.Clone(),
                    RevisionId = "rev-live",
                    DeploymentId = "dep-1",
                    PrimaryActorId = "workflow-actor-live",
                },
                deploymentState,
                "evt-deployment"));

        var source = sourceWriter.Upserts.Should().ContainSingle().Subject;
        source.WorkflowId.Should().Be("published-service-1");
        source.Id.Should().Be("scope-1:published-service-1:service");

        var row = rowWriter.Upserts.Should().ContainSingle().Subject;
        row.Id.Should().Be("scope-1:workflow:published-service-1");
        row.PublishedServiceId.Should().Be("published-service-1");
    }

    [Fact]
    public void ServiceCapabilityRegistration_ShouldAttachWorkflowCatalogueRevisionMaterializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGAgentServiceCapability(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        var materializers = provider.GetServices<IProjectionMaterializer<ServiceRevisionCatalogProjectionContext>>()
            .Select(static materializer => materializer.GetType().FullName ?? materializer.GetType().Name)
            .ToList();

        materializers.Should().Contain(name => name.Contains(nameof(ScopeWorkflowCatalogueRevisionSourceProjector), StringComparison.Ordinal));
    }

    [Fact]
    public async Task RowMaterializer_ShouldComposeDraftAndServiceSourcesIntoStableAggregateRow()
    {
        var draft = ExistingDraftSource("scope-1", "wf-shared");
        draft.Name = "Draft Display";
        draft.Description = "draft desc";
        draft.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        var serviceSource = ExistingServiceSource("scope-1", "wf-shared", "published-service-1");
        serviceSource.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-05T00:00:00Z");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var materializer = new ScopeWorkflowCatalogueRowMaterializer(
            new RecordingCatalogueSourceReader([draft, serviceSource], sourceWriter),
            rowWriter);

        await materializer.RefreshAsync(
            "scope-1",
            "wf-shared",
            "evt-refresh",
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"));

        var row = rowWriter.Upserts.Should().ContainSingle().Subject;
        row.Id.Should().Be("scope-1:workflow:wf-shared");
        row.ActorId.Should().Be("scope-workflow-catalogue-row:scope-1:wf-shared");
        row.StateVersion.Should().Be(WatermarkStateVersion("2026-08-06T00:00:00Z"));
        row.Name.Should().Be("Draft Display");
        row.Description.Should().Be("draft desc");
        row.HasDraftSource.Should().BeTrue();
        row.HasPublishedSource.Should().BeTrue();
        row.SourceWatermarkUtc.Should().Be(DateTimeOffset.Parse("2026-08-05T00:00:00Z"));
        row.UpdatedAtSource.Should().Be(ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);
        row.PublishedServiceId.Should().Be("published-service-1");
        row.CommittedActorId.Should().Be("workflow-actor-live");

        sourceWriter.DeleteMarkers.Add(new ProjectionDocumentDeleteMarker(
            draft.Id,
            draft.ActorId,
            draft.StateVersion + 1,
            "evt-delete-draft",
            DateTimeOffset.Parse("2026-08-06T01:00:00Z")));
        await materializer.RefreshAsync(
            "scope-1",
            "wf-shared",
            "evt-delete-draft",
            DateTimeOffset.Parse("2026-08-06T01:00:00Z"));

        rowWriter.Upserts.Last().StateVersion.Should().Be(WatermarkStateVersion("2026-08-06T01:00:00Z"));
        rowWriter.Upserts.Last().HasDraftSource.Should().BeFalse();
        rowWriter.DeleteMarkers.Should().BeEmpty();

        sourceWriter.DeleteMarkers.Add(new ProjectionDocumentDeleteMarker(
            serviceSource.Id,
            serviceSource.ActorId,
            serviceSource.StateVersion + 1,
            "evt-delete-service",
            DateTimeOffset.Parse("2026-08-06T02:00:00Z")));
        await materializer.RefreshAsync(
            "scope-1",
            "wf-shared",
            "evt-delete-service",
            DateTimeOffset.Parse("2026-08-06T02:00:00Z"));

        rowWriter.DeleteMarkers.Should().ContainSingle().Which.Should().Be(new ProjectionDocumentDeleteMarker(
            "scope-1:workflow:wf-shared",
            "scope-workflow-catalogue-row:scope-1:wf-shared",
            WatermarkStateVersion("2026-08-06T02:00:00Z") + 1,
            "evt-delete-service",
            DateTimeOffset.Parse("2026-08-06T02:00:00Z")));
    }

    [Fact]
    public async Task RowMaterializer_ShouldRetryWhenRowWriteConflicts()
    {
        var draft = ExistingDraftSource("scope-1", "wf-retry");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher
        {
            RejectedWritesRemaining = 1,
        };
        rowWriter.Upserts.Add(new ScopeWorkflowCatalogueRowDocument
        {
            Id = "scope-1:workflow:wf-retry",
            ActorId = "scope-workflow-catalogue-row:scope-1:wf-retry",
            StateVersion = 4,
            LastEventId = "evt-existing",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            ScopeId = "scope-1",
            WorkflowId = "wf-retry",
            HasDraftSource = true,
            RowUpdatedAtUtc = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            SourceWatermarkUtc = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        });
        var materializer = new ScopeWorkflowCatalogueRowMaterializer(
            new RecordingCatalogueSourceReader([draft], sourceWriter),
            rowWriter);

        await materializer.RefreshAsync(
            "scope-1",
            "wf-retry",
            "evt-refresh",
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"));

        rowWriter.Upserts.Last().StateVersion.Should().Be(WatermarkStateVersion("2026-08-06T00:00:00Z"));
        rowWriter.UpsertAttempts.Should().Be(2);
    }

    [Fact]
    public async Task RowMaterializer_ShouldRetryWhenRowDeleteConflicts()
    {
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher
        {
            RejectedDeletesRemaining = 1,
        };
        rowWriter.Upserts.Add(new ScopeWorkflowCatalogueRowDocument
        {
            Id = "scope-1:workflow:wf-delete-retry",
            ActorId = "scope-workflow-catalogue-row:scope-1:wf-delete-retry",
            StateVersion = 4,
            LastEventId = "evt-existing",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            ScopeId = "scope-1",
            WorkflowId = "wf-delete-retry",
        });
        var materializer = new ScopeWorkflowCatalogueRowMaterializer(
            new RecordingCatalogueSourceReader([], sourceWriter),
            rowWriter);

        await materializer.RefreshAsync(
            "scope-1",
            "wf-delete-retry",
            "evt-refresh",
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"));

        rowWriter.DeleteMarkers.Should().ContainSingle().Which.StateVersion.Should().Be(WatermarkStateVersion("2026-08-06T00:00:00Z") + 1);
        rowWriter.DeleteAttempts.Should().Be(2);
    }

    [Fact]
    public async Task RowMaterializer_ShouldUseSourceAuthorityWatermarkWhenSourceUpdatedAtDiffersFromProjectionObservation()
    {
        var draft = ExistingDraftSource("scope-1", "wf-source-watermark");
        draft.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T01:00:00Z"));
        draft.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-07T00:00:00Z");
        draft.LastEventId = "evt-source-newer";
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var materializer = new ScopeWorkflowCatalogueRowMaterializer(
            new RecordingCatalogueSourceReader([draft], new RecordingCatalogueSourceDispatcher()),
            rowWriter);

        await materializer.RefreshAsync(
            "scope-1",
            "wf-source-watermark",
            "evt-refresh-older",
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"));

        var row = rowWriter.Upserts.Should().ContainSingle().Subject;
        row.StateVersion.Should().Be(WatermarkStateVersion("2026-08-07T00:00:00Z"));
        row.LastEventId.Should().Be("evt-source-newer");
    }

    private static long WatermarkStateVersion(string value) =>
        DateTimeOffset.Parse(value).UtcDateTime.Ticks;

    private static ScopeWorkflowCatalogueBackfillHostedService CreateService(
        IReadOnlyList<ServiceCatalogReadModel> serviceCatalogs,
        IReadOnlyList<ServiceDeploymentCatalogReadModel> deploymentCatalogs,
        IReadOnlyList<ServiceRevisionCatalogReadModel> revisionCatalogs,
        IReadOnlyList<StudioWorkspaceCurrentStateDocument> workspaces,
        IReadOnlyList<ScopeWorkflowCatalogueSourceDocument> existingSources,
        RecordingCatalogueSourceDispatcher sourceWriter,
        RecordingCatalogueRowDispatcher rowWriter,
        RecordingCatalogueSourceReader sourceReader) =>
        new(
            new StubProjectionDocumentReader<ServiceCatalogReadModel>(serviceCatalogs),
            new StubProjectionDocumentReader<ServiceDeploymentCatalogReadModel>(deploymentCatalogs),
            new StubProjectionDocumentReader<ServiceRevisionCatalogReadModel>(revisionCatalogs),
            new StubProjectionDocumentReader<StudioWorkspaceCurrentStateDocument>(workspaces),
            new StubProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument>(existingSources),
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, rowWriter),
            new StubWorkflowYamlDocumentService(),
            NullLogger<ScopeWorkflowCatalogueBackfillHostedService>.Instance);

    private static ServiceRevisionCatalogReadModel WorkflowRevisionCatalog(
        string serviceKey,
        string revisionId,
        string workflowId,
        string workflowName,
        bool includeExplicitBindingIdentity = true) =>
        new()
        {
            Id = serviceKey,
            ActorId = $"service-revisions:{serviceKey}",
            StateVersion = 5,
            LastEventId = $"evt-revision-{revisionId}",
            UpdatedAt = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            Revisions =
            [
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = revisionId,
                    WorkflowName = workflowName,
                    PreparedArtifact = new PreparedServiceRevisionArtifact
                    {
                        RevisionId = revisionId,
                        ImplementationKind = ServiceImplementationKind.Workflow,
                        DeploymentPlan = new ServiceDeploymentPlan
                        {
                            WorkflowPlan = new WorkflowServiceDeploymentPlan
                            {
                                WorkflowName = workflowName,
                                WorkflowId = includeExplicitBindingIdentity ? workflowId : string.Empty,
                                RevisionId = includeExplicitBindingIdentity ? revisionId : string.Empty,
                                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                                CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
                                {
                                    ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                                },
                            },
                        },
                    },
                },
            ],
        };

    private static ServiceRevisionCatalogState ToRevisionCatalogState(
        ServiceIdentity identity,
        ServiceRevisionCatalogReadModel revisionCatalog)
    {
        var state = new ServiceRevisionCatalogState
        {
            Identity = identity.Clone(),
        };

        foreach (var revision in revisionCatalog.Revisions)
        {
            state.Revisions[revision.RevisionId] = new ServiceRevisionRecordState
            {
                Spec = new ServiceRevisionSpec
                {
                    Identity = identity.Clone(),
                    RevisionId = revision.RevisionId,
                    ImplementationKind = ServiceImplementationKind.Workflow,
                    WorkflowSpec = new WorkflowServiceRevisionSpec
                    {
                        WorkflowName = revision.WorkflowName,
                        DefinitionActorId = revision.PreparedArtifact?.DeploymentPlan.WorkflowPlan.DefinitionActorId ?? string.Empty,
                    },
                },
                Status = ServiceRevisionStatus.Published,
                PreparedArtifact = revision.PreparedArtifact?.Clone(),
            };
        }

        return state;
    }

    private static EventEnvelope BuildRevisionEnvelope<T>(
        T evt,
        ServiceRevisionCatalogState state,
        string eventId)
        where T : IMessage =>
        BuildCommittedStateEnvelope(evt, state, eventId);

    private static EventEnvelope BuildDeploymentEnvelope<T>(
        T evt,
        ServiceDeploymentState state,
        string eventId)
        where T : IMessage =>
        BuildCommittedStateEnvelope(evt, state, eventId);

    private static EventEnvelope BuildCommittedStateEnvelope<TEvent, TState>(
        TEvent evt,
        TState state,
        string eventId)
        where TEvent : IMessage
        where TState : IMessage =>
        new()
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T02:05:00Z")),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = 9,
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T02:00:00Z")),
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private static ServiceIdentity ServiceIdentity(
        string tenantId,
        string appId,
        string serviceNamespace,
        string serviceId) =>
        new()
        {
            TenantId = tenantId,
            AppId = appId,
            Namespace = serviceNamespace,
            ServiceId = serviceId,
        };

    private static ServiceRevisionCatalogReadModel MalformedWorkflowRevisionCatalog(
        string serviceKey,
        string revisionId) =>
        new()
        {
            Id = serviceKey,
            ActorId = $"service-revisions:{serviceKey}",
            StateVersion = 5,
            LastEventId = $"evt-revision-{revisionId}",
            UpdatedAt = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            Revisions =
            [
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = revisionId,
                    WorkflowName = "Malformed Workflow",
                    PreparedArtifact = new PreparedServiceRevisionArtifact
                    {
                        RevisionId = revisionId,
                        ImplementationKind = ServiceImplementationKind.Workflow,
                        DeploymentPlan = new ServiceDeploymentPlan
                        {
                            WorkflowPlan = new WorkflowServiceDeploymentPlan
                            {
                                WorkflowName = "Malformed Workflow",
                                RevisionId = revisionId,
                            },
                        },
                    },
                },
            ],
        };

    private static ScopeWorkflowCatalogueSourceDocument ExistingDraftSource(string scopeId, string workflowId) =>
        new()
        {
            Id = $"{scopeId}:{workflowId}:draft",
            ActorId = $"studio-workspace-{scopeId}",
            StateVersion = 1,
            LastEventId = $"evt-{workflowId}",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            ScopeId = scopeId,
            WorkflowId = workflowId,
            SourceKind = ScopeWorkflowCatalogueSourceDocument.DraftSourceKind,
            Name = workflowId,
            SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        };

    private static ScopeWorkflowCatalogueSourceDocument ExistingServiceSource(
        string scopeId,
        string workflowId,
        string publishedServiceId) =>
        new()
        {
            Id = $"{scopeId}:{workflowId}:service",
            ActorId = $"service-deployment-{scopeId}",
            StateVersion = 2,
            LastEventId = $"evt-service-{workflowId}",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-03T00:00:00Z")),
            ScopeId = scopeId,
            WorkflowId = workflowId,
            SourceKind = ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind,
            Name = "Published Workflow",
            SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-03T00:00:00Z"),
            ServiceKey = "svc-key",
            WorkflowName = "Published Workflow",
            CommittedActorId = "workflow-actor-live",
            ActiveRevisionId = "rev-live",
            DeploymentId = "dep-1",
            DeploymentStatus = ServiceDeploymentStatus.Active.ToString(),
            PublishedServiceId = publishedServiceId,
        };

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class KeyedProjectionDocumentReader<TReadModel>(IReadOnlyList<TReadModel> documents)
        : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(documents.FirstOrDefault(document => string.Equals(document.Id, key, StringComparison.Ordinal)));

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
            {
                Items = documents,
                NextCursor = null,
                TotalCount = documents.Count,
            });
    }

    private sealed class StubProjectionDocumentReader<TReadModel>(IReadOnlyList<TReadModel> documents)
        : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(default(TReadModel));

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
            {
                Items = documents,
                NextCursor = null,
                TotalCount = documents.Count,
            });
    }

    private sealed class RecordingCatalogueSourceReader(
        IReadOnlyList<ScopeWorkflowCatalogueSourceDocument> existingSources,
        RecordingCatalogueSourceDispatcher dispatcher)
        : IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string>
    {
        public Task<ScopeWorkflowCatalogueSourceDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            if (dispatcher.DeleteMarkers.Any(marker => string.Equals(marker.Id, key, StringComparison.Ordinal)))
                return Task.FromResult<ScopeWorkflowCatalogueSourceDocument?>(null);

            var source = dispatcher.Upserts.LastOrDefault(document => string.Equals(document.Id, key, StringComparison.Ordinal)) ??
                         existingSources.LastOrDefault(document => string.Equals(document.Id, key, StringComparison.Ordinal));
            return Task.FromResult(source);
        }

        public Task<ProjectionDocumentQueryResult<ScopeWorkflowCatalogueSourceDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<ScopeWorkflowCatalogueSourceDocument>
            {
                Items = existingSources.Concat(dispatcher.Upserts).ToList(),
                NextCursor = null,
                TotalCount = existingSources.Count + dispatcher.Upserts.Count,
            });
    }

    private sealed class RecordingCatalogueSourceDispatcher
        : IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument>
    {
        public List<ScopeWorkflowCatalogueSourceDocument> Upserts { get; } = [];

        public List<ProjectionDocumentDeleteMarker> DeleteMarkers { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ScopeWorkflowCatalogueSourceDocument readModel,
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

    private sealed class RecordingCatalogueRowDispatcher
        : IProjectionWriteDispatcher<ScopeWorkflowCatalogueRowDocument>
    {
        public List<ScopeWorkflowCatalogueRowDocument> Upserts { get; } = [];

        public List<ProjectionDocumentDeleteMarker> DeleteMarkers { get; } = [];

        public int RejectedWritesRemaining { get; init; }

        public int RejectedDeletesRemaining { get; init; }

        public int UpsertAttempts { get; private set; }

        public int DeleteAttempts { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(
            ScopeWorkflowCatalogueRowDocument readModel,
            CancellationToken ct = default)
        {
            UpsertAttempts++;
            if (RejectedWritesRemaining >= UpsertAttempts)
                return Task.FromResult(ProjectionWriteResult.Conflict());

            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());

        public Task<ProjectionWriteResult> DeleteAsync(
            ProjectionDocumentDeleteMarker marker,
            CancellationToken ct = default)
        {
            DeleteAttempts++;
            if (RejectedDeletesRemaining >= DeleteAttempts)
                return Task.FromResult(ProjectionWriteResult.Conflict());

            DeleteMarkers.Add(marker);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml)
        {
            var document = new WorkflowDocument
            {
                Name = ReadScalar(yaml, "name") ?? "workflow",
                Description = ReadScalar(yaml, "description") ?? string.Empty,
            };
            return new WorkflowParseResult(document, []);
        }

        public string Serialize(WorkflowDocument document) =>
            $"name: {document.Name}\ndescription: {document.Description}\nsteps: []\n";

        private static string? ReadScalar(string yaml, string key)
        {
            foreach (var line in yaml.Split('\n'))
            {
                var trimmed = line.Trim();
                var prefix = $"{key}:";
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    return trimmed[prefix.Length..].Trim();
            }

            return null;
        }
    }
}
