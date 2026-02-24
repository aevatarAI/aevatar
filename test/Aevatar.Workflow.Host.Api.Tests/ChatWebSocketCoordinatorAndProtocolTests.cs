using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatWebSocketCoordinatorAndProtocolTests
{
    [Fact]
    public async Task ExecuteAsync_WhenSuccess_ShouldSendAckEventsAndQueryResult()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var queryService = new FakeQueryService
        {
            Snapshot = new WorkflowActorSnapshot
            {
                ActorId = "actor-1",
                WorkflowName = "direct",
                LastCommandId = "cmd-1",
            },
        };
        var service = new FakeCommandExecutionService
        {
            Handler = async (_, emitAsync, onStartedAsync, ct) =>
            {
                var started = new WorkflowChatRunStarted("actor-1", "direct", "cmd-1");
                if (onStartedAsync != null)
                    await onStartedAsync(started, ct);

                await emitAsync(new WorkflowOutputFrame { Type = "RUN_STARTED", ThreadId = "actor-1" }, ct);
                await emitAsync(new WorkflowOutputFrame { Type = "RUN_FINISHED", ThreadId = "actor-1" }, ct);

                return new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.None,
                    started,
                    new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };

        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope("req-1", new ChatInput
            {
                Prompt = "hello",
                Workflow = "direct",
                AgentId = "actor-1",
            }),
            service,
            queryService,
            CancellationToken.None);

        var types = socket.SentTexts
            .Select(static text => JsonDocument.Parse(text).RootElement.GetProperty("type").GetString())
            .ToList();
        types.Should().Equal("command.ack", "agui.event", "agui.event", "query.result");

        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.Prompt.Should().Be("hello");
        queryService.LastActorId.Should().Be("actor-1");
    }

    [Fact]
    public async Task ExecuteAsync_WhenStartFails_ShouldSendCommandErrorOnly()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var queryService = new FakeQueryService();
        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => Task.FromResult(
                new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.WorkflowNotFound,
                    null,
                    null)),
        };

        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope("req-2", new ChatInput { Prompt = "hello" }),
            service,
            queryService,
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        using var doc = JsonDocument.Parse(socket.SentTexts[0]);
        doc.RootElement.GetProperty("type").GetString().Should().Be("command.error");
        doc.RootElement.GetProperty("code").GetString().Should().Be("WORKFLOW_NOT_FOUND");
        queryService.LastActorId.Should().BeNull();
    }

    [Fact]
    public async Task ReceiveTextAsync_ShouldSkipNonTextAndAssembleChunks()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(WebSocketMessageType.Binary, Encoding.UTF8.GetBytes("ignore"), true);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hel"), false);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("lo"), true);

        var text = await ChatWebSocketProtocol.ReceiveTextAsync(socket, CancellationToken.None);

        text.Should().Be("hello");
    }

    [Fact]
    public async Task ReceiveTextAsync_WhenCloseFrame_ShouldReturnNull()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(WebSocketMessageType.Close, [], true);

        var text = await ChatWebSocketProtocol.ReceiveTextAsync(socket, CancellationToken.None);

        text.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_ShouldRespectSocketStateAndUseCamelCase()
    {
        var openSocket = new FakeWebSocket(WebSocketState.Open);
        await ChatWebSocketProtocol.SendAsync(openSocket, new { RequestId = "r1", ValueNum = 2 }, CancellationToken.None);

        openSocket.SentTexts.Should().ContainSingle();
        using (var doc = JsonDocument.Parse(openSocket.SentTexts[0]))
        {
            doc.RootElement.GetProperty("requestId").GetString().Should().Be("r1");
            doc.RootElement.GetProperty("valueNum").GetInt32().Should().Be(2);
        }

        var closedSocket = new FakeWebSocket(WebSocketState.Closed);
        await ChatWebSocketProtocol.SendAsync(closedSocket, new { RequestId = "x" }, CancellationToken.None);
        closedSocket.SentTexts.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAsync_ShouldOnlyCloseOpenOrCloseReceivedSockets()
    {
        var open = new FakeWebSocket(WebSocketState.Open);
        await ChatWebSocketProtocol.CloseAsync(open, CancellationToken.None);
        open.CloseCalls.Should().Be(1);
        open.State.Should().Be(WebSocketState.Closed);

        var closeReceived = new FakeWebSocket(WebSocketState.CloseReceived);
        await ChatWebSocketProtocol.CloseAsync(closeReceived, CancellationToken.None);
        closeReceived.CloseCalls.Should().Be(1);

        var aborted = new FakeWebSocket(WebSocketState.Aborted);
        await ChatWebSocketProtocol.CloseAsync(aborted, CancellationToken.None);
        aborted.CloseCalls.Should().Be(0);
    }

    private sealed class FakeCommandExecutionService
        : ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>
    {
        public Func<WorkflowChatRunRequest, Func<WorkflowOutputFrame, CancellationToken, ValueTask>, Func<WorkflowChatRunStarted, CancellationToken, ValueTask>?, CancellationToken, Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>>>
            Handler { get; set; } = (_, _, _, _) =>
                Task.FromResult(new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.None,
                    null,
                    null));

        public WorkflowChatRunRequest? LastCommand { get; private set; }

        public Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>> ExecuteAsync(
            WorkflowChatRunRequest command,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Handler(command, emitAsync, onStartedAsync, ct);
        }
    }

    private sealed class FakeQueryService : IWorkflowExecutionQueryApplicationService
    {
        public WorkflowActorSnapshot? Snapshot { get; init; }
        public string? LastActorId { get; private set; }

        public bool ActorQueryEnabled => true;
        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);
        public IReadOnlyList<string> ListWorkflows() => [];
        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            LastActorId = actorId;
            return Task.FromResult<WorkflowActorSnapshot?>(Snapshot);
        }
        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>([]);
        public Task<IReadOnlyList<WorkflowActorGraphEdge>> ListActorGraphEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkflowActorGraphEdge>>([]);
        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowActorGraphSubgraph
            {
                RootNodeId = actorId,
            });

        public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            if (Snapshot == null)
                return Task.FromResult<WorkflowActorGraphEnrichedSnapshot?>(null);

            return Task.FromResult<WorkflowActorGraphEnrichedSnapshot?>(new WorkflowActorGraphEnrichedSnapshot
            {
                Snapshot = Snapshot,
                Subgraph = new WorkflowActorGraphSubgraph
                {
                    RootNodeId = actorId,
                },
            });
        }
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly ConcurrentQueue<(WebSocketMessageType Type, byte[] Data, bool EndOfMessage)> _receiveQueue = new();
        private WebSocketState _state;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public FakeWebSocket(WebSocketState state)
        {
            _state = state;
        }

        public List<string> SentTexts { get; } = [];
        public int CloseCalls { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueReceive(WebSocketMessageType type, byte[] data, bool endOfMessage)
        {
            _receiveQueue.Enqueue((type, data, endOfMessage));
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCalls++;
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receiveQueue.TryDequeue(out var frame))
            {
                var count = Math.Min(buffer.Count, frame.Data.Length);
                if (count > 0 && buffer.Array != null)
                    Array.Copy(frame.Data, 0, buffer.Array, buffer.Offset, count);
                return Task.FromResult(new WebSocketReceiveResult(count, frame.Type, frame.EndOfMessage));
            }

            _state = WebSocketState.CloseReceived;
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (messageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
                SentTexts.Add(text);
            }

            return Task.CompletedTask;
        }
    }
}
