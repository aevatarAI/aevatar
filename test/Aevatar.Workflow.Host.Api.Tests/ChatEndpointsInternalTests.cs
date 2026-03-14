using System.Net.WebSockets;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
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
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.WorkflowName.Should().Be("direct");
        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        body.Should().Contain("cmd-1");
        body.Should().Contain("corr-1");
        body.Should().Contain("actor-1");
    }

    [Fact]
    public async Task HandleCommand_ShouldPreserveOpaqueActorIdInAcceptedLocationAndPayload()
    {
        const string opaqueActorId = "script-runtime:opaque-actor-9";
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt(opaqueActorId, "direct", "cmd-1", "corr-1")),
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
        http.Response.Headers.Location.ToString().Should().Be($"/api/actors/{opaqueActorId}");
        body.Should().Contain(opaqueActorId);
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
        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.WorkflowName.Should().Be("missing");
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
            new FakeCommandInteractionService(),
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleChat_ShouldReturnJsonError_WhenExecutionReturnsStartErrorBeforeWriterStarts()
    {
        var http = CreateHttpContext();
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Failure(WorkflowChatRunStartError.WorkflowBindingMismatch)),
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
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
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
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
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
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) => throw new InvalidOperationException("boom"),
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
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
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
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>();
        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "",
                RunId = "run-1",
                StepId = "step-1",
            },
            service,
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
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.ActorNotFound("actor-404", "run-1")),
        };
        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-404",
                RunId = "run-1",
                StepId = "step-1",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("Actor 'actor-404' not found");
    }

    [Fact]
    public async Task HandleResume_ShouldDispatchCommand_WhenActorIsWorkflowRun()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "cmd-1", "cmd-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "step-1",
                CommandId = "cmd-1",
                Approved = true,
                UserInput = "approved",
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "host",
                },
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be("actor-1");
        service.Commands.Single().RunId.Should().Be("run-1");
        service.Commands.Single().StepId.Should().Be("step-1");
        service.Commands.Single().CommandId.Should().Be("cmd-1");
        service.Commands.Single().Approved.Should().BeTrue();
        service.Commands.Single().UserInput.Should().Be("approved");
        service.Commands.Single().Metadata.Should().ContainKey("source").WhoseValue.Should().Be("host");
    }

    [Fact]
    public async Task HandleResume_ShouldTreatActorIdAsOpaqueAndForwardItUnchanged()
    {
        const string opaqueActorId = "static-gagent:script-runtime:mixed-shape";
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(opaqueActorId, "run-1", "cmd-1", "cmd-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = opaqueActorId,
                RunId = "run-1",
                StepId = "step-1",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be(opaqueActorId);
    }

    [Fact]
    public async Task HandleResume_ShouldRejectMismatchedRunId()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.RunBindingMismatch("actor-1", "run-other", "run-expected")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-other",
                StepId = "step-1",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("run-expected");
        service.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleResume_ShouldMapInvalidStepId_FromApplicationLayer()
    {
        var service = new RecordingDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidStepId("actor-1", "run-1", " ")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "step-1",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("stepId is required");
    }

    [Fact]
    public async Task HandleSignal_ShouldRejectNonRunActor()
    {
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.ActorNotWorkflowRun("actor-1", "run-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                SignalName = "approve",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("not a workflow run actor");
        service.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleSignal_ShouldRejectMissingFields()
    {
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>();
        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "",
                SignalName = "approve",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("actorId, runId and signalName are required");
    }

    [Fact]
    public async Task HandleSignal_ShouldRejectMismatchedRunId()
    {
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.RunBindingMismatch("actor-1", "run-other", "run-expected")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-other",
                SignalName = "approve",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("run-expected");
        service.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleSignal_ShouldMapInvalidSignalName_FromApplicationLayer()
    {
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidSignalName("actor-1", "run-1", " ")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                SignalName = "approve",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("signalName is required");
    }

    [Fact]
    public async Task HandleSignal_ShouldForwardStepId_WhenProvided()
    {
        var receipt = new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "signal-cmd-1", "corr-1");
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(receipt),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "wait-approval",
                SignalName = "approval",
                Payload = "approved",
                CommandId = "signal-cmd-1",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be("actor-1");
        service.Commands.Single().RunId.Should().Be("run-1");
        service.Commands.Single().SignalName.Should().Be("approval");
        service.Commands.Single().Payload.Should().Be("approved");
        service.Commands.Single().StepId.Should().Be("wait-approval");
        service.Commands.Single().CommandId.Should().Be("signal-cmd-1");
        body.Should().Contain("wait-approval");
    }

    [Fact]
    public async Task HandleSignal_ShouldDispatchCommand_AndGenerateCommandId_WhenMissing()
    {
        var receipt = new WorkflowRunControlAcceptedReceipt(
            "actor-1",
            "run-1",
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var service = new RecordingDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(receipt),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                SignalName = "approve",
                Payload = "yes",
            },
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.Commands.Should().ContainSingle();
        service.Commands.Single().ActorId.Should().Be("actor-1");
        service.Commands.Single().RunId.Should().Be("run-1");
        service.Commands.Single().SignalName.Should().Be("approve");
        service.Commands.Single().Payload.Should().Be("yes");
        service.Commands.Single().CommandId.Should().BeNull();
        service.Commands.Single().StepId.Should().BeNull();
        body.Should().Contain(receipt.CommandId);
        body.Should().Contain("\"accepted\":true");
    }

    [Fact]
    public async Task HandleChatWebSocket_ShouldRejectNonWebSocketRequests()
    {
        var http = CreateHttpContext();

        await WorkflowCapabilityEndpoints.HandleChatWebSocket(
            http,
            new FakeCommandInteractionService(),
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
            new FakeCommandInteractionService(),
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
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = (_, _, _, _) => throw new InvalidOperationException("boom"),
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

    private sealed class FakeCommandInteractionService
        : ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>
    {
        public Func<WorkflowChatRunRequest, Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask>, Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>>> ResultFactory { get; set; } =
            (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Failure(WorkflowChatRunStartError.AgentNotFound));

        public Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default) =>
            ResultFactory(request, emitAsync, onAcceptedAsync, ct);
    }

    private sealed class FakeCommandDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.AgentNotFound);

        public Exception? DispatchException { get; set; }
        public WorkflowChatRunRequest? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            ct.ThrowIfCancellationRequested();
            if (DispatchException != null)
                throw DispatchException;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingDispatchService<TCommand, TReceipt, TError>
        : ICommandDispatchService<TCommand, TReceipt, TError>
    {
        public List<TCommand> Commands { get; } = [];

        public CommandDispatchResult<TReceipt, TError> Result { get; set; } =
            CommandDispatchResult<TReceipt, TError>.Failure(default!);

        public Exception? DispatchException { get; set; }

        public Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
            TCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (DispatchException != null)
                throw DispatchException;
            return Task.FromResult(Result);
        }
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
