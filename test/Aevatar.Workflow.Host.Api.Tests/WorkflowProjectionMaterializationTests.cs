using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowProjectionMaterializationTests
{
    private const string AuditSentinel = "audit-secret-sentinel";

    [Fact]
    public void WorkflowRunInsightReportArtifactProjector_Ctor_ShouldThrow_WhenDependencyMissing()
    {
        var reportStore = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);

        Action noReader = () => new WorkflowRunInsightReportArtifactProjector(null!, reportStore, graphWriter);
        Action noReportWriter = () => new WorkflowRunInsightReportArtifactProjector(reportStore, null!, graphWriter);
        Action noGraphWriter = () => new WorkflowRunInsightReportArtifactProjector(reportStore, reportStore, null!);

        noReader.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("reportReader");
        noReportWriter.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("reportWriter");
        noGraphWriter.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("graphWriter");
    }

    [Fact]
    public async Task WorkflowExecutionCurrentStateProjector_ShouldWriteCommittedSnapshot_AndIgnoreInvalidEnvelope()
    {
        var store = new RecordingDocumentStore<WorkflowExecutionCurrentStateDocument>(x => x.Id);
        var projector = new WorkflowExecutionCurrentStateProjector(store, new FixedClock(DateTimeOffset.Parse("2026-03-17T10:00:00+00:00")));
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(context, new EventEnvelope());
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new WorkflowCompletedEvent
                {
                    WorkflowName = "wf-1",
                    Success = false,
                    Output = "partial",
                    Error = "boom",
                    RunId = "run-1",
                },
                BuildState("failed", finalError: "boom")));

        store.UpsertCount.Should().Be(1);
        store.Stored.Should().ContainKey("actor-1");
        store.Stored["actor-1"].Success.Should().BeFalse();
        store.Stored["actor-1"].RunId.Should().Be("run-1");
        store.Stored["actor-1"].Status.Should().Be("failed");
        store.Stored["actor-1"].FinalError.Should().Be("boom");

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new WorkflowCompletedEvent
                {
                    WorkflowName = "wf-1",
                    Success = true,
                    Output = "done",
                    RunId = "run-1",
                },
                BuildState("completed", finalOutput: "done")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRunStoppedEvent
                {
                    RunId = "run-1",
                    Reason = "manual-stop",
                },
                BuildState("stopped", runId: "", finalError: "manual-stop")));

        store.UpsertCount.Should().Be(3);
        store.Stored.Should().ContainKey("actor-1");
        store.Stored["actor-1"].Success.Should().BeNull();
        store.Stored["actor-1"].RunId.Should().Be("actor-1");
        store.Stored["actor-1"].Status.Should().Be("stopped");
        store.Stored["actor-1"].FinalError.Should().Be("manual-stop");
    }

    [Fact]
    public async Task WorkflowCatalogCurrentStateProjector_ShouldProjectDefinitionReadModelWithFreshness()
    {
        var store = new RecordingDocumentStore<WorkflowCatalogCurrentStateDocument>(x => x.Id);
        var projector = new WorkflowCatalogCurrentStateProjector(
            store,
            new FixedClock(DateTimeOffset.Parse("2026-03-17T10:00:00+00:00")));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "workflow-definition:repo_install",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(context, new EventEnvelope());
        await projector.ProjectAsync(
            context,
            BuildDefinitionCommittedEnvelope(
                7,
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = "repo_install",
                    WorkflowYaml = BuildDefinitionYaml("repo_install"),
                    SourceKind = "repo",
                },
                new WorkflowState
                {
                    WorkflowName = "repo_install",
                    WorkflowYaml = BuildDefinitionYaml("repo_install"),
                    SourceKind = "repo",
                    Compiled = true,
                }));

        store.UpsertCount.Should().Be(1);
        store.Stored.Should().ContainKey("repo_install");
        var document = store.Stored["repo_install"];
        document.ActorId.Should().Be("workflow-definition:repo_install");
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("definition-evt-7");
        document.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-03-17T11:07:00+00:00"));
        document.Source.Should().Be("repo");
        document.Primitives.Should().Contain("assign");
        document.Steps.Should().ContainSingle(step => step.Id == "bootstrap");
    }

    [Fact]
    public async Task WorkflowCatalogCurrentStateProjector_ShouldProjectGraphAndDependencyBranches()
    {
        var store = new RecordingDocumentStore<WorkflowCatalogCurrentStateDocument>(x => x.Id);
        var projector = new WorkflowCatalogCurrentStateProjector(
            store,
            new FixedClock(DateTimeOffset.Parse("2026-03-17T10:00:00+00:00")));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "workflow-definition:complex",
            ProjectionKind = "workflow-binding",
        };

        await projector.ProjectAsync(
            context,
            BuildDefinitionCommittedEnvelope(
                8,
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = "complex",
                    WorkflowYaml = BuildComplexDefinitionYaml("complex"),
                },
                new WorkflowState
                {
                    WorkflowName = " complex ",
                    WorkflowYaml = BuildComplexDefinitionYaml("complex"),
                    Compiled = true,
                }));
        await projector.ProjectAsync(
            context,
            BuildDefinitionCommittedEnvelope(
                9,
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = "blank",
                    WorkflowYaml = "",
                },
                new WorkflowState
                {
                    WorkflowName = "   ",
                    WorkflowYaml = BuildComplexDefinitionYaml("blank"),
                }));
        await projector.ProjectAsync(
            context,
            BuildDefinitionCommittedEnvelope(
                10,
                new WorkflowCompletedEvent(),
                new WorkflowState
                {
                    WorkflowName = "ignored",
                    WorkflowYaml = BuildComplexDefinitionYaml("ignored"),
                }));

        store.UpsertCount.Should().Be(1);
        var document = store.Stored["complex"];
        document.Source.Should().Be("builtin");
        document.Category.Should().Be("llm");
        document.RequiresLlmProvider.Should().BeFalse();
        document.Primitives.Should().Contain(["conditional", "connector_call", "foreach", "llm_call", "workflow_call"]);
        document.RequiredConnectors.Should().Equal("aevatar_cli", "mcp_tools");
        document.WorkflowCalls.Should().Equal("child_workflow");
        document.Roles.Should().ContainSingle(role => role.Id == "operator");
        document.Roles[0].EventModules.Should().Equal("audit", "trace");
        document.Steps.Should().Contain(step => step.Id == "child_llm" && step.TargetRole == "operator");
        document.Steps.Should().Contain(step =>
            step.Id == "fanout" &&
            step.Children.Single().Id == "child_llm");
        document.Edges.Should().Contain(edge => edge.From == "decide" && edge.To == "call_connector" && edge.Label == "true");
        document.Edges.Should().Contain(edge => edge.From == "call_connector" && edge.To == "call_child");
        document.Edges.Should().Contain(edge => edge.From == "fanout" && edge.To == "child_llm" && edge.Label == "child");
    }

    [Fact]
    public async Task WorkflowCatalogCurrentStateProjector_ShouldNotMaterializeScopeOwnedDefinition()
    {
        var store = new RecordingDocumentStore<WorkflowCatalogCurrentStateDocument>(x => x.Id);
        var projector = new WorkflowCatalogCurrentStateProjector(
            store,
            new FixedClock(DateTimeOffset.Parse("2026-03-17T10:00:00+00:00")));
        var context = new WorkflowBindingProjectionContext
        {
            RootActorId = "scope-workflow:tenant-a:report",
            ProjectionKind = "workflow-binding",
        };

        // A scope-owned workflow definition (bound during service invocation/activation) carries a
        // non-empty ScopeId. The global runnable catalog is a shared template gallery, so a scoped
        // definition must never materialize into it — otherwise any tenant can read another tenant's
        // workflow YAML/system prompts, and same-named scoped definitions clobber each other.
        await projector.ProjectAsync(
            context,
            BuildDefinitionCommittedEnvelope(
                11,
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = "tenant_report",
                    WorkflowYaml = BuildDefinitionYaml("tenant_report"),
                    SourceKind = "service_revision",
                    ScopeId = "tenant-a",
                },
                new WorkflowState
                {
                    WorkflowName = "tenant_report",
                    WorkflowYaml = BuildDefinitionYaml("tenant_report"),
                    SourceKind = "service_revision",
                    ScopeId = "tenant-a",
                    Compiled = true,
                }));

        store.UpsertCount.Should().Be(0);
        store.Stored.Should().NotContainKey("tenant_report");

        // A globally shared definition of the same name (empty ScopeId, e.g. a startup/file import)
        // is still materialized — the guard keys on scope ownership, not the workflow name.
        await projector.ProjectAsync(
            context,
            BuildDefinitionCommittedEnvelope(
                12,
                new BindWorkflowDefinitionEvent
                {
                    WorkflowName = "tenant_report",
                    WorkflowYaml = BuildDefinitionYaml("tenant_report"),
                    SourceKind = "repo",
                },
                new WorkflowState
                {
                    WorkflowName = "tenant_report",
                    WorkflowYaml = BuildDefinitionYaml("tenant_report"),
                    SourceKind = "repo",
                    Compiled = true,
                }));

        store.UpsertCount.Should().Be(1);
        store.Stored.Should().ContainKey("tenant_report");
        store.Stored["tenant_report"].Source.Should().Be("repo");
    }

    [Fact]
    public async Task WorkflowRunInsightReportArtifactProjector_ShouldTrackLifecycleReplyAndCompletionBranches()
    {
        var store = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(store, store, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                1,
                new WorkflowRunExecutionStartedEvent
                {
                    RunId = "run-1",
                    WorkflowName = "wf-1",
                    Input = "hello",
                    DefinitionActorId = "definition-1",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new StepRequestEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    StepType = "llm_call",
                    TargetRole = "assistant",
                    Parameters = { ["temperature"] = "0.2" },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = false,
                    Output = "partial",
                    Error = "boom",
                    WorkerId = "role-1",
                    NextStepId = "step-2",
                    BranchKey = "fallback",
                    AssignedVariable = "answer",
                    AssignedValue = "42",
                    Annotations = { ["token_usage"] = "99" },
                    Usage = new WorkflowUsageMetrics
                    {
                        PromptTokens = 11,
                        CompletionTokens = 13,
                        TotalTokens = 24,
                        Model = "gpt-5.4",
                        Cost = 0.31,
                        LatencyMs = 180,
                    },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRoleReplyRecordedEvent
                {
                    RunId = "run-1",
                    RoleActorId = "role-1",
                    RoleId = "",
                    SessionId = "session-1",
                    Content = "tool says hi",
                    ToolCalls =
                    {
                        new WorkflowRoleReplyToolCall
                        {
                            ToolName = "search",
                            CallId = "call-1",
                        },
                    },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                5,
                new WorkflowCompletedEvent
                {
                    WorkflowName = "wf-1",
                    Success = true,
                    Output = "done",
                    RunId = "run-1",
                },
                BuildState("completed", finalOutput: "done")));

        var report = store.Stored["actor-1"];
        report.WorkflowName.Should().Be("wf-1");
        report.Input.Should().Be("hello");
        report.Success.Should().BeTrue();
        report.FinalOutput.Should().Be("done");
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Completed);
        report.Steps.Should().ContainSingle();
        report.Steps[0].StepId.Should().Be("step-1");
        report.Steps[0].Success.Should().BeFalse();
        report.Steps[0].OutputPreview.Should().Be("partial");
        report.Steps[0].CompletionAnnotations.Should().ContainKey("token_usage");
        report.Steps[0].Usage.TotalTokens.Should().Be(24);
        report.Steps[0].Usage.Model.Should().Be("gpt-5.4");
        report.Usage.TotalTokens.Should().Be(24);
        report.Usage.PromptTokens.Should().Be(11);
        report.RoleReplies.Should().ContainSingle();
        report.RoleReplies[0].RoleId.Should().Be("role-1");
        report.Timeline.Select(x => x.Stage).Should().Contain([
            "workflow.start",
            "step.request",
            "step.failed",
            "role.reply",
            "tool.call",
            "workflow.completed",
        ]);
    }

    [Fact]
    public async Task WorkflowRunInsightReportArtifactProjector_ShouldSanitizePayloadDerivedReportFields()
    {
        var store = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(store, store, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                1,
                new WorkflowRunExecutionStartedEvent
                {
                    RunId = "run-1",
                    WorkflowName = "wf-1",
                    Input = $$"""{"prompt":"go","access_token":"{{AuditSentinel}}"}""",
                },
                BuildState("running", input: $$"""{"prompt":"state","token":"{{AuditSentinel}}"}""")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new StepRequestEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    StepType = "tool_call",
                    TargetRole = "assistant",
                    Parameters = { ["api_key"] = AuditSentinel, ["query"] = $"Bearer {AuditSentinel}" },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = false,
                    Output = $$"""{"answer":"partial","secret":"{{AuditSentinel}}"}""",
                    Error = $"failed token={AuditSentinel}",
                    AssignedVariable = "password",
                    AssignedValue = AuditSentinel,
                    Annotations = { ["authorization"] = $"Bearer {AuditSentinel}" },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRoleReplyRecordedEvent
                {
                    RunId = "run-1",
                    RoleActorId = "role-1",
                    SessionId = "session-1",
                    Content = $"reply Bearer {AuditSentinel}",
                    ToolCalls =
                    {
                        new WorkflowRoleReplyToolCall
                        {
                            ToolName = "search",
                            CallId = "call-1",
                            ArgumentsJson = $$"""{"api_key":"{{AuditSentinel}}"}""",
                            ResultJson = $$"""{"access_token":"{{AuditSentinel}}"}""",
                            Error = $"signature=sha256={new string('a', 16)}{AuditSentinel}",
                        },
                    },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                5,
                new WorkflowCompletedEvent
                {
                    WorkflowName = "wf-1",
                    Success = false,
                    Output = $$"""{"final":"no","token":"{{AuditSentinel}}"}""",
                    Error = $"Bearer {AuditSentinel}",
                    RunId = "run-1",
                },
                BuildState(
                    "failed",
                    finalOutput: $$"""{"state":"final","token":"{{AuditSentinel}}"}""",
                    finalError: $"state Bearer {AuditSentinel}")));

        var report = store.Stored["actor-1"];
        FlattenReportStrings(report).Should().NotContain(value => value.Contains(AuditSentinel, StringComparison.Ordinal));
        report.Input.Should().NotContain(AuditSentinel);
        report.Steps[0].RequestParameters["api_key"].Should().Be("[redacted]");
        report.Steps[0].AssignedValue.Should().Be("[redacted]");
        report.RoleReplies[0].ContentLength.Should().Be(report.RoleReplies[0].Content.Length);
    }


    [Fact]
    public async Task WorkflowRunInsightReportArtifactProjector_ShouldTrackSuspensionSignalAndStoppedBranches()
    {
        var store = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(store, store, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                1,
                new WorkflowSuspendedEvent
                {
                    RunId = "run-1",
                    StepId = "step-9",
                    SuspensionType = "wait_signal",
                    Prompt = "Need approval",
                    TimeoutSeconds = 30,
                    VariableName = "approval",
                    Metadata = { ["source"] = "user" },
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new WaitingForSignalEvent
                {
                    RunId = "run-1",
                    StepId = "step-9",
                    SignalName = "approve",
                    TimeoutMs = 120000,
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new WorkflowSignalBufferedEvent
                {
                    RunId = "run-1",
                    StepId = "step-9",
                    SignalName = "approve",
                    Payload = "{}",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRunStoppedEvent
                {
                    RunId = "run-1",
                    Reason = "manual-stop",
                },
                BuildState("stopped", finalError: "manual-stop")));

        var report = store.Stored["actor-1"];
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
        report.FinalError.Should().Be("manual-stop");
        report.Steps.Should().ContainSingle();
        report.Steps[0].SuspensionType.Should().Be("wait_signal");
        report.Steps[0].SuspensionPrompt.Should().Be("Need approval");
        report.Steps[0].SuspensionTimeoutSeconds.Should().Be(30);
        report.Timeline.Select(x => x.Stage).Should().Contain([
            "workflow.suspended",
            "signal.waiting",
            "signal.buffered",
            "workflow.stopped",
        ]);
    }

    [Fact]
    public async Task WorkflowRunInsightReportArtifactProjector_ShouldIgnoreInvalidEnvelope_AndMissingStateRoot()
    {
        var reportStore = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(reportStore, reportStore, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };

        await projector.ProjectAsync(context, new EventEnvelope());
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "outer-missing-state",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-17T11:00:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-missing-state",
                        Version = 6,
                        EventData = Any.Pack(new WorkflowCompletedEvent
                        {
                            WorkflowName = "wf-1",
                            RunId = "run-1",
                            Success = true,
                            Output = "done",
                        }),
                    },
                }),
            });

        reportStore.UpsertCount.Should().Be(0);
        graphWriter.UpsertCount.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowArtifactProjector_ShouldTrackStepAndTopologyEvents_AndSkipDuplicates()
    {
        var reportStore = new RecordingDocumentStore<WorkflowRunInsightReportDocument>(x => x.Id);
        var graphWriter = new RecordingGraphWriter<WorkflowRunInsightReportDocument>(x => x.Id);
        var projector = new WorkflowRunInsightReportArtifactProjector(reportStore, reportStore, graphWriter);
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-materialization",
        };
        var requestEnvelope = BuildCommittedEnvelope(
            1,
            new StepRequestEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                StepType = "tool_call",
                TargetRole = "assistant",
                Parameters = { ["query"] = "weather" },
            },
            BuildState("running"));

        await projector.ProjectAsync(context, requestEnvelope);
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                2,
                new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = true,
                    Output = "sunny",
                    WorkerId = "role-1",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                3,
                new WorkflowSuspendedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    SuspensionType = "human_input",
                    Prompt = "confirm",
                    TimeoutSeconds = 15,
                    VariableName = "answer",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                4,
                new WorkflowRoleActorLinkedEvent
                {
                    RunId = "run-1",
                    RoleId = "assistant",
                    ChildActorId = "role-actor-1",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                5,
                new SubWorkflowBindingUpsertedEvent
                {
                    WorkflowName = "sub-flow",
                    ChildActorId = "child-run-1",
                },
                BuildState("running")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                5,
                new SubWorkflowBindingUpsertedEvent
                {
                    WorkflowName = "sub-flow",
                    ChildActorId = "child-run-1",
                },
                BuildState("running"),
                eventId: "evt-5"));

        reportStore.UpsertCount.Should().Be(5);
        graphWriter.UpsertCount.Should().Be(5);
        reportStore.Stored["actor-1"].Timeline.Select(x => x.Stage).Should().Contain(["step.request", "step.completed"]);
        graphWriter.Stored["actor-1"].Steps.Should().ContainSingle();
        graphWriter.Stored["actor-1"].Steps[0].TargetRole.Should().Be("assistant");
        graphWriter.Stored["actor-1"].Steps[0].SuspensionType.Should().Be("human_input");
        graphWriter.Stored["actor-1"].Topology.Select(x => x.Child).Should().Contain(["role-actor-1", "child-run-1"]);
        reportStore.Stored["actor-1"].Timeline.Select(x => x.Stage).Should().Contain(["step.request", "step.completed", "workflow.suspended"]);
    }

    [Fact]
    public void WorkflowMaterializationLeases_And_Codecs_ShouldCoverLifecycleBranches()
    {
        var materializationLease = new WorkflowExecutionMaterializationRuntimeLease(new WorkflowExecutionMaterializationContext
        {
            RootActorId = "actor-2",
            ProjectionKind = "workflow-execution-materialization",
        });
        var bindingLease = new WorkflowBindingRuntimeLease(new WorkflowBindingProjectionContext
        {
            RootActorId = "actor-2",
            ProjectionKind = "workflow-binding",
        });
        materializationLease.Context.RootActorId.Should().Be("actor-2");
        bindingLease.Context.ProjectionKind.Should().Be("workflow-binding");

        var bindingCodec = new WorkflowBindingSessionEventCodec();
        bindingCodec.Channel.Should().Be("workflow-binding");
        var bindingEnvelope = new EventEnvelope
        {
            Id = "binding-1",
            Payload = Any.Pack(new StringValue { Value = "binding" }),
        };
        var payload = bindingCodec.Serialize(bindingEnvelope);
        bindingCodec.Deserialize(bindingCodec.GetEventType(bindingEnvelope), payload)!.Id.Should().Be("binding-1");
        bindingCodec.Deserialize("mismatch", payload).Should().BeNull();

        var runCodec = new WorkflowRunEventSessionCodec();
        var runEnvelope = new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload { Name = "evt" },
        };
        runCodec.Deserialize(runCodec.GetEventType(runEnvelope), runCodec.Serialize(runEnvelope))!.Custom.Name.Should().Be("evt");
        runCodec.Deserialize(string.Empty, ByteString.Empty).Should().BeNull();
    }

    private static EventEnvelope BuildCommittedEnvelope(
        long version,
        IMessage payload,
        WorkflowRunState state,
        string? eventId = null)
    {
        var timestamp = DateTimeOffset.Parse($"2026-03-17T10:{version:00}:00+00:00");
        return new EventEnvelope
        {
            Id = $"outer-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId ?? $"evt-{version}",
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(timestamp),
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private static EventEnvelope BuildDefinitionCommittedEnvelope(
        long version,
        IMessage payload,
        WorkflowState state)
    {
        var timestamp = DateTimeOffset.Parse($"2026-03-17T11:{version:00}:00+00:00");
        return new EventEnvelope
        {
            Id = $"definition-outer-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = $"definition-evt-{version}",
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(timestamp),
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private static string BuildDefinitionYaml(string name) =>
        $"""
        name: {name}
        description: Bootstrap runtime.
        roles:
          - id: operator
            name: Operator
            system_prompt: ""
        steps:
          - id: bootstrap
            type: assign
            parameters:
              target: result
              value: "ok"
        """;

    private static string BuildComplexDefinitionYaml(string name) =>
        $"""
        name: {name}
        description: Complex runtime.
        roles:
          - id: operator
            name: Operator
            system_prompt: "Operate."
            provider: openai
            model: gpt-test
            temperature: 0.2
            max_tokens: 128
            max_tool_rounds: 2
            max_history_messages: 3
            event_modules: "audit, trace, audit"
            event_routes: "route:*"
            connectors:
              - aevatar_cli
        steps:
          - id: decide
            type: conditional
            parameters:
              condition: "ready"
            branches:
              true:
                next: call_connector
              false:
                next: fanout
          - id: call_connector
            type: connector_call
            target_role: operator
            parameters:
              connector: mcp_tools
              operation: search
            next: call_child
          - id: call_child
            type: workflow_call
            workflow: child_workflow
            next: fanout
          - id: fanout
            type: foreach
            sub_step_type: llm_call
            children:
              - id: child_llm
                type: llm_call
                target_role: operator
                prompt: "Summarize."
        """;

    private static WorkflowRunState BuildState(
        string status,
        string runId = "run-1",
        string input = "hello",
        string finalOutput = "",
        string finalError = "") =>
        new()
        {
            RunId = runId,
            WorkflowName = "wf-1",
            LastCommandId = "cmd-1",
            DefinitionActorId = "definition-1",
            Status = status,
            Input = input,
            FinalOutput = finalOutput,
            FinalError = finalError,
            Compiled = true,
        };

    private static IReadOnlyList<string> FlattenReportStrings(WorkflowRunInsightReportDocument report)
    {
        var values = new List<string>
        {
            report.Input,
            report.FinalOutput,
            report.FinalError,
        };

        foreach (var step in report.Steps)
        {
            values.Add(step.OutputPreview);
            values.Add(step.Error);
            values.Add(step.AssignedValue);
            values.AddRange(step.RequestParameters.Values);
            values.AddRange(step.CompletionAnnotations.Values);
        }

        foreach (var reply in report.RoleReplies)
            values.Add(reply.Content);

        foreach (var timelineEvent in report.Timeline)
        {
            values.Add(timelineEvent.Message);
            values.AddRange(timelineEvent.Data.Values);
        }

        return values;
    }

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingDocumentStore<TReadModel>
        : IProjectionDocumentReader<TReadModel, string>,
          IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        private readonly Func<TReadModel, string> _keySelector;

        public RecordingDocumentStore(Func<TReadModel, string> keySelector)
        {
            _keySelector = keySelector;
        }

        public Dictionary<string, TReadModel> Stored { get; } = new(StringComparer.Ordinal);

        public int UpsertCount { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            Stored[_keySelector(readModel)] = readModel;
            UpsertCount++;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            var removed = Stored.Remove(id);
            return Task.FromResult(removed
                ? ProjectionWriteResult.Applied()
                : ProjectionWriteResult.Duplicate());
        }

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            Stored.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingGraphWriter<TReadModel> : IProjectionGraphWriter<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        private readonly Func<TReadModel, string> _keySelector;

        public RecordingGraphWriter(Func<TReadModel, string> keySelector)
        {
            _keySelector = keySelector;
        }

        public Dictionary<string, TReadModel> Stored { get; } = new(StringComparer.Ordinal);

        public int UpsertCount { get; private set; }

        public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Stored[_keySelector(readModel)] = readModel;
            UpsertCount++;
            return Task.CompletedTask;
        }
    }

}
