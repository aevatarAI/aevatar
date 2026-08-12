using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Domain.Studio.Models;
using IWorkflowYamlDocumentService = Aevatar.Studio.Application.Studio.Abstractions.IWorkflowYamlDocumentService;
using WorkflowParseResult = Aevatar.Studio.Application.Studio.Abstractions.WorkflowParseResult;
using Aevatar.Studio.Projection.Metadata;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkspaceCurrentStateProjectorTests
{
    private const string RootActorId = "studio-workspace-scope-1";
    private static readonly JsonFormatter ReadModelFormatter = new(
        JsonFormatter.Settings.Default
            .WithPreserveProtoFieldNames(true)
            .WithFormatDefaultValues(true));
    private static readonly JsonParser StateRootParser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    [Fact]
    public async Task ProjectAsync_ShouldUpsertDocument_WhenCommittedWorkspaceStateArrives()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-05-19T08:00:00Z"));
        var projector = CreateProjector(dispatcher, clock);
        var state = new StudioWorkspaceState
        {
            WorkspaceId = RootActorId,
            ScopeId = "scope-1",
            Settings = new StudioWorkspaceSettings
            {
                RuntimeBaseUrl = "http://127.0.0.1:5100",
            },
        };
        state.Directories.Add(new StudioWorkspaceDirectory
        {
            DirectoryId = "dir-1",
            Label = "Drafts",
            Path = "/tmp/drafts",
        });

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new StudioWorkspaceSettingsUpdated { WorkspaceId = RootActorId, ScopeId = "scope-1" },
                state,
                version: 7,
                eventId: "evt-7"));

        dispatcher.Upserts.Should().ContainSingle();
        var written = dispatcher.Upserts[0];
        written.Id.Should().Be(RootActorId);
        written.ActorId.Should().Be(RootActorId);
        written.StateVersion.Should().Be(7);
        written.LastEventId.Should().Be("evt-7");
        written.UpdatedAt.Should().NotBeNull();
        var parsedState = StateRootParser.Parse<StudioWorkspaceState>(written.StateRootJson);
        parsedState.Settings.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
    }

    [Fact]
    public async Task ProjectAsync_ShouldStoreWorkspaceStateAsOpaqueJsonString_ForElasticsearch()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var catalogueDispatcher = new RecordingCatalogueSourceDispatcher();
        var rowDispatcher = new RecordingCatalogueRowDispatcher();
        var projectedAt = DateTimeOffset.Parse("2026-05-19T08:00:00Z");
        var projector = CreateProjector(
            dispatcher,
            new FixedProjectionClock(projectedAt),
            catalogueDispatcher,
            rowDispatcher);
        var state = new StudioWorkspaceState
        {
            WorkspaceId = RootActorId,
            ScopeId = "scope-1",
        };
        state.Drafts.Add("wf-alpha", new StudioWorkflowDraft
        {
            WorkflowId = "wf-alpha",
            Name = "workflow alpha",
            FileName = "workflow-alpha.yaml",
            DirectoryId = "dir-1",
            DirectoryLabel = "Drafts",
            Yaml = """
                   name: workflow-alpha
                   steps:
                     - id: step-1
                       config:
                         model:
                           provider:
                             nested:
                               arbitrary: value
                   """,
            Version = 4,
        });

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new StudioWorkflowDraftSaved
                {
                    WorkspaceId = RootActorId,
                    ScopeId = "scope-1",
                    Draft = state.Drafts["wf-alpha"],
                },
                state,
                version: 9,
                eventId: "evt-9"));

        var written = dispatcher.Upserts.Should().ContainSingle().Subject;
        written.StateRootJson.Should().Contain("\"drafts\"");
        written.StateRootJson.Should().Contain("nested");
        written.DraftSummaries.Should().ContainSingle().Which.Should().BeEquivalentTo(new StudioWorkspaceDraftSummary
        {
            WorkflowId = "wf-alpha",
            Name = "workflow alpha",
            FileName = "workflow-alpha.yaml",
            DirectoryId = "dir-1",
            Version = 4,
        });
        var catalogueSource = catalogueDispatcher.Upserts.Should().ContainSingle().Subject;
        catalogueSource.ScopeId.Should().Be("scope-1");
        catalogueSource.WorkflowId.Should().Be("wf-alpha");
        catalogueSource.SourceKind.Should().Be(ScopeWorkflowCatalogueSourceDocument.DraftSourceKind);
        catalogueSource.ActorId.Should().Be("scope-workflow-catalogue-source:scope-1:wf-alpha:draft");
        catalogueSource.StateVersion.Should().Be(catalogueSource.UpdatedAt.ToDateTimeOffset().UtcDateTime.Ticks);
        catalogueSource.Name.Should().Be("workflow-alpha");
        rowDispatcher.Commands.Should().ContainSingle(command => command.ScopeId == "scope-1" &&
                                                                  command.WorkflowId == "wf-alpha" &&
                                                                  command.DraftSource != null);
        StudioWorkspaceCurrentStateDocument.Descriptor.Fields.InDeclarationOrder()
            .Select(field => field.Name)
            .Should().NotContain("state_root");
        StudioWorkspaceCurrentStateDocument.Descriptor.Fields.InDeclarationOrder()
            .Single(field => field.Name == "state_root_json")
            .FieldType.Should().Be(FieldType.String);
        StudioWorkspaceCurrentStateDocument.Descriptor.Fields.InDeclarationOrder()
            .Single(field => field.Name == "draft_summaries")
            .MessageType.Should().Be(StudioWorkspaceDraftSummary.Descriptor);

        using var serialized = JsonDocument.Parse(ReadModelFormatter.Format(written));
        serialized.RootElement.TryGetProperty("state_root_json", out var stateRootJsonElement).Should().BeTrue();
        stateRootJsonElement.ValueKind.Should().Be(JsonValueKind.String);
        serialized.RootElement.TryGetProperty("draft_summaries", out var draftSummariesElement).Should().BeTrue();
        draftSummariesElement.ValueKind.Should().Be(JsonValueKind.Array);
        draftSummariesElement[0].TryGetProperty("workflow_id", out _).Should().BeTrue();
        draftSummariesElement[0].TryGetProperty("name", out _).Should().BeTrue();
        draftSummariesElement[0].TryGetProperty("yaml", out _).Should().BeFalse();
        serialized.RootElement.TryGetProperty("state_root", out _).Should().BeFalse();
    }

    [Fact]
    public void Metadata_ShouldDisableDynamicMappingAndKeepStateRootJsonNonIndexed()
    {
        var metadata = new StudioWorkspaceCurrentStateDocumentMetadataProvider().Metadata;

        metadata.Mappings["dynamic"].Should().Be(false);
        var properties = metadata.Mappings["properties"].Should()
            .BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Subject;
        properties.Should().ContainKey("id");
        properties.Should().ContainKey("actor_id");
        properties.Should().ContainKey("state_version");
        properties.Should().ContainKey("last_event_id");
        properties.Should().ContainKey("updated_at");
        properties.Should().NotContainKey("state_root");

        var stateRootJsonMapping = properties["state_root_json"].Should()
            .BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Subject;
        stateRootJsonMapping["type"].Should().Be("text");
        stateRootJsonMapping["index"].Should().Be(false);

        var draftSummariesMapping = properties["draft_summaries"].Should()
            .BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Subject;
        draftSummariesMapping["type"].Should().Be("nested");
        draftSummariesMapping["dynamic"].Should().Be(false);
        var draftSummaryProperties = draftSummariesMapping["properties"].Should()
            .BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Subject;
        draftSummaryProperties.Should().ContainKeys("workflow_id", "name", "file_name", "directory_id", "version");
        draftSummaryProperties.Should().NotContainKey("yaml");
    }

    [Fact]
    public async Task ProjectAsync_ShouldNoOp_WhenEnvelopeIsUnrelatedOrInvalid()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = CreateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(
            NewContext(),
            new EventEnvelope
            {
                Id = "raw",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(new StudioWorkspaceSettingsUpdated()),
            });

        await projector.ProjectAsync(
            NewContext(),
            new EventEnvelope
            {
                Id = "wrong-state",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-wrong",
                        Version = 1,
                        EventData = Any.Pack(new StudioWorkspaceSettingsUpdated()),
                    },
                    StateRoot = Any.Pack(new StringValue { Value = "not-workspace-state" }),
                }),
            });

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WhenDraftDeleted_ShouldDeleteCatalogueDraftSourceByExactKey()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var catalogueDispatcher = new RecordingCatalogueSourceDispatcher();
        var rowDispatcher = new RecordingCatalogueRowDispatcher();
        var projector = CreateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            catalogueDispatcher,
            rowDispatcher);
        var projectedAt = DateTimeOffset.Parse("2026-08-06T00:00:00Z");

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new StudioWorkflowDraftDeleted
                {
                    WorkspaceId = RootActorId,
                    ScopeId = "scope-1",
                    WorkflowId = "wf-alpha",
                    DeletedAtUtc = Timestamp.FromDateTimeOffset(projectedAt),
                },
                new StudioWorkspaceState { WorkspaceId = RootActorId, ScopeId = "scope-1" },
                version: 12,
                eventId: "evt-draft-deleted",
                envelopeTimestamp: projectedAt,
                stateEventTimestamp: projectedAt));

        dispatcher.Upserts.Should().ContainSingle();
        catalogueDispatcher.Upserts.Should().BeEmpty();
        catalogueDispatcher.DeleteMarkers.Should().ContainSingle().Which.Should().Be(
            new ProjectionDocumentDeleteMarker(
                "scope-1:wf-alpha:draft",
                "scope-workflow-catalogue-source:scope-1:wf-alpha:draft",
                projectedAt.UtcDateTime.Ticks + 1,
                "evt-draft-deleted",
                projectedAt));
        rowDispatcher.Commands.Should().ContainSingle();
        rowDispatcher.Commands[0].ScopeId.Should().Be("scope-1");
        rowDispatcher.Commands[0].WorkflowId.Should().Be("wf-alpha");
        rowDispatcher.Commands[0].DraftSource.Should().BeNull();
        rowDispatcher.Commands[0].ObservationEventId.Should().Be("evt-draft-deleted");
    }

    [Fact]
    public async Task ProjectAsync_WhenStateEventTimestampMissing_ShouldUseEnvelopeTimestamp()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-05-19T12:00:00Z"));
        var projector = CreateProjector(dispatcher, clock);
        var envelopeTimestamp = DateTimeOffset.Parse("2026-05-19T11:00:00Z");

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new StudioWorkspaceDirectoryRemoved { WorkspaceId = RootActorId, ScopeId = "scope-1" },
                new StudioWorkspaceState { WorkspaceId = RootActorId, ScopeId = "scope-1" },
                version: 8,
                eventId: "evt-8",
                envelopeTimestamp: envelopeTimestamp,
                stateEventTimestamp: null));

        dispatcher.Upserts.Should().ContainSingle();
        dispatcher.Upserts[0].UpdatedAt.ToDateTimeOffset().Should().Be(envelopeTimestamp);
    }

    private static StudioWorkspaceCurrentStateProjector CreateProjector(
        RecordingWriteDispatcher dispatcher,
        IProjectionClock clock,
        RecordingCatalogueSourceDispatcher? catalogueDispatcher = null,
        RecordingCatalogueRowDispatcher? rowDispatcher = null)
    {
        catalogueDispatcher ??= new RecordingCatalogueSourceDispatcher();
        rowDispatcher ??= new RecordingCatalogueRowDispatcher();
        var rowMaterializer = new ScopeWorkflowCatalogueRowMaterializer(
            new RecordingCatalogueSourceReader(catalogueDispatcher),
            rowDispatcher);
        return new StudioWorkspaceCurrentStateProjector(
            dispatcher,
            catalogueDispatcher,
            rowMaterializer,
            new StubWorkflowYamlDocumentService(),
            clock);
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = StudioWorkspaceConventions.ProjectionKindValue,
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        StudioWorkspaceState state,
        long version,
        string eventId,
        DateTimeOffset? envelopeTimestamp = null,
        DateTimeOffset? stateEventTimestamp = null)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(envelopeTimestamp ?? DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                    Timestamp = stateEventTimestamp.HasValue
                        ? Timestamp.FromDateTimeOffset(stateEventTimestamp.Value)
                        : null,
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<StudioWorkspaceCurrentStateDocument>
    {
        public List<StudioWorkspaceCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            StudioWorkspaceCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
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

    private sealed class RecordingCatalogueSourceReader(RecordingCatalogueSourceDispatcher dispatcher)
        : IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string>
    {
        public Task<ScopeWorkflowCatalogueSourceDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            var deleted = dispatcher.DeleteMarkers.Any(marker => string.Equals(marker.Id, key, StringComparison.Ordinal));
            var source = deleted
                ? null
                : dispatcher.Upserts.LastOrDefault(document => string.Equals(document.Id, key, StringComparison.Ordinal));
            return Task.FromResult(source);
        }

        public Task<ProjectionDocumentQueryResult<ScopeWorkflowCatalogueSourceDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<ScopeWorkflowCatalogueSourceDocument>
            {
                Items = dispatcher.Upserts,
                NextCursor = null,
                TotalCount = dispatcher.Upserts.Count,
            });
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
            });
            return Task.CompletedTask;
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

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
