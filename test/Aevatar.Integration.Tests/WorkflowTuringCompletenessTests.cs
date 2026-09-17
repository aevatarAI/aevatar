using System.Net;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Integration.AI;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowCallerCredential = Aevatar.Workflow.Abstractions.WorkflowCallerCredential;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowTuringCompleteness")]
public sealed class WorkflowTuringCompletenessTests : WorkflowGAgentTestBase
{
    [Fact]
    public async Task IncDecJzProgram_ShouldTransferCounterValueInClosedWorldMode()
    {
        var workflow = BuildCounterTransferWorkflow();
        WorkflowValidator.Validate(workflow).Should().BeEmpty();

        var completed = await ExecuteClosedWorldWorkflowAsync(workflow, maxTransitions: 256);

        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("2");
    }

    [Fact]
    public async Task TwoCounterProgram_ShouldComputeAdditionInClosedWorldMode()
    {
        var workflow = BuildCounterAdditionWorkflow();
        WorkflowValidator.Validate(workflow).Should().BeEmpty();

        var completed = await ExecuteClosedWorldWorkflowAsync(workflow, maxTransitions: 512);

        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("5");
    }

    [Fact]
    public async Task NonHaltingProgram_ShouldExceedTransitionBudget()
    {
        var workflow = BuildNonHaltingWorkflow();
        WorkflowValidator.Validate(workflow).Should().BeEmpty();

        Func<Task> run = async () => await ExecuteClosedWorldWorkflowAsync(workflow, maxTransitions: 64);
        await run.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task ToolCallFailure_ShouldTerminateWorkflowAsFailed()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "tool_failure",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "call_service",
                    Type = "tool_call",
                    Parameters = new Dictionary<string, string>
                    {
                        ["tool"] = "failing_tool",
                    },
                },
            ],
        };
        var toolCallModule = new ToolCallModule(
            [new SingleToolSource(new FailingWorkflowTool())],
            NullLogger<ToolCallModule>.Instance);

        var completed = await ExecuteClosedWorldWorkflowAsync(
            workflow,
            maxTransitions: 16,
            toolCallModule);

        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("NYXID_PROXY_HTTP_503");
        completed.Error.Should().Contain("The service request failed.");
    }

    [Fact]
    public async Task NyxIdMissingAdmission_ShouldRemainFailedThroughSchedulingProjectionAndSse()
    {
        const string arguments =
            """{"slug":"home-assistant-q1000","path":"/q1000","method":"GET"}""";
        var workflow = new WorkflowDefinition
        {
            Name = "nyxid_failure",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "call_service",
                    Type = "tool_call",
                    Next = "report_q1000",
                    Parameters = new Dictionary<string, string>
                    {
                        ["tool"] = "nyxid_proxy",
                        ["arguments"] = arguments,
                    },
                },
                new StepDefinition
                {
                    Id = "report_q1000",
                    Type = "transform",
                },
            ],
        };
        var requestHandler = new CountingHandler();
        var nyxIdTool = new NyxIdProxyTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(requestHandler)));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(nyxIdTool)],
            new AdmittedAgentToolExecutor(
                AlwaysStartingAgentToolAdmissionLedger.Instance,
                new AlwaysAppendingAuditTrailAppender(),
                new StableAuditActorIdentityHasher()));
        var toolCallModule = new ToolCallModule([adapter], NullLogger<ToolCallModule>.Instance);
        var requestedStepIds = new List<string>();

        var completed = await ExecuteClosedWorldWorkflowAsync(
            workflow,
            maxTransitions: 16,
            toolCallModule,
            requestedStepIds,
            new WorkflowCallerCredential { BearerToken = "user-token" });

        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("EXTERNAL_CAPABILITY_CALL_SITE_NOT_ADMITTED");
        requestedStepIds.Should().ContainSingle().Which.Should().Be("call_service");
        requestedStepIds.Should().NotContain("report_q1000");
        requestHandler.RequestCount.Should().Be(0);

        var committedPublisher = new RecordingEventPublisher();
        var runAgent = CreateRunAgent();
        SetAgentId(runAgent, "workflow-run-nyxid-failure");
        runAgent.EventPublisher = committedPublisher;
        runAgent.CommittedStateEventPublisher = committedPublisher;
        await BindInteractiveWorkflowRunDefinitionAsync(
            runAgent,
            "definition-nyxid-failure",
            """
            name: nyxid_failure
            steps:
              - id: call_service
                type: tool_call
            """,
            workflow.Name,
            runId: completed.RunId);
        await runAgent.HandleWorkflowCompleted(completed);

        var committed = committedPublisher.Published
            .Select(static publication => publication.evt)
            .OfType<CommittedStateEventPublished>()
            .Single(static publication =>
                publication.StateEvent.EventData.Is(WorkflowCompletedEvent.Descriptor));
        var committedEnvelope = new EventEnvelope
        {
            Id = committed.StateEvent.EventId,
            Timestamp = committed.StateEvent.Timestamp?.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(runAgent.Id),
            Payload = Any.Pack(committed),
        };

        var reportStore = new RecordingReportStore();
        var reportProjector = new WorkflowRunInsightReportArtifactProjector(
            reportStore,
            reportStore);
        await reportProjector.ProjectAsync(
            new WorkflowExecutionMaterializationContext
            {
                RootActorId = runAgent.Id,
                ProjectionKind = "workflow-execution",
            },
            committedEnvelope);

        reportStore.Document.Should().NotBeNull();
        reportStore.Document!.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Failed);
        reportStore.Document.Success.Should().BeFalse();

        var sseFrames = new EventEnvelopeToWorkflowRunEventMapper(
            [new WorkflowCompletedRunEventEnvelopeMappingHandler()])
            .Map(committedEnvelope);

        sseFrames.Should().ContainSingle();
        sseFrames[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunError);
        sseFrames.Should().NotContain(static frame =>
            frame.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished);
    }

    private static WorkflowDefinition BuildCounterTransferWorkflow() =>
        new()
        {
            Name = "counter_transfer",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "init_c1",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "c1",
                        ["value"] = "2",
                    },
                },
                new StepDefinition
                {
                    Id = "init_c2",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "c2",
                        ["value"] = "0",
                    },
                },
                new StepDefinition
                {
                    Id = "check_c1",
                    Type = "conditional",
                    Parameters = new Dictionary<string, string>
                    {
                        ["condition"] = "${eq(variables.c1, '0')}",
                    },
                    Branches = new Dictionary<string, string>
                    {
                        ["true"] = "halt",
                        ["false"] = "dec_c1",
                    },
                },
                new StepDefinition
                {
                    Id = "dec_c1",
                    Type = "assign",
                    Next = "inc_c2",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "c1",
                        ["value"] = "${sub(variables.c1, 1)}",
                    },
                },
                new StepDefinition
                {
                    Id = "inc_c2",
                    Type = "assign",
                    Next = "check_c1",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "c2",
                        ["value"] = "${add(variables.c2, 1)}",
                    },
                },
                new StepDefinition
                {
                    Id = "halt",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "result",
                        ["value"] = "${variables.c2}",
                    },
                },
            ],
        };

    private static WorkflowDefinition BuildCounterAdditionWorkflow() =>
        new()
        {
            Name = "counter_addition",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "init_a",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "a",
                        ["value"] = "2",
                    },
                },
                new StepDefinition
                {
                    Id = "init_b",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "b",
                        ["value"] = "3",
                    },
                },
                new StepDefinition
                {
                    Id = "check_b",
                    Type = "conditional",
                    Parameters = new Dictionary<string, string>
                    {
                        ["condition"] = "${eq(variables.b, '0')}",
                    },
                    Branches = new Dictionary<string, string>
                    {
                        ["true"] = "halt",
                        ["false"] = "inc_a",
                    },
                },
                new StepDefinition
                {
                    Id = "inc_a",
                    Type = "assign",
                    Next = "dec_b",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "a",
                        ["value"] = "${add(variables.a, 1)}",
                    },
                },
                new StepDefinition
                {
                    Id = "dec_b",
                    Type = "assign",
                    Next = "check_b",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "b",
                        ["value"] = "${sub(variables.b, 1)}",
                    },
                },
                new StepDefinition
                {
                    Id = "halt",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "result",
                        ["value"] = "${variables.a}",
                    },
                },
            ],
        };

    private static WorkflowDefinition BuildNonHaltingWorkflow() =>
        new()
        {
            Name = "non_halting",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "loop",
                    Type = "conditional",
                    Parameters = new Dictionary<string, string>
                    {
                        ["condition"] = "false",
                    },
                    Branches = new Dictionary<string, string>
                    {
                        ["true"] = "halt",
                        ["false"] = "loop",
                    },
                },
                new StepDefinition
                {
                    Id = "halt",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "result",
                        ["value"] = "done",
                    },
                },
            ],
        };

    private static async Task<WorkflowCompletedEvent> ExecuteClosedWorldWorkflowAsync(
        WorkflowDefinition workflow,
        int maxTransitions,
        IEventModule<IWorkflowExecutionContext>? toolCallModule = null,
        ICollection<string>? requestedStepIds = null,
        WorkflowCallerCredential? callerCredential = null)
    {
        var loop = new WorkflowLoopModule();
        loop.SetWorkflow(workflow);

        var modules = new Dictionary<string, IEventModule<IWorkflowExecutionContext>>(StringComparer.OrdinalIgnoreCase)
        {
            ["assign"] = new AssignModule(),
            ["conditional"] = new ConditionalModule(),
            ["switch"] = new SwitchModule(),
            ["transform"] = new TransformModule(),
            ["while"] = new WhileModule(),
        };
        if (toolCallModule != null)
            modules["tool_call"] = toolCallModule;

        var queue = new Queue<IMessage>();
        var workflowRunAgent = new TestWorkflowRunAgent("workflow-turing-proof-agent", "proof-run");
        if (callerCredential != null)
        {
            await workflowRunAgent.UpdateExecutionContextAsync(new WorkflowRunExecutionContextDelta
            {
                CallerCredential = callerCredential,
            });
        }
        queue.Enqueue(new StartWorkflowEvent
        {
            RunId = "proof-run",
            Input = "seed",
        });

        var transitions = 0;
        while (queue.Count > 0 && transitions < maxTransitions)
        {
            transitions++;
            var message = queue.Dequeue();
            if (message is not StartWorkflowEvent && message is not StepCompletedEvent)
                continue;

            var loopCtx = CreateContext(workflowRunAgent);
            await loop.HandleAsync(Envelope(message), loopCtx, CancellationToken.None);
            foreach (var (evt, _) in loopCtx.Published)
            {
                switch (evt)
                {
                    case WorkflowCompletedEvent completed:
                        return completed;
                    case StepCompletedEvent completedStep:
                        queue.Enqueue(completedStep);
                        break;
                    case StepRequestEvent request:
                    {
                        requestedStepIds?.Add(request.StepId);
                        var completedStep = await ExecuteStepAsync(request, modules, workflowRunAgent);
                        queue.Enqueue(completedStep);
                        break;
                    }
                }
            }
        }

        throw new TimeoutException($"Workflow did not complete within transition budget ({maxTransitions}).");
    }

    private static async Task<StepCompletedEvent> ExecuteStepAsync(
        StepRequestEvent request,
        IReadOnlyDictionary<string, IEventModule<IWorkflowExecutionContext>> modules,
        TestWorkflowRunAgent workflowRunAgent)
    {
        if (!modules.TryGetValue(request.StepType, out var module))
            throw new InvalidOperationException($"No closed-world executor for step type '{request.StepType}'.");

        var ctx = CreateContext(workflowRunAgent);
        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        if (module is ToolCallModule toolCallModule)
        {
            await WorkflowCoreModuleTestBase.DrainToolCallContinuationsAsync(
                toolCallModule,
                request,
                ctx);
        }

        return ctx.GetPublishedSnapshot().Select(static item => item.evt).OfType<StepCompletedEvent>().Single();
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("workflow-turing-test", TopologyAudience.Self),
        };
    }

    private static TestEventHandlerContext CreateContext(TestWorkflowRunAgent workflowRunAgent)
    {
        return new TestEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            workflowRunAgent,
            NullLogger.Instance);
    }

    private sealed class FailingWorkflowTool : IWorkflowTool
    {
        public string Name => "failing_tool";

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(WorkflowToolExecutionResult.Failed(
                """{"error":true,"status":503}""",
                "NYXID_PROXY_HTTP_503",
                "The service request failed."));
        }
    }

    private sealed class SingleAgentToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class AlwaysAppendingAuditTrailAppender : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingReportStore :
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>,
        IProjectionWriteDispatcher<WorkflowRunInsightReportDocument>,
        IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string>,
        IProjectionGraphWriter<WorkflowRunInsightReportDocument>
    {
        public WorkflowRunInsightReportDocument? Document { get; private set; }

        public Task<WorkflowRunInsightReportDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Document);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowRunInsightReportDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This regression reads a single projected run by id.");

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkflowRunInsightReportDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Document = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException("This regression does not delete projected runs.");

        public Task<ProjectionDocumentMutationResult<WorkflowRunInsightReportDocument>> MutateAsync(
            string key,
            Func<WorkflowRunInsightReportDocument?, WorkflowRunInsightReportDocument> reducer,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var incoming = reducer(Document?.Clone());
            var result = ProjectionWriteResultEvaluator.Evaluate(Document, incoming);
            if (result.IsApplied)
                Document = incoming.Clone();

            return Task.FromResult(new ProjectionDocumentMutationResult<WorkflowRunInsightReportDocument>(
                result,
                Document?.Clone()));
        }

        Task IProjectionGraphWriter<WorkflowRunInsightReportDocument>.UpsertAsync(
            WorkflowRunInsightReportDocument readModel,
            string projectionKind,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class SingleToolSource(IWorkflowTool tool) : IWorkflowToolSource
    {
        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IWorkflowTool>>([tool]);
        }
    }
}
