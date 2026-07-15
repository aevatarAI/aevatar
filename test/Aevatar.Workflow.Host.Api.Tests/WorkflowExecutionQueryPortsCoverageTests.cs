using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Workflows;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionQueryPortsCoverageTests
{
    [Theory]
    [InlineData("running", WorkflowRunCompletionStatus.Running)]
    [InlineData("completed", WorkflowRunCompletionStatus.Completed)]
    [InlineData("failed", WorkflowRunCompletionStatus.Failed)]
    [InlineData("stopped", WorkflowRunCompletionStatus.Stopped)]
    [InlineData("not_found", WorkflowRunCompletionStatus.NotFound)]
    [InlineData("disabled", WorkflowRunCompletionStatus.Disabled)]
    [InlineData("unknown", WorkflowRunCompletionStatus.Unknown)]
    public void WorkflowExecutionReadModelMapper_ShouldMapCurrentStateStatuses(
        string status,
        WorkflowRunCompletionStatus expected)
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-1",
            RootActorId = "actor-1",
            CommandId = "cmd-1",
            Status = status,
            FinalOutput = "done",
            FinalError = "err",
            SagaStatus = WorkflowSagaStatus.CompensationDeadLetter, DeadLetterFailedCompensationStepId = "refund_payment",
            DeadLetterRemainingUncompensated = 2, DeadLetterError = "refund failed",
            UpdatedAt = DateTimeOffset.Parse("2026-03-17T08:00:00+00:00"),
        });

        snapshot.CompletionStatus.Should().Be(expected);
        snapshot.LastOutput.Should().Be("done");
        snapshot.LastError.Should().Be("err");
        snapshot.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        snapshot.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        snapshot.DeadLetterRemainingUncompensated.Should().Be(2); snapshot.DeadLetterError.Should().Be("refund failed");
    }

    [Fact]
    public void WorkflowExecutionReadModelMapper_ShouldExposeCurrentStateInputFileDescriptors()
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-1",
            WorkflowName = "wf",
            Status = "running",
            InputFileRefs =
            {
                new WorkflowExecutionInputFileRefReadModel
                {
                    FileId = "file-1",
                    ArtifactId = "workflow-file://file-1",
                    SourceKindValue = (int)Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.ConnectedServiceResource,
                    SourceMessageId = "om_1",
                    SourceResourceKey = "file_key_1",
                    FileName = "invoice.pdf",
                    MediaType = "application/pdf",
                    SizeBytes = 2048,
                    Sha256 = "sha-file-1",
                    CreatedAtUnixMs = 1710000000000,
                    ExpiresAtUnixMs = 1710003600000,
                    OwnerRunId = "run-owner",
                    OwnerScopeId = "scope-owner",
                },
            },
        });

        var fileRef = snapshot.InputFileRefs.Should().ContainSingle().Subject;
        fileRef.FileId.Should().Be("file-1");
        fileRef.ArtifactId.Should().Be("workflow-file://file-1");
        fileRef.SourceKind.Should().Be(Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_1");
        fileRef.SourceResourceKey.Should().Be("file_key_1");
        fileRef.FileName.Should().Be("invoice.pdf");
        fileRef.MediaType.Should().Be("application/pdf");
        fileRef.SizeBytes.Should().Be(2048);
        fileRef.Sha256.Should().Be("sha-file-1");
        fileRef.OwnerRunId.Should().Be("run-owner");
        fileRef.OwnerScopeId.Should().Be("scope-owner");
        fileRef.CreatedAtUnixMs.Should().Be(1710000000000);
        fileRef.ExpiresAtUnixMs.Should().Be(1710003600000);
        WorkflowRunFileRef.Descriptor.Fields.InDeclarationOrder()
            .Should()
            .NotContain(field => field.Name.Contains("base64", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(WorkflowExecutionCompletionStatus.Running, WorkflowRunCompletionStatus.Running)]
    [InlineData(WorkflowExecutionCompletionStatus.Completed, WorkflowRunCompletionStatus.Completed)]
    [InlineData(WorkflowExecutionCompletionStatus.Failed, WorkflowRunCompletionStatus.Failed)]
    [InlineData(WorkflowExecutionCompletionStatus.Stopped, WorkflowRunCompletionStatus.Stopped)]
    [InlineData(WorkflowExecutionCompletionStatus.NotFound, WorkflowRunCompletionStatus.NotFound)]
    [InlineData(WorkflowExecutionCompletionStatus.Disabled, WorkflowRunCompletionStatus.Disabled)]
    [InlineData(WorkflowExecutionCompletionStatus.WaitingForSignal, WorkflowRunCompletionStatus.Running)]
    [InlineData((WorkflowExecutionCompletionStatus)999, WorkflowRunCompletionStatus.Unknown)]
    public void WorkflowExecutionReadModelMapper_ShouldMapRunReportCompletionStatuses(
        WorkflowExecutionCompletionStatus status,
        WorkflowRunCompletionStatus expected)
    {
        var mapper = new WorkflowExecutionReadModelMapper();

        var report = mapper.ToRunReport(new WorkflowRunInsightReportDocument
        {
            CompletionStatus = status,
        });

        report.CompletionStatus.Should().Be(expected);
    }

    [Fact]
    public async Task WorkflowCatalogReadModelQueryPort_ShouldOnlyReadAndMapReadModelDocuments()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
        var catalogReader = new RecordingDocumentReader<WorkflowCatalogCurrentStateDocument>
        {
            Item = BuildCatalogDocument("alpha", updatedAt),
            Items =
            [
                BuildCatalogDocument("beta", updatedAt.AddMinutes(1), sortOrder: 2),
                BuildCatalogDocument("alpha", updatedAt, sortOrder: 1),
            ],
        };
        var port = new WorkflowCatalogReadModelQueryPort(
            catalogReader,
            new WorkflowCatalogReadModelMapper());

        var catalog = await port.ListWorkflowCatalogAsync();
        var detail = await port.GetWorkflowDetailAsync("alpha");

        catalog.Select(x => x.Name).Should().Equal("alpha", "beta");
        catalog[0].AuthorityStateVersion.Should().Be(11);
        catalog[0].ProjectionWatermark.Should().Be(updatedAt);
        detail.Should().NotBeNull();
        detail!.Definition.Steps.Should().ContainSingle(step => step.Id == "start");
        detail.Definition.Roles.Should().ContainSingle(role => role.Id == "operator");
        detail.Definition.Roles[0].EventModules.Should().Equal("audit", "trace");
        detail.Definition.Steps[0].Children.Should().ContainSingle(child => child.Id == "child");
        detail.Edges.Should().ContainSingle(edge => edge.From == "start" && edge.To == "child" && edge.Label == "child");
        typeof(WorkflowCapabilitiesDocument)
            .GetProperty("AuthorityStateVersion")
            .Should()
            .BeNull();
        catalogReader.QueryCalls.Should().Be(1);
        catalogReader.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowCatalogReadModelQueryPort_WhenReadModelsAreMissing_ShouldReturnHonestDefaults()
    {
        var catalogReader = new RecordingDocumentReader<WorkflowCatalogCurrentStateDocument>();
        var port = new WorkflowCatalogReadModelQueryPort(
            catalogReader,
            new WorkflowCatalogReadModelMapper());

        (await port.GetWorkflowDetailAsync("   ")).Should().BeNull();
        (await port.GetWorkflowDetailAsync("missing")).Should().BeNull();

        typeof(WorkflowCapabilitiesDocument)
            .GetProperty("AuthorityStateVersion")
            .Should()
            .BeNull();
        catalogReader.GetCalls.Should().Be(1);
        catalogReader.QueryCalls.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowCatalogReadModelQueryPort_ShouldAwaitReadModelReadersWithoutBlocking()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
        var catalogReader = new DeferredQueryDocumentReader<WorkflowCatalogCurrentStateDocument>(
            [BuildCatalogDocument("alpha", updatedAt)]);
        var port = new WorkflowCatalogReadModelQueryPort(
            catalogReader,
            new WorkflowCatalogReadModelMapper());

        var catalogTask = port.ListWorkflowCatalogAsync();

        catalogTask.IsCompleted.Should().BeFalse();
        catalogReader.CompleteQuery();
        var catalog = await catalogTask;
        catalog.Should().ContainSingle(item => item.Name == "alpha");
    }

    [Theory]
    [InlineData("custom", "home", "deterministic", "your-workflows", 0, "Saved")]
    [InlineData("custom", "cwd", "deterministic", "your-workflows", 0, "Workspace")]
    [InlineData("counter-addition", "turing", "deterministic", "advanced-patterns", 901, "Advanced")]
    [InlineData("minsky-inc-dec-jz", "turing", "deterministic", "advanced-patterns", 902, "Advanced")]
    [InlineData("machine", "turing", "deterministic", "advanced-patterns", 999, "Advanced")]
    [InlineData("transform", "builtin", "deterministic", "primitive-examples", 1, "Mini")]
    [InlineData("llm_call", "builtin", "llm", "ai-workflows", 8, "Starter")]
    [InlineData("human_input_basic_auto_resume", "builtin", "llm", "ai-workflows", 39, "Interactive")]
    [InlineData("connector_cli_demo", "builtin", "llm", "integration-workflows", 50, "Integration")]
    [InlineData("demo_template", "builtin", "deterministic", "advanced-patterns", 17, "Advanced")]
    [InlineData("48_custom", "app", "deterministic", "advanced-patterns", 48, "Advanced")]
    [InlineData("49_custom", "demo", "deterministic", "advanced-patterns", 49, "Advanced")]
    [InlineData("custom", "repo", "llm", "starter-workflows", 100, "Starter")]
    [InlineData("custom", "external", "deterministic", "starter-workflows", 200, "Workflow")]
    [InlineData("", "builtin", "deterministic", "starter-workflows", 200, "Built-in")]
    public void WorkflowCatalogClassificationPolicy_ShouldClassifyCatalogGroups(
        string workflowName,
        string sourceKind,
        string category,
        string expectedGroup,
        int expectedSortOrder,
        string expectedSourceLabel)
    {
        var classification = WorkflowCatalogClassificationPolicy.Classify(workflowName, sourceKind, category);

        classification.Group.Should().Be(expectedGroup);
        classification.SortOrder.Should().Be(expectedSortOrder);
        classification.SourceLabel.Should().Be(expectedSourceLabel);
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenDisabled_ShouldReturnEmptyGraphResultsWithoutTouchingStores()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            WorkflowArtifactQueryEnabled = false,
        });

        harness.ArtifactPort.WorkflowArtifactQueryEnabled.Should().BeFalse();
        (await harness.ArtifactPort.GetWorkflowRunGraphExportEdgesAsync("actor-1")).Should().BeEmpty();
        (await harness.ArtifactPort.GetWorkflowRunGraphExportSubgraphAsync("actor-1")).RootNodeId.Should().Be("actor-1");
        harness.CurrentStateReader.GetCalls.Should().Be(0);
        harness.ReportReader.GetCalls.Should().Be(0);
        harness.GraphStore.GetNeighborsCalls.Should().Be(0);
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenActorIdIsBlank_ShouldShortCircuitGraphQueries()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            WorkflowArtifactQueryEnabled = true,
        });

        (await harness.ArtifactPort.GetWorkflowRunGraphExportEdgesAsync("   ")).Should().BeEmpty();

        var subgraph = await harness.ArtifactPort.GetWorkflowRunGraphExportSubgraphAsync("   ");
        subgraph.RootNodeId.Should().BeEmpty();
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();

        harness.GraphStore.GetNeighborsCalls.Should().Be(0);
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenActorIdIsNull_ShouldReturnEmptyRootSubgraph()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            WorkflowArtifactQueryEnabled = true,
        });

        var subgraph = await harness.ArtifactPort.GetWorkflowRunGraphExportSubgraphAsync(null!);

        subgraph.RootNodeId.Should().BeEmpty();
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task CurrentStateQueryPort_WhenEnabled_ShouldReadAndMapCurrentStateDocuments()
    {
        var now = DateTimeOffset.UtcNow;
        var harness = CreateHarness(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowActorCurrentStateQueryEnabled = true,
            },
            currentStateReader: new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>
            {
                Item = new WorkflowExecutionCurrentStateDocument
                {
                    Id = "actor-1",
                    RootActorId = "actor-1",
                    WorkflowName = "wf",
                    StateVersion = 12,
                    LastEventId = "evt-12",
                    SagaStatus = WorkflowSagaStatus.CompensationDeadLetter, DeadLetterFailedCompensationStepId = "refund_payment",
                    DeadLetterRemainingUncompensated = 2, DeadLetterError = "refund failed",
                    UpdatedAt = now,
                },
                Items =
                [
                    new WorkflowExecutionCurrentStateDocument
                    {
                        Id = "actor-1",
                        RootActorId = "actor-1",
                        WorkflowName = "wf",
                        StateVersion = 12,
                        LastEventId = "evt-12",
                        SagaStatus = WorkflowSagaStatus.CompensationDeadLetter, DeadLetterFailedCompensationStepId = "refund_payment",
                        DeadLetterRemainingUncompensated = 2, DeadLetterError = "refund failed",
                        UpdatedAt = now,
                    },
                ],
            });

        var snapshot = await harness.CurrentStatePort.GetWorkflowActorCurrentStateAsync("actor-1");
        var snapshots = await harness.CurrentStatePort.ListWorkflowActorCurrentStatesAsync(5);
        var projectionState = await harness.CurrentStatePort.GetWorkflowActorProjectionStateAsync("actor-1");

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("actor-1");
        snapshot.StateVersion.Should().Be(12);
        snapshot.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        snapshot.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        snapshot.DeadLetterRemainingUncompensated.Should().Be(2); snapshot.DeadLetterError.Should().Be("refund failed");
        snapshots.Should().ContainSingle();
        projectionState.Should().NotBeNull();
        projectionState!.ActorId.Should().Be("actor-1");
        harness.CurrentStateReader.GetCalls.Should().Be(2);
        harness.CurrentStateReader.QueryCalls.Should().Be(1);
        harness.ReportReader.GetCalls.Should().Be(0);
    }

    [Fact]
    public async Task CurrentStateQueryPort_WhenDisabledBlankOrMissing_ShouldShortCircuit()
    {
        var disabled = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            WorkflowActorCurrentStateQueryEnabled = false,
        });
        disabled.CurrentStatePort.WorkflowActorCurrentStateQueryEnabled.Should().BeFalse();
        (await disabled.CurrentStatePort.GetWorkflowActorCurrentStateAsync("actor-1")).Should().BeNull();
        (await disabled.CurrentStatePort.ListWorkflowActorCurrentStatesAsync()).Should().BeEmpty();
        (await disabled.CurrentStatePort.GetWorkflowActorProjectionStateAsync("actor-1")).Should().BeNull();
        disabled.CurrentStateReader.GetCalls.Should().Be(0);
        disabled.CurrentStateReader.QueryCalls.Should().Be(0);

        var missing = CreateHarness(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowActorCurrentStateQueryEnabled = true,
            },
            currentStateReader: new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>());
        (await missing.CurrentStatePort.GetWorkflowActorCurrentStateAsync("   ")).Should().BeNull();
        (await missing.CurrentStatePort.GetWorkflowActorProjectionStateAsync("actor-404")).Should().BeNull();
        missing.CurrentStateReader.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task ArtifactQueryPort_ListWorkflowRunTimelineExportAsync_ShouldDeriveFromReportArtifact()
    {
        var harness = CreateHarness(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowArtifactQueryEnabled = true,
            },
            reportReader: new RecordingDocumentReader<WorkflowRunInsightReportDocument>
            {
                Item = new WorkflowRunInsightReportDocument
                {
                    Id = "actor-1",
                    RootActorId = "actor-1",
                    Timeline =
                    {
                        new WorkflowExecutionTimelineEvent
                        {
                            Timestamp = DateTimeOffset.Parse("2026-03-17T08:01:00+00:00"),
                            Stage = "older",
                            Message = "msg-1",
                            EventType = "type-1",
                            Data = { ["k1"] = "v1" },
                        },
                        new WorkflowExecutionTimelineEvent
                        {
                            Timestamp = DateTimeOffset.Parse("2026-03-17T08:03:00+00:00"),
                            Stage = "newer",
                            Message = "msg-2",
                            EventType = "type-2",
                            Data = { ["k2"] = "v2" },
                        },
                        new WorkflowExecutionTimelineEvent
                        {
                            Timestamp = DateTimeOffset.Parse("2026-03-17T08:02:00+00:00"),
                            Stage = "middle",
                            Message = "msg-3",
                            EventType = "type-3",
                        },
                    },
                },
            });

        var items = await harness.ArtifactPort.ListWorkflowRunTimelineExportAsync("actor-1", take: 2);

        items.Select(x => x.Stage).Should().Equal("newer", "middle");
        items[0].Data.Should().Contain("k2", "v2");
        harness.ReportReader.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task ArtifactQueryPort_ListWorkflowRunTimelineExportAsync_ShouldShortCircuitWhenDisabledBlankOrMissing()
    {
        var disabled = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = false,
            WorkflowArtifactQueryEnabled = true,
        });
        (await disabled.ArtifactPort.ListWorkflowRunTimelineExportAsync("actor-1")).Should().BeEmpty();
        disabled.ReportReader.GetCalls.Should().Be(0);

        var enabled = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            WorkflowArtifactQueryEnabled = true,
        });
        (await enabled.ArtifactPort.ListWorkflowRunTimelineExportAsync("   ")).Should().BeEmpty();
        (await enabled.ArtifactPort.ListWorkflowRunTimelineExportAsync("actor-404")).Should().BeEmpty();
        enabled.ReportReader.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenEnabled_ShouldForwardGraphOptionsToGraphStore()
    {
        var now = DateTimeOffset.UtcNow;
        var harness = CreateHarness(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                WorkflowArtifactQueryEnabled = true,
            },
            graphStore: new RecordingProjectionGraphStore
            {
                GraphEdgesResult =
                [
                    new ProjectionGraphEdge
                    {
                        Scope = WorkflowExecutionGraphConstants.Scope,
                        EdgeId = "edge-1",
                        FromNodeId = "actor-1",
                        ToNodeId = "actor-2",
                        EdgeType = "CHILD_OF",
                        UpdatedAt = now,
                    },
                ],
                GraphSubgraphResult = new ProjectionGraphSubgraph
                {
                    Nodes =
                    [
                        new ProjectionGraphNode
                        {
                            Scope = WorkflowExecutionGraphConstants.Scope,
                            NodeId = "actor-1",
                            NodeType = "Actor",
                        },
                    ],
                },
            },
            currentStateReader: new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>
            {
                Item = new WorkflowExecutionCurrentStateDocument
                {
                    Id = "actor-1",
                    RootActorId = "actor-1",
                    StateVersion = 12,
                    LastEventId = "evt-12",
                    UpdatedAt = now,
                    WorkflowName = "wf",
                },
            });

        var options = new WorkflowRunGraphExportQueryOptions
        {
            Direction = WorkflowRunGraphExportDirection.Inbound,
            EdgeTypes = ["CHILD_OF"],
        };

        var edges = await harness.ArtifactPort.GetWorkflowRunGraphExportEdgesAsync("actor-1", take: 7, options: options);
        var subgraph = await harness.ArtifactPort.GetWorkflowRunGraphExportSubgraphAsync("actor-1", depth: 4, take: 11, options: options);

        edges.Should().ContainSingle(x => x.EdgeId == "edge-1");
        subgraph.RootNodeId.Should().Be("actor-1");

        harness.GraphStore.LastGraphEdgesQuery.Should().NotBeNull();
        harness.GraphStore.LastGraphEdgesQuery!.RootNodeId.Should().Be("actor-1");
        harness.GraphStore.LastGraphEdgesQuery.Take.Should().Be(7);
        harness.GraphStore.LastGraphEdgesQuery.Direction.Should().Be(ProjectionGraphDirection.Inbound);
        harness.GraphStore.LastGraphEdgesQuery.EdgeTypes.Should().Equal("CHILD_OF");

        harness.GraphStore.LastSubgraphQuery.Should().NotBeNull();
        harness.GraphStore.LastSubgraphQuery!.RootNodeId.Should().Be("actor-1");
        harness.GraphStore.LastSubgraphQuery.Depth.Should().Be(4);
        harness.GraphStore.LastSubgraphQuery.Take.Should().Be(11);
    }

    [Fact]
    public async Task ArtifactQueryPort_ShouldNormalizeBlankEdgeTypes_AndDefaultDirectionToBoth()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            WorkflowArtifactQueryEnabled = true,
        });

        var options = new WorkflowRunGraphExportQueryOptions
        {
            Direction = (WorkflowRunGraphExportDirection)99,
            EdgeTypes = [" CHILD_OF ", "", "CHILD_OF", "  ", "OWNS"],
        };

        await harness.ArtifactPort.GetWorkflowRunGraphExportEdgesAsync("actor-1", take: 0, options: options);
        await harness.ArtifactPort.GetWorkflowRunGraphExportSubgraphAsync("actor-1", depth: 99, take: 5001, options: options);

        harness.GraphStore.LastGraphEdgesQuery.Should().NotBeNull();
        harness.GraphStore.LastGraphEdgesQuery!.Direction.Should().Be(ProjectionGraphDirection.Both);
        harness.GraphStore.LastGraphEdgesQuery.EdgeTypes.Should().Equal("CHILD_OF", "OWNS");
        harness.GraphStore.LastGraphEdgesQuery.Take.Should().Be(1);

        harness.GraphStore.LastSubgraphQuery.Should().NotBeNull();
        harness.GraphStore.LastSubgraphQuery!.Direction.Should().Be(ProjectionGraphDirection.Both);
        harness.GraphStore.LastSubgraphQuery.Depth.Should().Be(8);
        harness.GraphStore.LastSubgraphQuery.Take.Should().Be(2000);
    }

    private static QueryPortHarness CreateHarness(
        WorkflowExecutionProjectionOptions options,
        RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>? currentStateReader = null,
        RecordingDocumentReader<WorkflowRunInsightReportDocument>? reportReader = null,
        RecordingProjectionGraphStore? graphStore = null)
    {
        currentStateReader ??= new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>();
        reportReader ??= new RecordingDocumentReader<WorkflowRunInsightReportDocument>();
        graphStore ??= new RecordingProjectionGraphStore();
        return new QueryPortHarness(
            new WorkflowExecutionCurrentStateQueryPort(
                currentStateReader,
                new WorkflowExecutionReadModelMapper(),
                options),
            new WorkflowExecutionArtifactQueryPort(
                reportReader,
                new WorkflowExecutionReadModelMapper(),
                graphStore,
                options),
            currentStateReader,
            reportReader,
            graphStore);
    }

    private sealed record QueryPortHarness(
        IWorkflowExecutionCurrentStateQueryPort CurrentStatePort,
        IWorkflowExecutionArtifactQueryPort ArtifactPort,
        RecordingDocumentReader<WorkflowExecutionCurrentStateDocument> CurrentStateReader,
        RecordingDocumentReader<WorkflowRunInsightReportDocument> ReportReader,
        RecordingProjectionGraphStore GraphStore);

    private static WorkflowCatalogCurrentStateDocument BuildCatalogDocument(
        string workflowName,
        DateTimeOffset updatedAt,
        int sortOrder = 1) =>
        new()
        {
            Id = workflowName,
            ActorId = $"workflow-definition:{workflowName}",
            WorkflowName = workflowName,
            WorkflowYaml = $"name: {workflowName}",
            Description = "Workflow description",
            Category = "deterministic",
            Group = "starter-workflows",
            GroupLabel = "Starter Workflows",
            SortOrder = sortOrder,
            Source = "repo",
            SourceLabel = "Starter",
            ShowInLibrary = true,
            StateVersion = 10 + sortOrder,
            LastEventId = $"evt-{sortOrder}",
            UpdatedAt = updatedAt,
            Primitives = ["assign"],
            Roles =
            [
                new()
                {
                    Id = "operator", Name = "Operator", SystemPrompt = "Operate.", Provider = "openai",
                    Model = "gpt-test", Temperature = 0.1f, MaxTokens = 512, MaxToolRounds = 2,
                    MaxHistoryMessages = 3, EventModules = ["audit", "trace"], EventRoutes = "route:*",
                    Connectors = ["aevatar_cli"],
                },
            ],
            Steps =
            [
                new()
                {
                    Id = "start",
                    Type = "assign",
                    TargetRole = "operator",
                    Parameters = { ["target"] = "result" },
                    Branches = { ["done"] = "child" },
                    Children =
                    [
                        new() { Id = "child", Type = "assign", TargetRole = "operator" },
                    ],
                },
            ],
            Edges =
            [
                new() { From = "start", To = "child", Label = "child" },
            ],
            RequiredConnectors = ["aevatar_cli"],
            WorkflowCalls = ["child_workflow"],
        };

    private sealed class RecordingDocumentReader<TReadModel> : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public int GetCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public TReadModel? Item { get; init; }
        public IReadOnlyList<TReadModel> Items { get; init; } = [];

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        { _ = key; ct.ThrowIfCancellationRequested(); GetCalls++; return Task.FromResult(Item); }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        { _ = query; ct.ThrowIfCancellationRequested(); QueryCalls++; return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel> { Items = Items }); }
    }

    private sealed class DeferredQueryDocumentReader<TReadModel> : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        private readonly TaskCompletionSource<ProjectionDocumentQueryResult<TReadModel>> _query =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IReadOnlyList<TReadModel> _items;

        public DeferredQueryDocumentReader(IReadOnlyList<TReadModel> items) => _items = items;

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        { _ = key; ct.ThrowIfCancellationRequested(); return Task.FromResult<TReadModel?>(null); }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        { _ = query; ct.ThrowIfCancellationRequested(); return _query.Task; }

        public void CompleteQuery() =>
            _query.SetResult(new ProjectionDocumentQueryResult<TReadModel> { Items = _items });
    }

    private sealed class RecordingProjectionGraphStore : IProjectionGraphStore
    {
        public int GetNeighborsCalls { get; private set; }
        public int GetSubgraphCalls { get; private set; }
        public ProjectionGraphQuery? LastGraphEdgesQuery { get; private set; }
        public ProjectionGraphQuery? LastSubgraphQuery { get; private set; }
        public IReadOnlyList<ProjectionGraphEdge> GraphEdgesResult { get; init; } = [];
        public ProjectionGraphSubgraph GraphSubgraphResult { get; init; } = new();

        public Task ReplaceOwnerGraphAsync(ProjectionOwnedGraph graph, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(string scope, string ownerId, int skip = 0, int take = 5000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(string scope, string ownerId, int skip = 0, int take = 5000, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetNeighborsCalls++;
            LastGraphEdgesQuery = query;
            return Task.FromResult(GraphEdgesResult);
        }

        public Task<ProjectionGraphSubgraph> GetSubgraphAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetSubgraphCalls++;
            LastSubgraphQuery = query;
            return Task.FromResult(GraphSubgraphResult);
        }
    }
}
