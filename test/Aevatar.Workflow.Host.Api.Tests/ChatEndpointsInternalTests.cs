using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatEndpointsInternalTests
{
    [Fact]
    public async Task HandleCommand_ShouldReturnAcceptedPayload_WhenDispatchSucceeds()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        body.Should().Contain("cmd-1");
        body.Should().Contain("corr-1");
        body.Should().Contain("actor-1");
    }

    [Fact]
    public async Task HandleCommand_ShouldMapStartError_WhenDispatchFails()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "missing" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("WORKFLOW_NOT_FOUND");
    }

    [Fact]
    public async Task HandleCommand_ShouldReturnBadRequest_WhenPromptMissing()
    {
        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = " " },
            new FakeCommandDispatchService(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleCommand_ShouldReturn499_WhenDispatchCanceled()
    {
        var service = new FakeCommandDispatchService
        {
            DispatchException = new OperationCanceledException("cancelled"),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(499);
    }

    [Fact]
    public async Task HandleCommand_ShouldReturnServerError_WhenDispatchThrows()
    {
        var service = new FakeCommandDispatchService
        {
            DispatchException = new InvalidOperationException("boom"),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("EXECUTION_FAILED");
    }

    [Fact]
    public async Task HandleChat_ShouldReturnBadRequest_WhenPromptMissing()
    {
        var http = CreateHttpContext();

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "" },
            new FakeWorkflowRunInteractionService(),
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleChat_ShouldReturnJsonError_WhenExecutionReturnsStartErrorBeforeWriterStarts()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeWorkflowRunInteractionService
        {
            ResultFactory = (_, _, _) => Task.FromResult(
                new WorkflowChatRunInteractionResult(
                    WorkflowChatRunStartError.WorkflowBindingMismatch,
                    null,
                    null)),
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("WORKFLOW_BINDING_MISMATCH");
    }

    [Fact]
    public async Task HandleChat_ShouldWriteSseFramesAndCorrelationHeader_WhenExecutionSucceeds()
    {
        var interactionService = new FakeWorkflowRunInteractionService
        {
            ResultFactory = async (emitAsync, onAcceptedAsync, ct) =>
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
                return new WorkflowChatRunInteractionResult(
                    WorkflowChatRunStartError.None,
                    receipt,
                    new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        http.Response.Headers["X-Correlation-Id"].ToString().Should().Be("corr-1");
        body.Should().Contain("aevatar.run.context");
        body.Should().Contain("\"delta\": \"hello\"");
    }

    [Fact]
    public async Task HandleChat_ShouldReturnServerError_WhenExecutionThrowsBeforeStreamStarts()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeWorkflowRunInteractionService
        {
            ResultFactory = (_, _, _) => throw new InvalidOperationException("boom"),
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("EXECUTION_FAILED");
    }

    [Fact]
    public async Task HandleChat_ShouldWriteRunErrorFrame_WhenExecutionThrowsAfterStreamStarts()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeWorkflowRunInteractionService
        {
            ResultFactory = async (emitAsync, onAcceptedAsync, ct) =>
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
                throw new InvalidOperationException("line1\r\nline2");
            },
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello" },
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"delta\": \"hello\"");
        body.Should().Contain("Workflow execution failed: line1  line2");
    }

    [Fact]
    public async Task HandleResume_ShouldRejectMissingFields()
    {
        var runtime = new FakeActorRuntime();
        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "",
                RunId = "run-1",
                StepId = "step-1",
            },
            runtime,
            runtime,
            new FakeWorkflowActorBindingReader(),
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("actorId, runId and stepId are required");
    }

    [Fact]
    public async Task HandleResume_ShouldReturnNotFound_WhenActorMissing()
    {
        var runtime = new FakeActorRuntime();
        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-404",
                RunId = "run-1",
                StepId = "step-1",
            },
            runtime,
            runtime,
            new FakeWorkflowActorBindingReader(),
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("Actor 'actor-404' not found");
    }

    [Fact]
    public async Task HandleResume_ShouldDispatchEnvelope_WhenActorIsWorkflowRun()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var bindingReader = new FakeWorkflowActorBindingReader
        {
            Binding = new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "actor-1",
                "definition-1",
                "run-1",
                "direct",
                "yaml",
                new Dictionary<string, string>()),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "step-1",
            },
            runtime,
            runtime,
            bindingReader,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        runtime.DispatchCalls.Should().ContainSingle();
        runtime.DispatchCalls.Single().ActorId.Should().Be("actor-1");
        runtime.DispatchCalls.Single().Envelope.Payload.TypeUrl.Should().Contain("WorkflowResumedEvent");
    }

    [Fact]
    public async Task HandleSignal_ShouldRejectNonRunActor()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var bindingReader = new FakeWorkflowActorBindingReader
        {
            Binding = WorkflowActorBinding.Unsupported("actor-1"),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                SignalName = "approve",
            },
            runtime,
            runtime,
            bindingReader,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("not a workflow run actor");
        runtime.DispatchCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleSignal_ShouldRejectMissingFields()
    {
        var runtime = new FakeActorRuntime();
        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "",
                SignalName = "approve",
            },
            runtime,
            runtime,
            new FakeWorkflowActorBindingReader(),
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("actorId, runId and signalName are required");
    }

    [Fact]
    public async Task HandleSignal_ShouldDispatchEnvelope_AndGenerateCommandId_WhenMissing()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var bindingReader = new FakeWorkflowActorBindingReader
        {
            Binding = new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "actor-1",
                "definition-1",
                "run-1",
                "direct",
                "yaml",
                new Dictionary<string, string>()),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                SignalName = "approve",
                Payload = "yes",
            },
            runtime,
            runtime,
            bindingReader,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        runtime.DispatchCalls.Should().ContainSingle();
        runtime.DispatchCalls.Single().Envelope.Payload.TypeUrl.Should().Contain("SignalReceivedEvent");
        runtime.DispatchCalls.Single().Envelope.Propagation.CorrelationId.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("\"accepted\":true");
    }

    [Fact]
    public async Task HandleChatWebSocket_ShouldRejectNonWebSocketRequests()
    {
        var http = CreateHttpContext();

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            new FakeWorkflowRunInteractionService(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("Expected websocket request.");
    }

    [Fact]
    public async Task HandleChatWebSocket_ShouldSendCommandError_WhenCommandParseFails()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive("""{"type":"unknown","payload":{"prompt":"hello"}}""");
        var http = CreateHttpContext();
        http.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            new FakeWorkflowRunInteractionService(),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        socket.SentTexts[0].Should().Contain("\"type\":\"command.error\"");
        socket.SentTexts[0].Should().Contain("INVALID_COMMAND");
        socket.CloseCalls.Should().Be(1);
    }

    [Fact]
    public async Task HandleChatWebSocket_ShouldSendFailure_WhenExecutionThrows()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive("""{"type":"chat.command","requestId":"req-1","payload":{"prompt":"hello"}}""");
        var http = CreateHttpContext();
        http.Features.Set<IHttpWebSocketFeature>(new FakeHttpWebSocketFeature(socket));
        var interactionService = new FakeWorkflowRunInteractionService
        {
            ResultFactory = (_, _, _) => throw new InvalidOperationException("boom"),
        };

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        socket.SentTexts.Should().ContainSingle();
        socket.SentTexts[0].Should().Contain("\"type\":\"command.error\"");
        socket.SentTexts[0].Should().Contain("RUN_EXECUTION_FAILED");
        socket.CloseCalls.Should().Be(1);
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private sealed class FakeWorkflowRunInteractionService : IWorkflowRunInteractionService
    {
        public Func<Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask>, Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<WorkflowChatRunInteractionResult>> ResultFactory { get; set; } =
            (_, _, _) => Task.FromResult(new WorkflowChatRunInteractionResult(WorkflowChatRunStartError.None, null, null));

        public Task<WorkflowChatRunInteractionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = request;
            return ResultFactory(emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class FakeCommandDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.AgentNotFound);
        public Exception? DispatchException { get; set; }

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            if (DispatchException != null)
                throw DispatchException;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public WorkflowActorBinding? Binding { get; set; }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Binding);
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);
        public List<(string ActorId, EventEnvelope Envelope)> DispatchCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(StoredActors.TryGetValue(id, out var actor) ? actor : null);

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DispatchCalls.Add((actorId, envelope));
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(StoredActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent(id + "-agent");
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeHttpWebSocketFeature(FakeWebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            _ = context;
            return Task.FromResult<WebSocket>(socket);
        }
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly Queue<byte[]> _receives = new();
        private WebSocketState _state;

        public FakeWebSocket(WebSocketState state)
        {
            _state = state;
        }

        public List<string> SentTexts { get; } = [];
        public int CloseCalls { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueReceive(string text) => _receives.Enqueue(Encoding.UTF8.GetBytes(text));

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = closeStatus;
            _ = statusDescription;
            CloseCalls++;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receives.Count == 0)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var frame = _receives.Dequeue();
            Array.Copy(frame, 0, buffer.Array!, buffer.Offset, frame.Length);
            return Task.FromResult(new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = messageType;
            _ = endOfMessage;
            SentTexts.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}
