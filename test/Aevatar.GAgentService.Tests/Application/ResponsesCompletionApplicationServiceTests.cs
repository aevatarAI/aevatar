using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
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

public sealed class ResponsesCompletionApplicationServiceTests
{
    [Fact]
    public async Task CollectAsync_WaitCompleteInvocationTool_ShouldRouteThroughChatRunTypedExecutor()
    {
        var harness = new CompletionHarness();
        var service = new ResponsesCompletionApplicationService(harness.CreateCoordinator());
        var tool = new TypedInvocationTool();
        var provider = new WaitCompleteInvocationProvider();

        var result = await service.CollectAsync(
            provider,
            BuildRequest(tool),
            BuildToolContext(),
            BuildClassification(tool));

        result.Text.Should().Be("typed completion consumed");
        provider.SecondRoundToolResult.Should().Contain("\"typed\":true");
        tool.TypedExecuteCount.Should().Be(1);
        tool.LegacyExecuteCount.Should().Be(0);
        harness.ChatRunPort.SubmittedToolCalls.Should().ContainSingle().Which.ToolExecutionResultJson
            .Should().Be("""{"dispatch":"typed"}""");
    }

    [Fact]
    public async Task StreamAsync_WaitCompleteInvocationTool_ShouldRouteThroughChatRunTypedExecutor()
    {
        var harness = new CompletionHarness();
        var service = new ResponsesCompletionApplicationService(harness.CreateCoordinator());
        var tool = new TypedInvocationTool();
        var provider = new WaitCompleteInvocationProvider();
        var textDeltas = new List<string>();

        var result = await service.StreamAsync(
            provider,
            BuildRequest(tool),
            BuildToolContext(),
            BuildClassification(tool),
            (delta, _) =>
            {
                textDeltas.Add(delta);
                return ValueTask.CompletedTask;
            });

        result.Text.Should().Be("typed completion consumed");
        textDeltas.Should().Equal("typed completion consumed");
        provider.SecondRoundToolResult.Should().Contain("\"typed\":true");
        tool.TypedExecuteCount.Should().Be(1);
        tool.LegacyExecuteCount.Should().Be(0);
        harness.ChatRunPort.SubmittedToolCalls.Should().ContainSingle().Which.ToolExecutionResultJson
            .Should().Be("""{"dispatch":"typed"}""");
    }

    private static LLMRequest BuildRequest(IAgentTool tool) =>
        new()
        {
            Messages = [ChatMessage.User("run local invocation")],
            RequestId = "request-1",
            CallerContext = new LLMRequestCallerContext("scope-1", "owner-1", "resp_1"),
            Model = "test-model",
            Tools = [tool],
            ToolContext = BuildToolContext(),
        };

    private static AgentToolExecutionContext BuildToolContext() =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-1", null),
            Caller = new AgentToolCallerContext("scope-1", "owner-1", "resp_1"),
        };

    private static ResponsesToolClassification BuildClassification(IAgentTool tool) =>
        new([], [tool], [tool.Name], []);

    private sealed class WaitCompleteInvocationProvider : ILLMProvider
    {
        private int _round;

        public string Name => "test";

        public string? SecondRoundToolResult { get; private set; }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _round++;
            if (_round == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_team",
                        Name = "aevatar_invoke_team",
                        ArgumentsJson = """{"team_id":"team-1","endpoint_id":"entry","wait":"complete"}""",
                    },
                };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                yield break;
            }

            SecondRoundToolResult = request.Messages.Last(message => message.Role == "tool").Content;
            yield return new LLMStreamChunk
            {
                DeltaContent = "typed completion consumed",
                IsLast = true,
                FinishReason = "stop",
            };
        }
    }

    private sealed class TypedInvocationTool : IAgentTool, IChatRunToolCompletionControlExecutor
    {
        public string Name => "aevatar_invoke_team";

        public string Description => "Invoke a team.";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public int TypedExecuteCount { get; private set; }

        public int LegacyExecuteCount { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            LegacyExecuteCount++;
            return Task.FromResult("""{"legacy":true}""");
        }

        public Task<ChatRunToolCompletionRequest> ExecuteForChatRunAsync(
            ChatRunToolCompletionRequest request,
            CancellationToken ct = default)
        {
            TypedExecuteCount++;
            return Task.FromResult(request with
            {
                ToolExecutionResultJson = """{"dispatch":"typed"}""",
                RunId = "team-command",
                Status = "RunFinished",
                CompletionResultJson = """{"typed":true,"status":"RunFinished"}""",
                ServiceId = "service-1",
                EndpointId = "entry",
                ScopeId = "scope-1",
                WaitMode = ChatRunSubRunWaitMode.Complete,
                CompletionObserved = true,
            });
        }
    }

    private sealed class CompletionHarness
    {
        private readonly RecordingSubscriptionProvider _subscriptionProvider = new();

        public CompletionHarness()
        {
            ChatRunPort = new RecordingChatRunActorPort(_subscriptionProvider);
        }

        public RecordingChatRunActorPort ChatRunPort { get; }

        public ChatRunToolCompletionCoordinator CreateCoordinator() =>
            new(
                ChatRunPort,
                _subscriptionProvider,
                new EmptyTerminalQueryPort(),
                new EmptyServiceRunQueryPort(),
                new EmptyWorkflowQueryService());
    }

    private sealed class RecordingChatRunActorPort(RecordingSubscriptionProvider subscriptionProvider)
        : IChatRunActorPort
    {
        public ChatRunStartRequest? StartRequest { get; private set; }

        public List<ChatRunToolCompletionRequest> SubmittedToolCalls { get; } = [];

        public Task<string> StartAsync(ChatRunStartRequest request, CancellationToken ct = default)
        {
            StartRequest = request;
            return Task.FromResult("chat-run:resp_1");
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
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ObserveSubRunTerminalAsync(
            string chatRunActorId,
            ChatRunSubRunTerminalObserved observed,
            CancellationToken ct = default) =>
            subscriptionProvider.PublishReadyAsync(
                chatRunActorId,
                new ChatRunToolResultReady
                {
                    ResponseId = StartRequest?.ResponseId ?? string.Empty,
                    RunId = observed.RunId,
                    CallerToolCallId = SubmittedToolCalls.Single().ToolCall.Id,
                    ToolName = SubmittedToolCalls.Single().ToolCall.Name,
                    InternalResultJson = observed.InternalResultJson,
                    LlmRound = SubmittedToolCalls.Single().LlmRound,
                    Status = observed.Status,
                    ServiceId = observed.ServiceId,
                    EndpointId = observed.EndpointId,
                    CompletionObserved = observed.CompletionObserved,
                });

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

    private sealed class EmptyServiceRunQueryPort : IServiceRunQueryPort
    {
        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceRunSnapshot>>([]);

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRunSnapshot?>(null);

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRunSnapshot?>(null);
    }

    private sealed class EmptyTerminalQueryPort : IGAgentRunTerminalQueryPort
    {
        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentRunTerminalSnapshot?>(null);

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentRunTerminalSnapshot?>(null);
    }

    private sealed class EmptyWorkflowQueryService : IWorkflowExecutionQueryApplicationService
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

        public IReadOnlyList<string> ListWorkflows() => [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowCatalogItem>>([]);

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(
            string workflowName,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowCatalogItemDetail?>(null);

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCapabilitiesDocument());

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorSnapshot?>(null);

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(
            string workflowRunId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowRunReport?>(null);

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
            string workflowRunId,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunTimelineExportItem>>([]);

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
            string workflowRunId,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunGraphExportEdge>>([]);

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
            string workflowRunId,
            int depth = 2,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowRunGraphExportSubgraph());
    }
}
