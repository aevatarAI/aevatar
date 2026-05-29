using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Workflow.Application.Abstractions.Queries;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ChatRunToolCompletionCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_WaitCompleteTeam_ShouldFoldToolResultReturnedByDispatcher()
    {
        var harness = new Harness();
        var coordinator = harness.CreateCoordinator();
        var toolCall = new ToolCall
        {
            Id = "call_team",
            Name = "aevatar_invoke_team",
            ArgumentsJson = """{"team_id":"team-1","endpoint_id":"entry","wait":"complete"}""",
        };

        var result = await coordinator.ExecuteAsync(
            BuildRequest(),
            toolCall,
            toolCall.ArgumentsJson,
            (request, _) => Task.FromResult(request with
            {
                ToolExecutionResultJson = """{"opaque":"llm-facing"}""",
                RunId = "team-command",
                Status = "RunFinished",
                CompletionResultJson = """
                    {
                      "completion_status": "RunFinished",
                      "completion_observed": true,
                      "events": [{ "type": "run_finished" }]
                    }
                    """,
                ServiceId = "service-1",
                EndpointId = "entry",
                WaitMode = ChatRunSubRunWaitMode.Complete,
                CompletionObserved = true,
            }),
            llmRound: 1);

        using var resultDocument = System.Text.Json.JsonDocument.Parse(result);
        resultDocument.RootElement.GetProperty("completion_observed").GetBoolean().Should().BeTrue();
        resultDocument.RootElement.GetProperty("completion_status").GetString().Should().Be("RunFinished");
        result.Should().NotContain("completion_not_observed");
        harness.ChatRunPort.ObservedTerminals.Should().ContainSingle().Which.Should().Match<ChatRunSubRunTerminalObserved>(
            terminal => terminal.RunId == "team-command" &&
                        terminal.Status == "RunFinished" &&
                        terminal.ServiceId == "service-1" &&
                        terminal.EndpointId == "entry" &&
                        terminal.InternalResultJson.Contains("\"completion_observed\": true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WaitCompleteWorkflow_WhenReadModelIsNotTerminal_ShouldNotSynthesizeCompletion()
    {
        var harness = new Harness();
        harness.WorkflowQuery.Snapshot = new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor",
            LastCommandId = "wf-command",
            CompletionStatus = WorkflowRunCompletionStatus.Running,
        };
        var coordinator = harness.CreateCoordinator();
        var toolCall = new ToolCall
        {
            Id = "call_workflow",
            Name = "aevatar_start_workflow",
            ArgumentsJson = """{"workflow_id":"wf-main","wait":"complete"}""",
        };

        var result = await coordinator.ExecuteAsync(
            BuildRequest(),
            toolCall,
            toolCall.ArgumentsJson,
            (request, _) => Task.FromResult(request with
            {
                ToolExecutionResultJson = """{"opaque":"workflow-dispatch"}""",
                RunId = "wf-command",
                Status = "streaming",
                StreamTopic = "aevatar://actors/workflow-actor/runs/wf-command",
                ActorId = "workflow-actor",
                WaitMode = ChatRunSubRunWaitMode.Complete,
            }),
            llmRound: 1);

        result.Should().Contain("completion_not_observed");
        result.Should().Contain("\"status\":\"streaming\"");
        result.Should().NotContain("\"status\":\"completed\"");
        harness.WorkflowQuery.LastActorId.Should().Be("workflow-actor");
        harness.ChatRunPort.ObservedTerminals.Should().ContainSingle().Which.Should().Match<ChatRunSubRunTerminalObserved>(
            terminal => terminal.RunId == "wf-command" &&
                        terminal.Status == "completion_not_observed" &&
                        terminal.InternalResultJson.Contains("completion_not_observed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WaitCompleteGAgentResolvedByName_ShouldStartObservationAfterActorIdIsKnown()
    {
        var harness = new Harness();
        harness.TerminalQuery.ByCorrelationId = new GAgentRunTerminalSnapshot(
            "actor-from-name",
            "session-1",
            "call_gagent_name",
            GAgentRunTerminalInteractionKind.DraftRun,
            GAgentRunTerminalStatus.TextMessageCompleted,
            "done",
            "resolved actor completed",
            7,
            "event-7",
            DateTimeOffset.UtcNow);
        var coordinator = harness.CreateCoordinator();
        var toolCall = new ToolCall
        {
            Id = "call_gagent_name",
            Name = "aevatar_invoke_gagent",
            ArgumentsJson = """{"actor_name":"RoleGAgent","payload":{"prompt":"hi"},"wait":"complete"}""",
        };

        var result = await coordinator.ExecuteAsync(
            BuildRequest(),
            toolCall,
            toolCall.ArgumentsJson,
            (request, _) => Task.FromResult(request with
            {
                ToolExecutionResultJson = """{"opaque":"gagent-dispatch"}""",
                RunId = "call_gagent_name",
                Status = "streaming",
                StreamTopic = "aevatar://actors/actor-from-name/runs/call_gagent_name",
                ActorId = "actor-from-name",
                WaitMode = ChatRunSubRunWaitMode.Complete,
            }),
            llmRound: 1);

        result.Should().Contain("resolved actor completed");
        harness.TerminalQuery.LastActorId.Should().Be("actor-from-name");
        harness.TerminalQuery.LastCorrelationId.Should().Be("call_gagent_name");
        harness.ChatRunPort.ObservationRequests.Should().HaveCount(2);
        harness.ChatRunPort.ObservationRequests[0].ToolExecutionResultJson.Should().BeEmpty();
        harness.ChatRunPort.ObservationRequests[1].ToolExecutionResultJson.Should().Be("""{"opaque":"gagent-dispatch"}""");
        harness.ChatRunPort.ObservationRequests[1].ActorId.Should().Be("actor-from-name");
        harness.ChatRunPort.ObservedTerminals.Should().ContainSingle().Which.ActorId.Should().Be("actor-from-name");
    }

    [Fact]
    public async Task ExecuteAsync_WaitCompleteGAgent_WhenOnlyReadyNotificationArrives_ShouldReturnInternalResultJson()
    {
        var harness = new Harness();
        var coordinator = harness.CreateCoordinator();
        var toolCall = new ToolCall
        {
            Id = "call_gagent_ready",
            Name = "aevatar_invoke_gagent",
            ArgumentsJson = """{"actor_id":"actor-ready","payload":{"prompt":"hi"},"wait":"complete"}""",
        };
        const string foldedPayload = """{"run_id":"run-ready","content":"ready payload"}""";

        var result = await coordinator.ExecuteAsync(
            BuildRequest(),
            toolCall,
            toolCall.ArgumentsJson,
            async (request, _) =>
            {
                await harness.PublishReadyAsync(new ChatRunToolResultReady
                {
                    ResponseId = request.ResponseId,
                    RunId = "run-ready",
                    CallerToolCallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    InternalResultJson = foldedPayload,
                    LlmRound = request.LlmRound,
                    Status = "RunFinished",
                    ActorId = "actor-ready",
                    CompletionObserved = true,
                });
                return request with
                {
                    ToolExecutionResultJson = """{"opaque":"gagent-dispatch"}""",
                    RunId = "run-ready",
                    Status = "streaming",
                    StreamTopic = "aevatar://actors/actor-ready/runs/run-ready",
                    ActorId = "actor-ready",
                    WaitMode = ChatRunSubRunWaitMode.Complete,
                };
            },
            llmRound: 2);

        result.Should().Be(foldedPayload);
        harness.ChatRunPort.ObservedTerminals.Should().BeEmpty();
        harness.ChatRunPort.SubmittedToolCalls.Should().ContainSingle()
            .Which.ToolExecutionResultJson.Should().Be("""{"opaque":"gagent-dispatch"}""");
    }

    [Fact]
    public async Task ExecuteAsync_WaitCompleteTeam_WhenReadModelIsTerminal_ShouldReturnObservedInternalResultJson()
    {
        var harness = new Harness();
        harness.ServiceRunQuery.ByRunId = new ServiceRunSnapshot(
            "scope-1",
            "service-1",
            "service-key",
            "team-run",
            "command-1",
            "correlation-1",
            "entry",
            ServiceImplementationKind.Static,
            "target-actor",
            "revision-1",
            "deployment-1",
            ServiceRunStatus.Completed,
            "actor-1",
            "tenant-1",
            "app-1",
            "namespace-1",
            7,
            "event-7",
            DateTimeOffset.Parse("2026-05-23T00:00:00+00:00"),
            DateTimeOffset.Parse("2026-05-23T00:01:00+00:00"));
        var coordinator = harness.CreateCoordinator();
        var toolCall = new ToolCall
        {
            Id = "call_team_readmodel",
            Name = "aevatar_invoke_team",
            ArgumentsJson = """{"team_id":"team-1","endpoint_id":"entry","wait":"complete"}""",
        };

        var result = await coordinator.ExecuteAsync(
            BuildRequest(),
            toolCall,
            toolCall.ArgumentsJson,
            (request, _) => Task.FromResult(request with
            {
                ToolExecutionResultJson = """{"opaque":"team-dispatch"}""",
                RunId = "team-run",
                Status = "running",
                ServiceId = "service-1",
                EndpointId = "entry",
                ScopeId = "scope-1",
                WaitMode = ChatRunSubRunWaitMode.Complete,
            }),
            llmRound: 1);

        var observed = harness.ChatRunPort.ObservedTerminals.Should().ContainSingle().Subject;
        observed.Should().Match<ChatRunSubRunTerminalObserved>(
            terminal => terminal.RunId == "team-run" &&
                        terminal.Status == ServiceRunStatus.Completed.ToString() &&
                        terminal.InternalResultJson.Contains("\"service_key\":\"service-key\"", StringComparison.Ordinal));
        result.Should().Be(observed.InternalResultJson);
    }

    private static LLMRequest BuildRequest() =>
        new()
        {
            Messages = [ChatMessage.User("hello")],
            RequestId = "request-1",
            CallerContext = new LLMRequestCallerContext("scope-1", "owner-1", "resp_1"),
            Model = "test-model",
        };

    private sealed class Harness
    {
        private readonly RecordingSubscriptionProvider _subscriptionProvider = new();
        public RecordingChatRunActorPort ChatRunPort { get; }
        public RecordingServiceRunQueryPort ServiceRunQuery { get; } = new();
        public RecordingTerminalQueryPort TerminalQuery { get; } = new();
        public RecordingWorkflowQueryService WorkflowQuery { get; } = new();

        public Harness()
        {
            ChatRunPort = new RecordingChatRunActorPort(_subscriptionProvider);
        }

        public ChatRunToolCompletionCoordinator CreateCoordinator() =>
            new(
                ChatRunPort,
                _subscriptionProvider,
                TerminalQuery,
                ServiceRunQuery,
                WorkflowQuery);

        public Task PublishReadyAsync(ChatRunToolResultReady ready) =>
            _subscriptionProvider.PublishReadyAsync(ChatRunPort.ActorId, ready);
    }

    private sealed class RecordingChatRunActorPort(RecordingSubscriptionProvider subscriptionProvider)
        : IChatRunActorPort
    {
        public string ActorId { get; } = "chat-run:resp_1";
        public ChatRunStartRequest? StartRequest { get; private set; }
        public List<ChatRunToolCompletionRequest> SubmittedToolCalls { get; } = [];
        public List<ChatRunToolCompletionRequest> ObservationRequests { get; } = [];
        public List<ChatRunSubRunTerminalObserved> ObservedTerminals { get; } = [];

        public Task<string> StartAsync(ChatRunStartRequest request, CancellationToken ct = default)
        {
            StartRequest = request;
            return Task.FromResult(ActorId);
        }

        public Task SubmitToolCallAsync(
            string chatRunActorId,
            ChatRunToolCompletionRequest request,
            CancellationToken ct = default)
        {
            SubmittedToolCalls.Add(request);
            return Task.CompletedTask;
        }

        public Task BeginSubRunObservationAsync(
            string chatRunActorId,
            ChatRunToolCompletionRequest request,
            CancellationToken ct = default)
        {
            ObservationRequests.Add(request);
            return Task.CompletedTask;
        }

        public async Task ObserveSubRunTerminalAsync(
            string chatRunActorId,
            ChatRunSubRunTerminalObserved observed,
            CancellationToken ct = default)
        {
            ObservedTerminals.Add(observed.Clone());
            var request = ObservationRequests.LastOrDefault() ?? SubmittedToolCalls.Last();
            await subscriptionProvider.PublishReadyAsync(
                chatRunActorId,
                new ChatRunToolResultReady
                {
                    ResponseId = StartRequest?.ResponseId ?? string.Empty,
                    RunId = observed.RunId,
                    CallerToolCallId = request.ToolCall.Id,
                    ToolName = request.ToolCall.Name,
                    InternalResultJson = observed.InternalResultJson,
                    LlmRound = request.LlmRound,
                    Status = observed.Status,
                    ActorId = observed.ActorId,
                    ServiceId = observed.ServiceId,
                    EndpointId = observed.EndpointId,
                    CompletionObserved = observed.CompletionObserved,
                });
        }

        public Task TerminateAsync(
            string chatRunActorId,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingSubscriptionProvider : IActorEventSubscriptionProvider
    {
        private Func<ChatRunToolResultReady, Task>? _handler;

        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            if (typeof(TMessage) != typeof(ChatRunToolResultReady))
                throw new NotSupportedException(typeof(TMessage).FullName);

            _handler = ready => handler((TMessage)(object)ready);
            return Task.FromResult<IAsyncDisposable>(new Subscription(this));
        }

        public Task PublishReadyAsync(string actorId, ChatRunToolResultReady ready) =>
            _handler?.Invoke(ready) ?? Task.CompletedTask;

        private sealed class Subscription(RecordingSubscriptionProvider owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner._handler = null;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingServiceRunQueryPort : IServiceRunQueryPort
    {
        public ServiceRunSnapshot? ByRunId { get; set; }

        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceRunSnapshot>>([]);

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default) =>
            Task.FromResult(ByRunId);

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRunSnapshot?>(null);
    }

    private sealed class RecordingTerminalQueryPort : IGAgentRunTerminalQueryPort
    {
        public GAgentRunTerminalSnapshot? ByCorrelationId { get; set; }
        public string? LastActorId { get; private set; }
        public string? LastCorrelationId { get; private set; }

        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default)
        {
            LastActorId = actorId;
            LastCorrelationId = correlationId;
            return Task.FromResult(ByCorrelationId);
        }

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentRunTerminalSnapshot?>(null);
    }

    private sealed class RecordingWorkflowQueryService : IWorkflowExecutionQueryApplicationService
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;
        public string? LastActorId { get; private set; }
        public WorkflowActorSnapshot? Snapshot { get; set; }

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

        public IReadOnlyList<string> ListWorkflows() => [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowCatalogItem>>([]);

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult<WorkflowCatalogItemDetail?>(null);

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCapabilitiesDocument());

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            LastActorId = actorId;
            return Task.FromResult(Snapshot);
        }

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowRunReport?>(null);

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
            string actorId,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunTimelineExportItem>>([]);

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunGraphExportEdge>>([]);

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowRunGraphExportSubgraph());
    }
}
