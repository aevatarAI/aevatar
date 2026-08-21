using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
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
    public async Task RunBackfillOnceAsync_ShouldBackfillDraftAndServiceSourcesFromWorkflowNativeCurrentStateReadModels()
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

        await service.RunBackfillOnceAsync(CancellationToken.None);

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
        serviceSource.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Active.ToString());
        serviceSource.CommittedActorId.Should().Be("workflow-actor-live");

        var draftSource = sourceWriter.Upserts.Single(static source => source.SourceKind == ScopeWorkflowCatalogueSourceDocument.DraftSourceKind);
        draftSource.Id.Should().Be("scope-1:wf-draft:draft");
        draftSource.ActorId.Should().Be("scope-workflow-catalogue-source:scope-1:wf-draft:draft");
        draftSource.StateVersion.Should().Be(WatermarkStateVersion("2026-08-02T00:00:00Z"));
        draftSource.Name.Should().Be("Draft From Yaml");
        draftSource.Description.Should().Be("draft desc");

        rowWriter.Commands.Select(static command => command.WorkflowId)
            .Should().Contain(["wf-published", "wf-draft"]);
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldUsePublishedServiceId_WhenWorkflowPlanHasNoExplicitBindingIdentity()
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
        var revisionCatalog = WorkflowRevisionCatalog(
            "svc-key",
            "rev-live",
            serviceCatalog.ServiceId,
            "Published Workflow",
            includeExplicitBindingIdentity: false);
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var service = CreateService(
            [serviceCatalog],
            [deploymentCatalog],
            [revisionCatalog],
            [],
            [],
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.RunBackfillOnceAsync(CancellationToken.None);

        var serviceSource = sourceWriter.Upserts.Should().ContainSingle().Subject;
        serviceSource.Id.Should().Be("scope-1:published-service-1:service");
        serviceSource.WorkflowId.Should().Be("published-service-1");
        serviceSource.PublishedServiceId.Should().Be("published-service-1");
        serviceSource.ActiveRevisionId.Should().Be("rev-live");

        var command = rowWriter.Commands.Should().ContainSingle().Subject;
        command.WorkflowId.Should().Be("published-service-1");
        command.ServiceSource.Should().NotBeNull();
        command.ServiceSource!.PublishedServiceId.Should().Be("published-service-1");
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldBackfillServiceSourceFromLatestDeactivatedDeployment_WhenNoActiveDeployment()
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
                    DeploymentId = "dep-old-archived",
                    RevisionId = "rev-old-archived",
                    PrimaryActorId = "workflow-actor-old",
                    Status = ServiceDeploymentStatus.Deactivated.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-05T01:00:00Z"),
                },
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-latest-archived",
                    RevisionId = "rev-archived",
                    PrimaryActorId = "workflow-actor-archived",
                    Status = ServiceDeploymentStatus.Deactivated.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-05T02:00:00Z"),
                },
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-failed-newer",
                    RevisionId = "rev-failed",
                    PrimaryActorId = "workflow-actor-failed",
                    Status = ServiceDeploymentStatus.Failed.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-05T03:00:00Z"),
                },
            ],
        };
        var revisionCatalog = WorkflowRevisionCatalog("svc-key", "rev-archived", "wf-archived", "Archived Workflow");
        var existingArchivedSource = ExistingServiceSource("scope-1", "wf-archived", "published-service-1");
        existingArchivedSource.DeploymentStatus = ServiceDeploymentStatus.Deactivated.ToString();
        var staleServiceSource = ExistingServiceSource("scope-1", "wf-stale", "published-service-1");
        var existingSources = new[] { existingArchivedSource, staleServiceSource };
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader(existingSources, sourceWriter);
        var service = CreateService(
            [serviceCatalog],
            [deploymentCatalog],
            [revisionCatalog],
            [],
            existingSources,
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.RunBackfillOnceAsync(CancellationToken.None);

        var serviceSource = sourceWriter.Upserts.Should().ContainSingle().Subject;
        serviceSource.Id.Should().Be("scope-1:wf-archived:service");
        serviceSource.WorkflowId.Should().Be("wf-archived");
        serviceSource.DeploymentId.Should().Be("dep-latest-archived");
        serviceSource.ActiveRevisionId.Should().Be("rev-archived");
        serviceSource.CommittedActorId.Should().Be("workflow-actor-archived");
        serviceSource.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Deactivated.ToString());
        serviceSource.PublishedServiceId.Should().Be("published-service-1");
        sourceWriter.DeleteMarkers.Select(static marker => marker.Id)
            .Should().Contain("scope-1:wf-stale:service")
            .And.NotContain("scope-1:wf-archived:service");
        rowWriter.Commands.Should().Contain(command =>
            command.WorkflowId == "wf-archived" &&
            command.ServiceSource != null &&
            command.ServiceSource.DeploymentStatus == ServiceDeploymentStatus.Deactivated.ToString());
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldDeleteStaleDraftSourcesForParsedWorkspaceScope()
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

        await service.RunBackfillOnceAsync(CancellationToken.None);

        sourceWriter.Upserts.Should().ContainSingle(source => source.Id == "scope-1:wf-current:draft");
        sourceWriter.DeleteMarkers.Should().ContainSingle().Which.Should().Be(new ProjectionDocumentDeleteMarker(
            "scope-1:wf-stale:draft",
            "studio-workspace-scope-1",
            WatermarkStateVersion("2026-08-02T01:00:00Z") + 1,
            "evt-workspace-current",
            DateTimeOffset.Parse("2026-08-02T01:00:00Z")));
        rowWriter.Commands.Should().ContainSingle(command => command.WorkflowId == "wf-stale" &&
                                                              command.DraftSource == null &&
                                                              command.ServiceSource == null);
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldDeleteStaleServiceSourcesByWorkflowKey()
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

        await service.RunBackfillOnceAsync(CancellationToken.None);

        sourceWriter.DeleteMarkers.Should().ContainSingle().Which.Id.Should().Be("scope-1:wf-old:service");
        rowWriter.Commands.Should().ContainSingle(command => command.WorkflowId == "wf-old" &&
                                                              command.DraftSource == null &&
                                                              command.ServiceSource == null);
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldUseDefaultServingRevision_WhenOlderDeploymentWasDeactivatedLater()
    {
        var serviceCatalog = new ServiceCatalogReadModel
        {
            Id = "svc-key",
            ActorId = "service-definition:scope-1:published-service-1",
            StateVersion = 4,
            LastEventId = "evt-service-catalog",
            TenantId = "scope-1",
            AppId = "workflow-app",
            Namespace = "user",
            ServiceId = "published-service-1",
            DisplayName = "Published Service",
            DefaultServingRevisionId = "rev-active",
            UpdatedAt = DateTimeOffset.Parse("2026-08-06T00:00:00Z"),
        };
        var deploymentCatalog = new ServiceDeploymentCatalogReadModel
        {
            Id = "svc-key",
            ActorId = "service-deployment:scope-1:published-service-1",
            StateVersion = 9,
            LastEventId = "evt-deployment",
            UpdatedAt = DateTimeOffset.Parse("2026-08-07T00:00:00Z"),
            Deployments =
            [
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-old",
                    RevisionId = "rev-old",
                    PrimaryActorId = "workflow-actor-old",
                    Status = ServiceDeploymentStatus.Deactivated.ToString(),
                    ActivatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-07T01:00:00Z"),
                },
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-active",
                    RevisionId = "rev-active",
                    PrimaryActorId = "workflow-actor-active",
                    Status = ServiceDeploymentStatus.Active.ToString(),
                    ActivatedAt = DateTimeOffset.Parse("2026-08-06T00:00:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-06T01:00:00Z"),
                },
            ],
        };
        var revisionCatalog = WorkflowRevisionCatalog("svc-key", "rev-active", "wf-active", "Active Workflow");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var service = CreateService(
            [serviceCatalog],
            [deploymentCatalog],
            [revisionCatalog],
            [],
            [],
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.RunBackfillOnceAsync(CancellationToken.None);

        var serviceSource = sourceWriter.Upserts.Should().ContainSingle().Subject;
        serviceSource.WorkflowId.Should().Be("wf-active");
        serviceSource.ActiveRevisionId.Should().Be("rev-active");
        serviceSource.DeploymentId.Should().Be("dep-active");
        serviceSource.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Active.ToString());
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldBackfillDeactivatedServiceSourcesFromWorkflowNativeCurrentStateReadModels()
    {
        var serviceCatalog = new ServiceCatalogReadModel
        {
            Id = "svc-key",
            ActorId = "service-definition:scope-1:published-service-1",
            StateVersion = 4,
            LastEventId = "evt-service-catalog",
            TenantId = "scope-1",
            AppId = "workflow-app",
            Namespace = "user",
            ServiceId = "published-service-1",
            DisplayName = "Published Service",
            UpdatedAt = DateTimeOffset.Parse("2026-08-06T00:00:00Z"),
        };
        var deploymentCatalog = new ServiceDeploymentCatalogReadModel
        {
            Id = "svc-key",
            ActorId = "service-deployment:scope-1:published-service-1",
            StateVersion = 9,
            LastEventId = "evt-deployment",
            UpdatedAt = DateTimeOffset.Parse("2026-08-07T00:00:00Z"),
            Deployments =
            [
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-1",
                    RevisionId = "rev-live",
                    PrimaryActorId = "workflow-actor-live",
                    Status = ServiceDeploymentStatus.Deactivated.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-07T01:00:00Z"),
                },
            ],
        };
        var revisionCatalog = WorkflowRevisionCatalog("svc-key", "rev-live", "wf-archived", "Archived Workflow");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var service = CreateService(
            [serviceCatalog],
            [deploymentCatalog],
            [revisionCatalog],
            [],
            [],
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.RunBackfillOnceAsync(CancellationToken.None);

        var serviceSource = sourceWriter.Upserts.Should().ContainSingle().Subject;
        serviceSource.WorkflowId.Should().Be("wf-archived");
        serviceSource.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Deactivated.ToString());
        rowWriter.Commands.Should().ContainSingle(command => command.WorkflowId == "wf-archived" &&
                                                              command.ServiceSource != null &&
                                                              command.ServiceSource.DeploymentStatus == ServiceDeploymentStatus.Deactivated.ToString());
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldPreserveExistingActiveServiceSource_WhenDeactivatedSourceForDifferentServiceArrivesLater()
    {
        var existingActiveSource = ExistingServiceSource("scope-1", "wf-shared", "published-service-a");
        existingActiveSource.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-03T00:00:00Z");
        var serviceCatalog = new ServiceCatalogReadModel
        {
            Id = "svc-key-b",
            ActorId = "service-definition:scope-1:published-service-b",
            StateVersion = 4,
            LastEventId = "evt-service-catalog-b",
            TenantId = "scope-1",
            AppId = "workflow-app",
            Namespace = "user",
            ServiceId = "published-service-b",
            DisplayName = "Published Service B",
            UpdatedAt = DateTimeOffset.Parse("2026-08-06T00:00:00Z"),
        };
        var deploymentCatalog = new ServiceDeploymentCatalogReadModel
        {
            Id = "svc-key-b",
            ActorId = "service-deployment:scope-1:published-service-b",
            StateVersion = 9,
            LastEventId = "evt-deployment-b",
            UpdatedAt = DateTimeOffset.Parse("2026-08-06T01:00:00Z"),
            Deployments =
            [
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-deactivated-b",
                    RevisionId = "rev-shared",
                    PrimaryActorId = "workflow-actor-b",
                    Status = ServiceDeploymentStatus.Deactivated.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-06T02:00:00Z"),
                },
            ],
        };
        var revisionCatalog = WorkflowRevisionCatalog("svc-key-b", "rev-shared", "wf-shared", "Shared Workflow");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([existingActiveSource], sourceWriter);
        var service = CreateService(
            [serviceCatalog],
            [deploymentCatalog],
            [revisionCatalog],
            [],
            [existingActiveSource],
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.RunBackfillOnceAsync(CancellationToken.None);

        sourceWriter.Upserts.Should().BeEmpty();
        sourceWriter.DeleteMarkers.Should().BeEmpty();
        rowWriter.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldBumpActiveServiceSourceVersion_WhenRestoringOverCrossServiceDeactivatedSource()
    {
        var existingDeactivatedSource = ExistingServiceSource("scope-1", "wf-shared", "published-service-b");
        existingDeactivatedSource.DeploymentStatus = ServiceDeploymentStatus.Deactivated.ToString();
        existingDeactivatedSource.StateVersion = WatermarkStateVersion("2026-08-08T00:00:00Z");
        existingDeactivatedSource.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-08T00:00:00Z");
        var serviceCatalog = new ServiceCatalogReadModel
        {
            Id = "svc-key-a",
            ActorId = "service-definition:scope-1:published-service-a",
            StateVersion = 4,
            LastEventId = "evt-service-catalog-a",
            TenantId = "scope-1",
            AppId = "workflow-app",
            Namespace = "user",
            ServiceId = "published-service-a",
            DisplayName = "Published Service A",
            UpdatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
        };
        var deploymentCatalog = new ServiceDeploymentCatalogReadModel
        {
            Id = "svc-key-a",
            ActorId = "service-deployment:scope-1:published-service-a",
            StateVersion = 9,
            LastEventId = "evt-deployment-a",
            UpdatedAt = DateTimeOffset.Parse("2026-08-05T01:00:00Z"),
            Deployments =
            [
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-active-a",
                    RevisionId = "rev-shared",
                    PrimaryActorId = "workflow-actor-a",
                    Status = ServiceDeploymentStatus.Active.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-05T02:00:00Z"),
                },
            ],
        };
        var revisionCatalog = WorkflowRevisionCatalog("svc-key-a", "rev-shared", "wf-shared", "Shared Workflow");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([existingDeactivatedSource], sourceWriter);
        var service = CreateService(
            [serviceCatalog],
            [deploymentCatalog],
            [revisionCatalog],
            [],
            [existingDeactivatedSource],
            sourceWriter,
            rowWriter,
            sourceReader);

        await service.RunBackfillOnceAsync(CancellationToken.None);

        var source = sourceWriter.Upserts.Should().ContainSingle().Subject;
        source.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Active.ToString());
        source.PublishedServiceId.Should().Be("published-service-a");
        source.StateVersion.Should().Be(existingDeactivatedSource.StateVersion + 1);
        source.StateVersion.Should().BeGreaterThan(WatermarkStateVersion("2026-08-05T02:00:00Z"));
        rowWriter.Commands.Should().ContainSingle(command =>
            command.WorkflowId == "wf-shared" &&
            command.ServiceSource != null &&
            command.ServiceSource.PublishedServiceId == "published-service-a");
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldSkipStaleServiceSourceDelete_WhenLatestActiveSourceIsOwnedByAnotherService()
    {
        var staleSnapshot = ExistingServiceSource("scope-1", "wf-shared", "published-service-b");
        staleSnapshot.DeploymentStatus = ServiceDeploymentStatus.Deactivated.ToString();
        staleSnapshot.StateVersion = WatermarkStateVersion("2026-08-04T00:00:00Z");
        var latestActiveSource = ExistingServiceSource("scope-1", "wf-shared", "published-service-a");
        latestActiveSource.StateVersion = WatermarkStateVersion("2026-08-07T00:00:00Z");
        latestActiveSource.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-07T00:00:00Z");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([latestActiveSource], sourceWriter);
        var service = CreateService(
            [],
            [],
            [],
            [],
            [staleSnapshot],
            sourceWriter,
            rowWriter,
            sourceReader,
            new StubProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument>(
                [staleSnapshot],
                [latestActiveSource]));

        await service.RunBackfillOnceAsync(CancellationToken.None);

        sourceWriter.DeleteMarkers.Should().BeEmpty();
        rowWriter.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldSkipMalformedWorkflowServiceRevisionWithoutFailingBackfill()
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

        await service.RunBackfillOnceAsync(CancellationToken.None);

        sourceWriter.Upserts.Should().BeEmpty();
        rowWriter.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RunBackfillOnceAsync_ShouldNotFail_WhenRowRefreshThrows()
    {
        // Production regression (2026-08-13): during a rolling upgrade the
        // first new-image pod backfilled rows whose actors only resolve on
        // the new image; the old-image silo threw UnknownAgentKindException
        // (which has no Orleans codec) and the unhandled StartAsync failure
        // crash-looped the rollout. The backfill must never abort boot.
        var staleServiceSource = ExistingServiceSource("scope-1", "wf-old", "published-service-1");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new ThrowingCatalogueRowDispatcher();
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

        var start = async () => await service.RunBackfillOnceAsync(CancellationToken.None);

        await start.Should().NotThrowAsync();
        rowWriter.Attempts.Should().BeGreaterThan(0, "the backfill must have tried the row before skipping it");
    }

    [Fact]
    public async Task StartAsync_ShouldNotWaitForBackfillCompletion()
    {
        var blockingReader = new BlockingProjectionDocumentReader<ServiceCatalogReadModel>();
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var service = new ScopeWorkflowCatalogueBackfillHostedService(
            blockingReader,
            new StubProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([]),
            new StubProjectionDocumentReader<ServiceRevisionCatalogReadModel>([]),
            new StubProjectionDocumentReader<StudioWorkspaceCurrentStateDocument>([]),
            new StubProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument>([]),
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, new RecordingCatalogueRowDispatcher()),
            new StubWorkflowYamlDocumentService(),
            NullLogger<ScopeWorkflowCatalogueBackfillHostedService>.Instance);

        var startTask = service.StartAsync(CancellationToken.None);

        startTask.IsCompletedSuccessfully.Should().BeTrue();
        await blockingReader.QueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
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
            new KeyedProjectionDocumentReader<ServiceCatalogReadModel>([]),
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([deploymentCatalog]),
            sourceReader,
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

        var command = rowWriter.Commands.Should().ContainSingle().Subject;
        command.ScopeId.Should().Be("scope-1");
        command.WorkflowId.Should().Be("wf-published");
        command.ServiceSource.Should().NotBeNull();
        command.ServiceSource!.ActiveRevisionId.Should().Be("rev-live");
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
            new KeyedProjectionDocumentReader<ServiceCatalogReadModel>([]),
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([revisionCatalog]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([]),
            sourceReader,
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

        var command = rowWriter.Commands.Should().ContainSingle().Subject;
        command.WorkflowId.Should().Be("published-service-1");
        command.ServiceSource.Should().NotBeNull();
        command.ServiceSource!.PublishedServiceId.Should().Be("published-service-1");
    }

    [Fact]
    public async Task ServiceSourceProjector_ShouldUseDefaultServingRevision_WhenOlderDeploymentWasDeactivatedLater()
    {
        var identity = ServiceIdentity("scope-1", "workflow-app", "user", "published-service-1");
        var serviceKey = ServiceKeys.Build(identity);
        var serviceCatalog = new ServiceCatalogReadModel
        {
            Id = serviceKey,
            ActorId = "service-definition:scope-1:published-service-1",
            StateVersion = 4,
            LastEventId = "evt-service-catalog",
            TenantId = identity.TenantId,
            AppId = identity.AppId,
            Namespace = identity.Namespace,
            ServiceId = identity.ServiceId,
            DisplayName = "Published Service",
            DefaultServingRevisionId = "rev-active",
            UpdatedAt = DateTimeOffset.Parse("2026-08-06T00:00:00Z"),
        };
        var revisionCatalog = WorkflowRevisionCatalog(serviceKey, "rev-active", "wf-active", "Active Workflow");
        var deploymentState = new ServiceDeploymentState
        {
            Identity = identity.Clone(),
        };
        deploymentState.Deployments["dep-old"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-old",
            RevisionId = "rev-old",
            PrimaryActorId = "workflow-actor-old",
            Status = ServiceDeploymentStatus.Deactivated,
            ActivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T00:00:00Z")),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-07T01:00:00Z")),
        };
        deploymentState.Deployments["dep-active"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-active",
            RevisionId = "rev-active",
            PrimaryActorId = "workflow-actor-active",
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T00:00:00Z")),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T01:00:00Z")),
        };
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([], sourceWriter);
        var projector = new ScopeWorkflowCatalogueServiceSourceProjector(
            new KeyedProjectionDocumentReader<ServiceCatalogReadModel>([serviceCatalog]),
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([revisionCatalog]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([]),
            sourceReader,
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, rowWriter),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-07T02:00:00Z")));

        await projector.ProjectAsync(
            new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = "service-deployment:svc-key",
                ProjectionKind = "service-deployments",
            },
            BuildDeploymentEnvelope(
                new ServiceDeploymentDeactivatedEvent
                {
                    Identity = identity.Clone(),
                    RevisionId = "rev-old",
                    DeploymentId = "dep-old",
                    DeactivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-07T01:00:00Z")),
                },
                deploymentState,
                "evt-deployment"));

        var source = sourceWriter.Upserts.Should().ContainSingle().Subject;
        source.WorkflowId.Should().Be("wf-active");
        source.ActiveRevisionId.Should().Be("rev-active");
        source.DeploymentId.Should().Be("dep-active");
        source.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Active.ToString());

        var command = rowWriter.Commands.Should().ContainSingle().Subject;
        command.WorkflowId.Should().Be("wf-active");
        command.ServiceSource.Should().NotBeNull();
        command.ServiceSource!.DeploymentId.Should().Be("dep-active");
    }

    [Fact]
    public async Task ServiceSourceProjector_ShouldMaterializeLatestDeactivatedDeployment_WhenNoActiveDeployment()
    {
        var identity = ServiceIdentity("scope-1", "workflow-app", "user", "published-service-1");
        var serviceKey = ServiceKeys.Build(identity);
        var revisionCatalog = WorkflowRevisionCatalog(serviceKey, "rev-archived", "wf-archived", "Archived Workflow");
        revisionCatalog.Revisions.Add(WorkflowRevisionCatalog(serviceKey, "rev-other", "wf-other", "Other Workflow").Revisions[0]);
        var oldDeactivated = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-old-archived",
            RevisionId = "rev-old-archived",
            PrimaryActorId = "workflow-actor-old",
            Status = ServiceDeploymentStatus.Deactivated,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T01:00:00Z")),
        };
        var latestDeactivated = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-latest-archived",
            RevisionId = "rev-archived",
            PrimaryActorId = "workflow-actor-archived",
            Status = ServiceDeploymentStatus.Deactivated,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T02:00:00Z")),
        };
        var failedNewer = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-failed-newer",
            RevisionId = "rev-other",
            PrimaryActorId = "workflow-actor-failed",
            Status = ServiceDeploymentStatus.Failed,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T03:00:00Z")),
        };
        var deploymentState = new ServiceDeploymentState
        {
            Identity = identity.Clone(),
        };
        deploymentState.Deployments[oldDeactivated.DeploymentId] = oldDeactivated;
        deploymentState.Deployments[latestDeactivated.DeploymentId] = latestDeactivated;
        deploymentState.Deployments[failedNewer.DeploymentId] = failedNewer;
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader(
            [ExistingServiceSource("scope-1", "wf-other", "published-service-1")],
            sourceWriter);
        var projector = new ScopeWorkflowCatalogueServiceSourceProjector(
            new KeyedProjectionDocumentReader<ServiceCatalogReadModel>([]),
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([revisionCatalog]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([]),
            sourceReader,
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, rowWriter),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-05T04:00:00Z")));

        await projector.ProjectAsync(
            new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = "service-deployment:svc-key",
                ProjectionKind = "service-deployments",
            },
            BuildDeploymentEnvelope(
                new ServiceDeploymentDeactivatedEvent
                {
                    Identity = identity.Clone(),
                    DeploymentId = "dep-latest-archived",
                    RevisionId = "rev-archived",
                    DeactivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T02:00:00Z")),
                },
                deploymentState,
                "evt-deployment-archived"));

        var source = sourceWriter.Upserts.Should().ContainSingle().Subject;
        source.Id.Should().Be("scope-1:wf-archived:service");
        source.WorkflowId.Should().Be("wf-archived");
        source.DeploymentId.Should().Be("dep-latest-archived");
        source.ActiveRevisionId.Should().Be("rev-archived");
        source.CommittedActorId.Should().Be("workflow-actor-archived");
        source.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Deactivated.ToString());
        source.PublishedServiceId.Should().Be("published-service-1");
        sourceWriter.DeleteMarkers.Select(static marker => marker.Id)
            .Should().Contain("scope-1:wf-other:service")
            .And.NotContain("scope-1:wf-archived:service");
        rowWriter.Commands.Should().Contain(command =>
            command.WorkflowId == "wf-archived" &&
            command.ServiceSource != null &&
            command.ServiceSource.DeploymentStatus == ServiceDeploymentStatus.Deactivated.ToString());
    }

    [Fact]
    public async Task ServiceSourceProjector_ShouldPreserveExistingActiveServiceSource_WhenDeactivatedSourceForDifferentServiceArrivesLater()
    {
        var identity = ServiceIdentity("scope-1", "workflow-app", "user", "published-service-b");
        var serviceKey = ServiceKeys.Build(identity);
        var revisionCatalog = WorkflowRevisionCatalog(serviceKey, "rev-shared", "wf-shared", "Shared Workflow");
        var deploymentState = new ServiceDeploymentState
        {
            Identity = identity.Clone(),
        };
        deploymentState.Deployments["dep-deactivated-b"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-deactivated-b",
            RevisionId = "rev-shared",
            PrimaryActorId = "workflow-actor-b",
            Status = ServiceDeploymentStatus.Deactivated,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T02:00:00Z")),
        };
        var existingActiveSource = ExistingServiceSource("scope-1", "wf-shared", "published-service-a");
        existingActiveSource.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-03T00:00:00Z");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([existingActiveSource], sourceWriter);
        var projector = new ScopeWorkflowCatalogueServiceSourceProjector(
            new KeyedProjectionDocumentReader<ServiceCatalogReadModel>([]),
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([revisionCatalog]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([]),
            sourceReader,
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, rowWriter),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-06T03:00:00Z")));

        await projector.ProjectAsync(
            new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = "service-deployment:svc-key-b",
                ProjectionKind = "service-deployments",
            },
            BuildDeploymentEnvelope(
                new ServiceDeploymentDeactivatedEvent
                {
                    Identity = identity.Clone(),
                    DeploymentId = "dep-deactivated-b",
                    RevisionId = "rev-shared",
                    DeactivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T02:00:00Z")),
                },
                deploymentState,
                "evt-deployment-b"));

        sourceWriter.Upserts.Should().BeEmpty();
        sourceWriter.DeleteMarkers.Should().BeEmpty();
        rowWriter.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceSourceProjector_ShouldBumpActiveSourceVersion_WhenRestoringOverCrossServiceDeactivatedSource()
    {
        var identity = ServiceIdentity("scope-1", "workflow-app", "user", "published-service-a");
        var serviceKey = ServiceKeys.Build(identity);
        var revisionCatalog = WorkflowRevisionCatalog(serviceKey, "rev-shared", "wf-shared", "Shared Workflow");
        var deploymentState = new ServiceDeploymentState
        {
            Identity = identity.Clone(),
        };
        deploymentState.Deployments["dep-active-a"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-active-a",
            RevisionId = "rev-shared",
            PrimaryActorId = "workflow-actor-a",
            Status = ServiceDeploymentStatus.Active,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-05T02:00:00Z")),
        };
        var existingDeactivatedSource = ExistingServiceSource("scope-1", "wf-shared", "published-service-b");
        existingDeactivatedSource.DeploymentStatus = ServiceDeploymentStatus.Deactivated.ToString();
        existingDeactivatedSource.StateVersion = WatermarkStateVersion("2026-08-08T00:00:00Z");
        existingDeactivatedSource.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-08T00:00:00Z");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([existingDeactivatedSource], sourceWriter);
        var projector = new ScopeWorkflowCatalogueServiceSourceProjector(
            new KeyedProjectionDocumentReader<ServiceCatalogReadModel>([]),
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([revisionCatalog]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([]),
            sourceReader,
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, rowWriter),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-09T00:00:00Z")));

        await projector.ProjectAsync(
            new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = "service-deployment:svc-key-a",
                ProjectionKind = "service-deployments",
            },
            BuildDeploymentEnvelope(
                new ServiceDeploymentActivatedEvent
                {
                    Identity = identity.Clone(),
                    DeploymentId = "dep-active-a",
                    RevisionId = "rev-shared",
                    PrimaryActorId = "workflow-actor-a",
                },
                deploymentState,
                "evt-deployment-a"));

        var source = sourceWriter.Upserts.Should().ContainSingle().Subject;
        source.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Active.ToString());
        source.PublishedServiceId.Should().Be("published-service-a");
        source.StateVersion.Should().Be(existingDeactivatedSource.StateVersion + 1);
        source.StateVersion.Should().BeGreaterThan(WatermarkStateVersion("2026-08-05T02:00:00Z"));
        rowWriter.Commands.Should().ContainSingle(command =>
            command.WorkflowId == "wf-shared" &&
            command.ServiceSource != null &&
            command.ServiceSource.PublishedServiceId == "published-service-a");
    }

    [Fact]
    public async Task ServiceSourceProjector_ShouldSkipNonVisibleCleanup_WhenExistingActiveSourceIsOwnedByAnotherService()
    {
        var identity = ServiceIdentity("scope-1", "workflow-app", "user", "published-service-b");
        var serviceKey = ServiceKeys.Build(identity);
        var revisionCatalog = WorkflowRevisionCatalog(serviceKey, "rev-current", "wf-current", "Current Workflow");
        revisionCatalog.Revisions.Add(WorkflowRevisionCatalog(serviceKey, "rev-shared", "wf-shared", "Shared Workflow").Revisions[0]);
        var deploymentState = new ServiceDeploymentState
        {
            Identity = identity.Clone(),
        };
        deploymentState.Deployments["dep-current-b"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-current-b",
            RevisionId = "rev-current",
            PrimaryActorId = "workflow-actor-b",
            Status = ServiceDeploymentStatus.Active,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-06T02:00:00Z")),
        };
        var existingActiveSource = ExistingServiceSource("scope-1", "wf-shared", "published-service-a");
        existingActiveSource.StateVersion = WatermarkStateVersion("2026-08-07T00:00:00Z");
        existingActiveSource.SourceUpdatedAtUtc = DateTimeOffset.Parse("2026-08-07T00:00:00Z");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var sourceReader = new RecordingCatalogueSourceReader([existingActiveSource], sourceWriter);
        var projector = new ScopeWorkflowCatalogueServiceSourceProjector(
            new KeyedProjectionDocumentReader<ServiceCatalogReadModel>([]),
            new KeyedProjectionDocumentReader<ServiceRevisionCatalogReadModel>([revisionCatalog]),
            new KeyedProjectionDocumentReader<ServiceDeploymentCatalogReadModel>([]),
            sourceReader,
            sourceWriter,
            new ScopeWorkflowCatalogueRowMaterializer(sourceReader, rowWriter),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-06T03:00:00Z")));

        await projector.ProjectAsync(
            new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = "service-deployment:svc-key-b",
                ProjectionKind = "service-deployments",
            },
            BuildDeploymentEnvelope(
                new ServiceDeploymentActivatedEvent
                {
                    Identity = identity.Clone(),
                    DeploymentId = "dep-current-b",
                    RevisionId = "rev-current",
                    PrimaryActorId = "workflow-actor-b",
                },
                deploymentState,
                "evt-deployment-b"));

        sourceWriter.Upserts.Should().ContainSingle(source => source.Id == "scope-1:wf-current:service");
        sourceWriter.DeleteMarkers.Should().BeEmpty();
        rowWriter.Commands.Should().ContainSingle(command => command.WorkflowId == "wf-current");
    }

    [Fact]
    public void ServiceCapabilityRegistration_ShouldAttachWorkflowCatalogueRevisionMaterializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime, StubActorRuntime>();
        services.AddSingleton<IActorDispatchPort, StubActorDispatchPort>();
        services.AddGAgentServiceCapability(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        var materializers = provider.GetServices<IProjectionMaterializer<ServiceRevisionCatalogProjectionContext>>()
            .Select(static materializer => materializer.GetType().FullName ?? materializer.GetType().Name)
            .ToList();

        materializers.Should().Contain(name => name.Contains(nameof(ScopeWorkflowCatalogueRevisionSourceProjector), StringComparison.Ordinal));
    }

    [Fact]
    public async Task RowMaterializer_ShouldDispatchObservedDraftAndServiceSourcesToRowActor()
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

        var command = rowWriter.Commands.Should().ContainSingle().Subject;
        command.ScopeId.Should().Be("scope-1");
        command.WorkflowId.Should().Be("wf-shared");
        command.DraftSource.Should().NotBeNull();
        command.DraftSource!.Name.Should().Be("Draft Display");
        command.DraftSource.Description.Should().Be("draft desc");
        command.ServiceSource.Should().NotBeNull();
        command.ServiceSource!.SourceUpdatedAtUtc.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-08-05T00:00:00Z"));
        command.ServiceSource.PublishedServiceId.Should().Be("published-service-1");
        command.ServiceSource.CommittedActorId.Should().Be("workflow-actor-live");

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

        rowWriter.Commands.Should().HaveCount(2);
        rowWriter.Commands[1].DraftSource.Should().BeNull();
        rowWriter.Commands[1].ServiceSource.Should().NotBeNull();

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

        rowWriter.Commands.Should().HaveCount(3);
        rowWriter.Commands[2].DraftSource.Should().BeNull();
        rowWriter.Commands[2].ServiceSource.Should().BeNull();
        rowWriter.Commands[2].ObservationEventId.Should().Be("evt-delete-service");
    }

    [Fact]
    public async Task RowMaterializer_ShouldDispatchSingleActorObservation_WhenSourceSnapshotExists()
    {
        var draft = ExistingDraftSource("scope-1", "wf-retry");
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var materializer = new ScopeWorkflowCatalogueRowMaterializer(
            new RecordingCatalogueSourceReader([draft], sourceWriter),
            rowWriter);

        await materializer.RefreshAsync(
            "scope-1",
            "wf-retry",
            "evt-refresh",
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"));

        rowWriter.Commands.Should().ContainSingle();
        rowWriter.Commands[0].DraftSource.Should().NotBeNull();
        rowWriter.Commands[0].DraftSource!.Name.Should().Be("wf-retry");
    }

    [Fact]
    public async Task RowMaterializer_ShouldDispatchEmptyActorObservation_WhenSourcesAreMissing()
    {
        var sourceWriter = new RecordingCatalogueSourceDispatcher();
        var rowWriter = new RecordingCatalogueRowDispatcher();
        var materializer = new ScopeWorkflowCatalogueRowMaterializer(
            new RecordingCatalogueSourceReader([], sourceWriter),
            rowWriter);

        await materializer.RefreshAsync(
            "scope-1",
            "wf-delete-retry",
            "evt-refresh",
            DateTimeOffset.Parse("2026-08-06T00:00:00Z"));

        rowWriter.Commands.Should().ContainSingle();
        rowWriter.Commands[0].DraftSource.Should().BeNull();
        rowWriter.Commands[0].ServiceSource.Should().BeNull();
        rowWriter.Commands[0].ObservationEventId.Should().Be("evt-refresh");
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

        var command = rowWriter.Commands.Should().ContainSingle().Subject;
        command.ObservationEventId.Should().Be("evt-refresh-older");
        command.DraftSource.Should().NotBeNull();
        command.DraftSource!.SourceUpdatedAtUtc.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-08-07T00:00:00Z"));
        command.DraftSource.LastEventId.Should().Be("evt-source-newer");
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
        IScopeWorkflowCatalogueRowCommandPort rowWriter,
        RecordingCatalogueSourceReader sourceReader,
        IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string>? catalogueSourceReader = null) =>
        new(
            new StubProjectionDocumentReader<ServiceCatalogReadModel>(serviceCatalogs),
            new StubProjectionDocumentReader<ServiceDeploymentCatalogReadModel>(deploymentCatalogs),
            new StubProjectionDocumentReader<ServiceRevisionCatalogReadModel>(revisionCatalogs),
            new StubProjectionDocumentReader<StudioWorkspaceCurrentStateDocument>(workspaces),
            catalogueSourceReader ?? new StubProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument>(existingSources),
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
                                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
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
                        ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
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
        string publishedServiceId,
        string? deploymentStatus = null) =>
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
            DeploymentStatus = deploymentStatus ?? ServiceDeploymentStatus.Active.ToString(),
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

    private sealed class StubProjectionDocumentReader<TReadModel>(
        IReadOnlyList<TReadModel> documents,
        IReadOnlyList<TReadModel>? getDocuments = null)
        : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult((getDocuments ?? documents).LastOrDefault(document =>
                string.Equals(document.Id, key, StringComparison.Ordinal)));

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

    private sealed class BlockingProjectionDocumentReader<TReadModel>
        : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        private readonly TaskCompletionSource<ProjectionDocumentQueryResult<TReadModel>> _pending = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource QueryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(default(TReadModel));

        public async Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            QueryStarted.TrySetResult();
            return await _pending.Task.WaitAsync(ct);
        }
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

    private sealed class ThrowingCatalogueRowDispatcher
        : IScopeWorkflowCatalogueRowCommandPort
    {
        public int Attempts { get; private set; }

        public Task ObserveSourcesAsync(
            string scopeId,
            string workflowId,
            ScopeWorkflowCatalogueSourceSnapshot? draftSource,
            ScopeWorkflowCatalogueSourceSnapshot? serviceSource,
            DateTimeOffset draftWatermarkUtc,
            DateTimeOffset serviceWatermarkUtc,
            string observationEventId,
            DateTimeOffset observedAt,
            CancellationToken ct = default)
        {
            Attempts++;
            throw new InvalidOperationException(
                "simulated actor dispatch failure (UnknownAgentKindException without an Orleans codec)");
        }
    }

    private sealed class RecordingCatalogueRowDispatcher
        : IScopeWorkflowCatalogueRowCommandPort
    {
        public List<ObserveScopeWorkflowCatalogueSourcesCommand> Commands { get; } = [];

        public Task ObserveSourcesAsync(
            string scopeId,
            string workflowId,
            ScopeWorkflowCatalogueSourceSnapshot? draftSource,
            ScopeWorkflowCatalogueSourceSnapshot? serviceSource,
            DateTimeOffset draftWatermarkUtc,
            DateTimeOffset serviceWatermarkUtc,
            string observationEventId,
            DateTimeOffset observedAt,
            CancellationToken ct = default)
        {
            Commands.Add(new ObserveScopeWorkflowCatalogueSourcesCommand
            {
                ScopeId = scopeId,
                WorkflowId = workflowId,
                DraftSource = draftSource?.Clone(),
                ServiceSource = serviceSource?.Clone(),
                ObservationEventId = observationEventId ?? string.Empty,
                ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
                DraftWatermarkUtc = Timestamp.FromDateTimeOffset(draftWatermarkUtc),
                ServiceWatermarkUtc = Timestamp.FromDateTimeOffset(serviceWatermarkUtc),
            });
            return Task.CompletedTask;
        }
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            Task.FromResult<IActor>(new StubActor(id ?? Guid.NewGuid().ToString("N")));

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            Task.FromResult<IActor>(new StubActor(id ?? Guid.NewGuid().ToString("N")));

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(new StubActor(id));

        public Task<bool> ExistsAsync(string id) => Task.FromResult(true);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new StubAgent(id);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
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
