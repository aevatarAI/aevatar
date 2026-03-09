using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatWebSocketCoordinatorAndProtocolTests
{
    [Fact]
    public void CreateCommandAck_ShouldMapReceiptFields()
    {
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");

        var envelope = ChatWebSocketEnvelopeFactory.CreateCommandAck("req-1", receipt);

        envelope.RequestId.Should().Be("req-1");
        envelope.CorrelationId.Should().Be("corr-1");
        envelope.Payload.CommandId.Should().Be("cmd-1");
        envelope.Payload.ActorId.Should().Be("actor-1");
        envelope.Payload.Workflow.Should().Be("direct");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendAckAndAguiEvent()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var service = new FakeWorkflowRunInteractionService
        {
            ResultFactory = async (emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(new WorkflowOutputFrame { Type = "delta", Delta = "hello" }, ct);
                return new WorkflowChatRunInteractionResult(
                    WorkflowChatRunStartError.None,
                    receipt,
                    new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };

        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope(
                "req-1",
                new ChatInput { Prompt = "hello" },
                WebSocketMessageType.Text),
            service,
            ApiRequestScope.BeginHttp(),
            CancellationToken.None);

        socket.SentTexts.Should().HaveCount(2);
        socket.SentTexts[0].Should().Contain("\"type\":\"command.ack\"");
        socket.SentTexts[0].Should().Contain("\"commandId\":\"cmd-1\"");
        socket.SentTexts[1].Should().Contain("\"type\":\"agui.event\"");
        socket.SentTexts[1].Should().Contain("\"delta\":\"hello\"");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendCommandError_WhenStartFails()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var service = new FakeWorkflowRunInteractionService
        {
            ResultFactory = (_, _, _) => Task.FromResult(
                new WorkflowChatRunInteractionResult(
                    WorkflowChatRunStartError.WorkflowNotFound,
                    null,
                    null)),
        };

        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope(
                "req-1",
                new ChatInput { Prompt = "hello", Workflow = "missing" },
                WebSocketMessageType.Text),
            service,
            ApiRequestScope.BeginHttp(),
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        socket.SentTexts[0].Should().Contain("\"type\":\"command.error\"");
        socket.SentTexts[0].Should().Contain("WORKFLOW_NOT_FOUND");
    }

    private sealed class FakeWorkflowRunInteractionService : IWorkflowRunInteractionService
    {
        public Func<Func<WorkflowOutputFrame, CancellationToken, ValueTask>, Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<WorkflowChatRunInteractionResult>> ResultFactory { get; set; } =
            (_, _, _) => Task.FromResult(new WorkflowChatRunInteractionResult(WorkflowChatRunStartError.None, null, null));

        public Task<WorkflowChatRunInteractionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = request;
            return ResultFactory(emitAsync, onAcceptedAsync, ct);
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

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            _ = endOfMessage;

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
