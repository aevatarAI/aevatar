using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;

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
        var service = new FakeCommandInteractionService
        {
            Handler = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = "message-1",
                        Delta = "hello",
                    },
                }, ct);
                return WorkflowChatRunInteractionResult
                    .Success(
                        receipt,
                        new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                            WorkflowProjectionCompletionStatus.Completed,
                            true));
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
        var service = new FakeCommandInteractionService
        {
            Handler = (_, _, _, _) => Task.FromResult(
                WorkflowChatRunInteractionResult
                    .Failure(WorkflowChatRunStartError.WorkflowNotFound)),
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

    [Fact]
    public async Task ExecuteAsync_WhenRuntimeDefaultMetadataProvided_ShouldMergeWithRequestMetadata()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var service = new FakeCommandInteractionService
        {
            Handler = (_, _, _, _) => Task.FromResult(
                WorkflowChatRunInteractionResult
                    .Failure(WorkflowChatRunStartError.AgentNotFound)),
        };

        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope(
                "req-defaults",
                new ChatInput
                {
                    Prompt = "hello",
                    Metadata = new Dictionary<string, string>
                    {
                        ["telegram.chat_id"] = "-100-request",
                    },
                },
                WebSocketMessageType.Text),
            service,
            ApiRequestScope.BeginHttp(),
            CancellationToken.None,
            defaultMetadata: new Dictionary<string, string>
            {
                ["telegram.chat_id"] = "-100-default",
                ["telegram.openclaw_bot_username"] = "openclaw_bot",
            });

        var command = service.LastRequest;
        command.Should().NotBeNull();
        var metadata = command!.Metadata ?? throw new InvalidOperationException("Expected metadata capture.");
        metadata["telegram.chat_id"].Should().Be("-100-request");
        metadata["telegram.openclaw_bot_username"].Should().Be("openclaw_bot");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendCommandError_WhenInputPartsAreUnsupported()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var service = new FakeCommandInteractionService();

        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope(
                "req-unsupported",
                new ChatInput
                {
                    InputParts =
                    [
                        new ChatInputContentPart
                        {
                            Type = "foo",
                        },
                    ],
                },
                WebSocketMessageType.Text),
            service,
            ApiRequestScope.BeginHttp(),
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        socket.SentTexts[0].Should().Contain("\"type\":\"command.error\"");
        socket.SentTexts[0].Should().Contain("PROMPT_REQUIRED");
        service.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ChatCommand_ShouldEmitInvalidFileInput_WhenInlineFileSizeBytesMismatchesDecodedBytes()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var service = new FakeCommandInteractionService();
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "sizeBytes": 6
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope(
                "req-file",
                input,
                WebSocketMessageType.Text),
            service,
            ApiRequestScope.BeginHttp(),
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        socket.SentTexts[0].Should().Contain("\"type\":\"command.error\"");
        socket.SentTexts[0].Should().Contain("INVALID_FILE_INPUT");
        service.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ChatCommand_ShouldDispatch_WhenInlineFileSizeBytesMatchesDecodedBytes()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var ingressPort = new RecordingWorkflowFileIngressPort();
        var service = new FakeCommandInteractionService
        {
            Handler = async (_, _, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                return WorkflowChatRunInteractionResult
                    .Success(
                        receipt,
                        new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                            WorkflowProjectionCompletionStatus.Completed,
                            true));
            },
        };
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "name": "hello.png",
                    "sizeBytes": 5
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        await ChatWebSocketRunCoordinator.ExecuteAsync(
            socket,
            new ChatWebSocketCommandEnvelope(
                "req-file",
                input,
                WebSocketMessageType.Text),
            service,
            ApiRequestScope.BeginHttp(),
            CancellationToken.None,
            fileIngressPort: ingressPort);

        socket.SentTexts.Should().ContainSingle();
        socket.SentTexts[0].Should().Contain("\"type\":\"command.ack\"");
        ingressPort.Requests.Should().ContainSingle();
        ingressPort.Requests[0].Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes("hello"));
        service.LastRequest.Should().NotBeNull();
        var part = service.LastRequest!.InputParts.Should().ContainSingle().Which;
        part.DataBase64.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.ArtifactId.Should().Be("workflow-file://file-1");
        part.FileRef.SizeBytes.Should().Be(5);
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

    private sealed class FakeCommandInteractionService : IWorkflowChatRunInteractionPort
    {
        public Func<WorkflowChatRunRequest, Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask>, Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<WorkflowChatRunInteractionResult>> Handler { get; set; } =
            (_, _, _, _) => Task.FromResult(
                WorkflowChatRunInteractionResult
                    .Failure(WorkflowChatRunStartError.AgentNotFound));

        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public Task<WorkflowChatRunInteractionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Handler(request, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class RecordingWorkflowFileIngressPort : IFileArtifactIngressPort
    {
        public List<FileArtifactIngressRequest> Requests { get; } = [];

        public ValueTask<FileArtifactIngressResult> IngestAsync(
            FileArtifactIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(new FileArtifactIngressResult(new ApplicationFileArtifactRef
            {
                FileId = "file-1",
                ArtifactId = "workflow-file://file-1",
                SourceKind = request.SourceKind,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = request.Content.Length,
                Sha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                CreatedAtUnixMs = 1710000000000,
                ExpiresAtUnixMs = 1710003600000,
            }));
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

        public void EnqueueReceive(WebSocketMessageType type, byte[] data, bool endOfMessage) =>
            _receiveQueue.Enqueue((type, data, endOfMessage));

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
