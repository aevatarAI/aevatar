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
            (_, _) => Task.FromResult("""
                {
                  "run_id": "team-command",
                  "status": "RunFinished",
                  "result": {
                    "completion_status": "RunFinished",
                    "completion_observed": true,
                    "events": [{ "type": "run_finished" }]
                  },
                  "service_id": "service-1",
                  "endpoint_id": "entry",
                  "wait": "complete"
                }
                """),
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
                        terminal.ResultJson.Contains("\"completion_observed\": true", StringComparison.Ordinal));
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
            (_, _) => Task.FromResult("""
                {
                  "run_id": "wf-command",
                  "status": "streaming",
                  "stream_topic": "aevatar://actors/workflow-actor/runs/wf-command",
                  "actor_id": "workflow-actor",
                  "wait": "complete"
                }
                """),
            llmRound: 1);

        result.Should().Contain("completion_not_observed");
        result.Should().Contain("\"status\":\"streaming\"");
        result.Should().NotContain("\"status\":\"completed\"");
        harness.WorkflowQuery.LastActorId.Should().Be("workflow-actor");
        harness.ChatRunPort.ObservedTerminals.Should().ContainSingle().Which.Should().Match<ChatRunSubRunTerminalObserved>(
            terminal => terminal.RunId == "wf-command" &&
                        terminal.Status == "completion_not_observed" &&
                        terminal.ResultJson.Contains("completion_not_observed", StringComparison.Ordinal));
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
            (_, _) => Task.FromResult("""
                {
                  "run_id": "call_gagent_name",
                  "status": "streaming",
                  "stream_topic": "aevatar://actors/actor-from-name/runs/call_gagent_name",
                  "actor_id": "actor-from-name",
                  "wait": "complete"
                }
                """),
            llmRound: 1);

        result.Should().Contain("resolved actor completed");
        harness.TerminalQuery.LastActorId.Should().Be("actor-from-name");
        harness.TerminalQuery.LastCorrelationId.Should().Be("call_gagent_name");
        harness.ChatRunPort.ObservationRequests.Should().HaveCount(2);
        harness.ChatRunPort.ObservationRequests[0].ToolExecutionResultJson.Should().BeEmpty();
        harness.ChatRunPort.ObservationRequests[1].ToolExecutionResultJson.Should().Contain("actor-from-name");
        harness.ChatRunPort.ObservedTerminals.Should().ContainSingle().Which.ActorId.Should().Be("actor-from-name");
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
                    ResultJson = observed.ResultJson,
                    LlmRound = request.LlmRound,
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
