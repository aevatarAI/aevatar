using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class WorkflowRunDeliveryProjectionSinkTests
{
    [Fact]
    public async Task PushAsync_ShouldPublishTerminalContinuationThroughDispatchPort()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        await using var sink = new WorkflowRunDeliveryProjectionSink(
            dispatchPort,
            "workflow-run-delivery:workflow-actor:wf-command",
            "workflow-run-delivery:workflow-actor:wf-command",
            "workflow-actor",
            "wf-command",
            TimeProvider.System,
            NullLogger.Instance);

        await sink.PushAsync(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "workflow output" }),
            },
        });

        dispatchPort.Calls.Should().ContainSingle();
        var call = dispatchPort.Calls.Single();
        call.ActorId.Should().Be("workflow-run-delivery:workflow-actor:wf-command");
        call.Envelope.Route.GetTargetActorId().Should().Be("workflow-run-delivery:workflow-actor:wf-command");
        call.Envelope.Propagation.CorrelationId.Should().Be("wf-command");
        var command = call.Envelope.Payload.Unpack<WorkflowRunDeliveryTerminalFrameObserved>();
        command.DeliveryId.Should().Be("workflow-run-delivery:workflow-actor:wf-command");
        command.WorkflowActorId.Should().Be("workflow-actor");
        command.WorkflowCommandId.Should().Be("wf-command");
        command.Status.Should().Be("completed");
        command.Text.Should().Be("workflow output");
        command.ErrorCode.Should().BeEmpty();
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }
}
