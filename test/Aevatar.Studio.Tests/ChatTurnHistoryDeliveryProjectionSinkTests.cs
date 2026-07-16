using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class ChatTurnHistoryDeliveryProjectionSinkTests
{
    [Fact]
    public void TerminalResult_FromRunFinished_ShouldMapCompletedOutput()
    {
        var result = ChatTurnTerminalResult.From(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                Result = Any.Pack(new WorkflowRunResultPayload { Output = " assistant final " }),
            },
        });

        result.Should().NotBeNull();
        result!.Status.Should().Be(ChatTurnTerminalStatus.Completed);
        result.Text.Should().Be("assistant final");
        result.ErrorCode.Should().BeEmpty();
    }

    [Fact]
    public void TerminalResult_FromRunError_ShouldMapFailedError()
    {
        var result = ChatTurnTerminalResult.From(new WorkflowRunEventEnvelope
        {
            RunError = new WorkflowRunErrorEventPayload
            {
                Message = " failed safely ",
                Code = "ERR",
            },
        });

        result.Should().NotBeNull();
        result!.Status.Should().Be(ChatTurnTerminalStatus.Failed);
        result.Text.Should().Be("failed safely");
        result.ErrorCode.Should().Be("ERR");
    }

    [Fact]
    public void TerminalResult_FromRunStopped_ShouldMapStoppedWithoutAssistantText()
    {
        var result = ChatTurnTerminalResult.From(new WorkflowRunEventEnvelope
        {
            RunStopped = new WorkflowRunStoppedEventPayload { Reason = " user stopped " },
        });

        result.Should().NotBeNull();
        result!.Status.Should().Be(ChatTurnTerminalStatus.Stopped);
        result.Text.Should().BeEmpty();
        result.ErrorCode.Should().Be("user stopped");
    }

    [Fact]
    public async Task PushAsync_WhenTerminalFrameArrives_ShouldDispatchDeliveryContinuation()
    {
        var dispatch = new RecordingActorDispatchPort();
        var sink = new ChatTurnHistoryDeliveryProjectionSink(
            dispatch,
            "delivery-actor",
            "delivery-1",
            "workflow-actor",
            "workflow-command",
            TimeProvider.System,
            NullLogger.Instance);

        await sink.PushAsync(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "done" }),
            },
        });

        dispatch.Calls.Should().ContainSingle();
        var call = dispatch.Calls.Single();
        call.ActorId.Should().Be("delivery-actor");
        var command = call.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryTerminalFrameObserved>();
        command.DeliveryId.Should().Be("delivery-1");
        command.WorkflowActorId.Should().Be("workflow-actor");
        command.WorkflowCommandId.Should().Be("workflow-command");
        command.Status.Should().Be(ChatTurnTerminalStatus.Completed);
        command.Text.Should().Be("done");
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope));
            return Task.FromResult(new DispatchAdmission(
                true,
                actorId,
                DateTimeOffset.UtcNow,
                envelope.Id,
                envelope.Propagation?.CorrelationId ?? string.Empty));
        }
    }
}
