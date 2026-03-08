using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatWebSocketCoordinatorAndProtocolTests
{
    [Fact]
    public async Task ExecuteAsync_WhenSuccess_ShouldSendAckAndRunEvents()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var activity = new Activity("ws-success-trace").Start();
        var service = new FakeCommandExecutionService
        {
            Handler = async (_, emitAsync, onStartedAsync, ct) =>
            {
                var started = new WorkflowChatRunStarted("actor-1", "direct", "cmd-1");
                if (onStartedAsync != null)
                    await onStartedAsync(started, ct);

                await emitAsync(new WorkflowOutputFrame { Type = WorkflowRunEventTypes.RunStarted, ThreadId = "actor-1" }, ct);
                await emitAsync(new WorkflowOutputFrame { Type = WorkflowRunEventTypes.RunFinished, ThreadId = "actor-1" }, ct);

                return new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.None,
                    started,
                    new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };

        var scope = ApiRequestScope.BeginWebSocket();
        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope("req-1", new ChatInput
            {
                Prompt = "hello",
                Workflow = "direct",
                WorkflowYamls = ["name: direct"],
                AgentId = "actor-1",
            }, WebSocketMessageType.Text),
            service,
            scope,
            CancellationToken.None);

        var types = socket.SentTexts
            .Select(static text => JsonDocument.Parse(text).RootElement.GetProperty("type").GetString())
            .ToList();
        types.Should().Equal(
            ChatWebSocketMessageTypes.CommandAck,
            ChatWebSocketMessageTypes.AguiEvent,
            ChatWebSocketMessageTypes.AguiEvent);

        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.Prompt.Should().Be("hello");
        service.LastCommand.WorkflowName.Should().BeNull();
        service.LastCommand.WorkflowYamls.Should().NotBeNull();
        service.LastCommand.WorkflowYamls![0].Should().Be("name: direct");

        using var ackDoc = JsonDocument.Parse(socket.SentTexts[0]);
        ackDoc.RootElement.GetProperty("correlationId").GetString().Should().Be("cmd-1");
        ackDoc.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();

        using var eventDoc = JsonDocument.Parse(socket.SentTexts[1]);
        eventDoc.RootElement.GetProperty("correlationId").GetString().Should().Be("cmd-1");
        eventDoc.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenStartFails_ShouldSendCommandErrorOnly()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var activity = new Activity("ws-error-trace").Start();
        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => Task.FromResult(
                new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.WorkflowNotFound,
                    null,
                    null)),
        };

        var scope = ApiRequestScope.BeginWebSocket();
        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope("req-2", new ChatInput { Prompt = "hello" }, WebSocketMessageType.Text),
            service,
            scope,
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        using var doc = JsonDocument.Parse(socket.SentTexts[0]);
        doc.RootElement.GetProperty("type").GetString().Should().Be(ChatWebSocketMessageTypes.CommandError);
        doc.RootElement.GetProperty("code").GetString().Should().Be("WORKFLOW_NOT_FOUND");
        doc.RootElement.GetProperty("correlationId").GetString().Should().Be("req-2");
        doc.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("payload", out _).Should().BeFalse();
        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.WorkflowName.Should().Be("auto");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAgentIdProvidedWithoutWorkflow_ShouldKeepWorkflowUnset()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => Task.FromResult(
                new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.AgentNotFound,
                    null,
                    null)),
        };

        var scope = ApiRequestScope.BeginWebSocket();
        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope(
                "req-3",
                new ChatInput
                {
                    Prompt = "hello",
                    AgentId = " actor-1 ",
                },
                WebSocketMessageType.Text),
            service,
            scope,
            CancellationToken.None);

        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.ActorId.Should().Be("actor-1");
        service.LastCommand.WorkflowName.Should().BeNull();
        socket.SentTexts.Should().ContainSingle();
        using var doc = JsonDocument.Parse(socket.SentTexts[0]);
        doc.RootElement.GetProperty("code").GetString().Should().Be("AGENT_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunEventArrivesBeforeAck_ShouldUseRequestIdAsCorrelationFallback()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        using var activity = new Activity("ws-out-of-order-trace").Start();
        var service = new FakeCommandExecutionService
        {
            Handler = async (_, emitAsync, onStartedAsync, ct) =>
            {
                await emitAsync(new WorkflowOutputFrame { Type = WorkflowRunEventTypes.RunStarted, ThreadId = "actor-1" }, ct);
                var started = new WorkflowChatRunStarted("actor-1", "direct", "cmd-late");
                if (onStartedAsync != null)
                    await onStartedAsync(started, ct);

                return new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.None,
                    started,
                    new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };

        var scope = ApiRequestScope.BeginWebSocket();
        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope("req-fallback", new ChatInput { Prompt = "hello" }, WebSocketMessageType.Text),
            service,
            scope,
            CancellationToken.None);

        socket.SentTexts.Should().HaveCount(2);
        using var eventDoc = JsonDocument.Parse(socket.SentTexts[0]);
        eventDoc.RootElement.GetProperty("type").GetString().Should().Be(ChatWebSocketMessageTypes.AguiEvent);
        eventDoc.RootElement.GetProperty("correlationId").GetString().Should().Be("req-fallback");
        eventDoc.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();

        using var ackDoc = JsonDocument.Parse(socket.SentTexts[1]);
        ackDoc.RootElement.GetProperty("type").GetString().Should().Be(ChatWebSocketMessageTypes.CommandAck);
        ackDoc.RootElement.GetProperty("correlationId").GetString().Should().Be("cmd-late");

    }

    [Fact]
    public async Task ReceiveAsync_ShouldAssembleTextChunks()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hel"), false);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("lo"), true);

        var frame = await ChatWebSocketProtocol.ReceiveAsync(socket, CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Value.MessageType.Should().Be(WebSocketMessageType.Text);
        Encoding.UTF8.GetString(frame.Value.Payload.Span).Should().Be("hello");
    }

    [Fact]
    public async Task ReceiveAsync_ShouldReadBinaryMessage()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(WebSocketMessageType.Binary, [0x01, 0x02], false);
        socket.EnqueueReceive(WebSocketMessageType.Binary, [0x03], true);

        var frame = await ChatWebSocketProtocol.ReceiveAsync(socket, CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Value.MessageType.Should().Be(WebSocketMessageType.Binary);
        frame.Value.Payload.ToArray().Should().Equal([0x01, 0x02, 0x03]);
    }

    [Fact]
    public async Task ReceiveAsync_WhenCloseFrame_ShouldReturnNull()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(WebSocketMessageType.Close, [], true);

        var frame = await ChatWebSocketProtocol.ReceiveAsync(socket, CancellationToken.None);

        frame.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_ShouldRespectSocketStateAndSupportTextAndBinary()
    {
        var openSocket = new FakeWebSocket(WebSocketState.Open);
        await ChatWebSocketProtocol.SendAsync(openSocket, new { RequestId = "r1", ValueNum = 2 }, CancellationToken.None);
        await ChatWebSocketProtocol.SendAsync(openSocket, new { RequestId = "r2", ValueNum = 3 }, CancellationToken.None, WebSocketMessageType.Binary);

        openSocket.SentTexts.Should().ContainSingle();
        using (var doc = JsonDocument.Parse(openSocket.SentTexts[0]))
        {
            doc.RootElement.GetProperty("requestId").GetString().Should().Be("r1");
            doc.RootElement.GetProperty("valueNum").GetInt32().Should().Be(2);
        }
        openSocket.SentBinaries.Should().ContainSingle();
        using (var doc = JsonDocument.Parse(openSocket.SentBinaries[0]))
        {
            doc.RootElement.GetProperty("requestId").GetString().Should().Be("r2");
            doc.RootElement.GetProperty("valueNum").GetInt32().Should().Be(3);
        }

        var closedSocket = new FakeWebSocket(WebSocketState.Closed);
        await ChatWebSocketProtocol.SendAsync(closedSocket, new { RequestId = "x" }, CancellationToken.None);
        closedSocket.SentTexts.Should().BeEmpty();
        closedSocket.SentBinaries.Should().BeEmpty();
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
        public List<byte[]> SentBinaries { get; } = [];
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
            else if (messageType == WebSocketMessageType.Binary)
            {
                var bytes = new byte[buffer.Count];
                Array.Copy(buffer.Array!, buffer.Offset, bytes, 0, buffer.Count);
                SentBinaries.Add(bytes);
            }

            return Task.CompletedTask;
        }
    }
}
