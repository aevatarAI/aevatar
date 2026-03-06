using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCapabilityEndpointsCoverageTests
{
    [Fact]
    public async Task HandleChat_WhenStarted_ShouldExposeTraceAndCorrelationHeaders()
    {
        using var activity = new System.Diagnostics.Activity("http-chat-trace").Start();
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        var service = new FakeCommandExecutionService
        {
            Handler = async (_, _, onStartedAsync, ct) =>
            {
                if (onStartedAsync != null)
                    await onStartedAsync(new WorkflowChatRunStarted("actor-1", "direct", "cmd-header"), ct);

                return new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.None,
                    new WorkflowChatRunStarted("actor-1", "direct", "cmd-header"),
                    new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            CancellationToken.None);

        http.Response.Headers.ContainsKey("X-Trace-Id").Should().BeFalse();
        http.Response.Headers["X-Correlation-Id"].ToString().Should().Be("cmd-header");
    }

    [Fact]
    public async Task HandleChat_WhenOperationCanceled_ShouldSwallowException()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };
        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => throw new OperationCanceledException(),
        };

        var act = async () => await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            service,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleCommand_WhenExecutionReturnsError_ShouldReturnMappedStatusAndBody()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => Task.FromResult(
                new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.AgentTypeNotSupported,
                    null,
                    null)),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello" },
            service,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);
        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        doc.RootElement.GetProperty("code").GetString().Should().Be("AGENT_TYPE_NOT_SUPPORTED");
    }

    [Fact]
    public async Task HandleCommand_WhenNoStartSignalButResultContainsStarted_ShouldReturnAccepted()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => Task.FromResult(
                new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.None,
                    new WorkflowChatRunStarted("actor-1", "direct", "cmd-1"),
                    new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true))),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);
        statusCode.Should().Be(StatusCodes.Status202Accepted);
        doc.RootElement.GetProperty("commandId").GetString().Should().Be("cmd-1");
        doc.RootElement.GetProperty("correlationId").GetString().Should().Be("cmd-1");
        doc.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();
        doc.RootElement.GetProperty("actorId").GetString().Should().Be("actor-1");
    }

    [Fact]
    public async Task HandleCommand_WhenResultHasNoErrorAndNoStarted_ShouldReturn500()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => Task.FromResult(
                new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                    WorkflowChatRunStartError.None,
                    null,
                    null)),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello" },
            service,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, _) = await ExecuteResultAsync(result);
        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task HandleChatWebSocket_WhenNotWebSocketRequest_ShouldReturn400()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };
        var service = new FakeCommandExecutionService();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            service,
            loggerFactory,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        text.Should().Contain("Expected websocket request.");
    }

    [Fact]
    public async Task HandleChatWebSocket_WhenParseFails_ShouldSendCommandError()
    {
        using var activity = new System.Diagnostics.Activity("ws-parse-trace").Start();
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"type":"chat.command","requestId":"req-parse","payload":{"prompt":""}}"""), true);

        var http = new DefaultHttpContext();
        http.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(socket));

        var service = new FakeCommandExecutionService();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            service,
            loggerFactory,
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        using var doc = JsonDocument.Parse(socket.SentTexts[0]);
        doc.RootElement.GetProperty("type").GetString().Should().Be(ChatWebSocketMessageTypes.CommandError);
        doc.RootElement.GetProperty("code").GetString().Should().Be("INVALID_PROMPT");
        doc.RootElement.GetProperty("requestId").GetString().Should().Be("req-parse");
        doc.RootElement.GetProperty("correlationId").GetString().Should().Be("req-parse");
        doc.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("payload", out _).Should().BeFalse();
    }

    [Fact]
    public async Task HandleChatWebSocket_WhenExecutionThrows_ShouldSendRunExecutionFailed()
    {
        using var activity = new System.Diagnostics.Activity("ws-exception-trace").Start();
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(
            WebSocketMessageType.Text,
            Encoding.UTF8.GetBytes("""{"type":"chat.command","requestId":"req-1","payload":{"prompt":"hello"}}"""),
            true);

        var http = new DefaultHttpContext();
        http.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(socket));

        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        using var loggerFactory = LoggerFactory.Create(_ => { });

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            service,
            loggerFactory,
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        using var doc = JsonDocument.Parse(socket.SentTexts[0]);
        doc.RootElement.GetProperty("type").GetString().Should().Be(ChatWebSocketMessageTypes.CommandError);
        doc.RootElement.GetProperty("code").GetString().Should().Be("RUN_EXECUTION_FAILED");
        doc.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("payload", out _).Should().BeFalse();
    }

    [Fact]
    public async Task HandleChatWebSocket_WhenBinaryExecutionThrows_ShouldSendBinaryRunExecutionFailed()
    {
        using var activity = new System.Diagnostics.Activity("ws-binary-exception-trace").Start();
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(
            WebSocketMessageType.Binary,
            Encoding.UTF8.GetBytes("""{"type":"chat.command","requestId":"req-b","payload":{"prompt":"hello"}}"""),
            true);

        var http = new DefaultHttpContext();
        http.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(socket));

        var service = new FakeCommandExecutionService
        {
            Handler = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        using var loggerFactory = LoggerFactory.Create(_ => { });

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            service,
            loggerFactory,
            CancellationToken.None);

        socket.SentTexts.Should().BeEmpty();
        socket.SentBinaries.Should().ContainSingle();
        using var doc = JsonDocument.Parse(socket.SentBinaries[0]);
        doc.RootElement.GetProperty("type").GetString().Should().Be(ChatWebSocketMessageTypes.CommandError);
        doc.RootElement.GetProperty("code").GetString().Should().Be("RUN_EXECUTION_FAILED");
        doc.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };
        http.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, body);
    }

    private sealed class FakeCommandExecutionService
        : ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>
    {
        public Func<WorkflowChatRunRequest, Func<WorkflowOutputFrame, CancellationToken, ValueTask>, Func<WorkflowChatRunStarted, CancellationToken, ValueTask>?, CancellationToken, Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>>>
            Handler { get; set; } = (_, _, _, _) =>
                Task.FromResult(
                    new CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>(
                        WorkflowChatRunStartError.None,
                        null,
                        null));

        public Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>> ExecuteAsync(
            WorkflowChatRunRequest command,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
            CancellationToken ct = default)
        {
            return Handler(command, emitAsync, onStartedAsync, ct);
        }
    }

    private sealed class FakeWebSocketFeature : IHttpWebSocketFeature
    {
        private readonly WebSocket _socket;

        public FakeWebSocketFeature(WebSocket socket)
        {
            _socket = socket;
        }

        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            return Task.FromResult(_socket);
        }
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly Queue<(WebSocketMessageType Type, byte[] Data, bool EndOfMessage)> _receiveFrames = new();
        private WebSocketState _state;

        public FakeWebSocket(WebSocketState state)
        {
            _state = state;
        }

        public List<string> SentTexts { get; } = [];
        public List<byte[]> SentBinaries { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueReceive(WebSocketMessageType type, byte[] data, bool endOfMessage)
        {
            _receiveFrames.Enqueue((type, data, endOfMessage));
        }

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_receiveFrames.Count == 0)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var frame = _receiveFrames.Dequeue();
            var count = Math.Min(buffer.Count, frame.Data.Length);
            if (count > 0 && buffer.Array != null)
                Array.Copy(frame.Data, 0, buffer.Array, buffer.Offset, count);

            return Task.FromResult(new WebSocketReceiveResult(count, frame.Type, frame.EndOfMessage));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text && buffer.Array != null)
                SentTexts.Add(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count));
            else if (messageType == WebSocketMessageType.Binary && buffer.Array != null)
            {
                var bytes = new byte[buffer.Count];
                Array.Copy(buffer.Array, buffer.Offset, bytes, 0, buffer.Count);
                SentBinaries.Add(bytes);
            }
            return Task.CompletedTask;
        }
    }
}
