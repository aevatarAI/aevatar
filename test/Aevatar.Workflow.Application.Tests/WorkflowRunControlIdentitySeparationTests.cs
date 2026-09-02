using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunControlIdentitySeparationTests
{
    [Fact]
    public async Task ResumeControlCommand_ShouldKeepActorRunCommandAndCorrelationIdentitiesSeparate()
    {
        // Refactor (issue1354): Old pattern: workflow run control could be read as sharing one identity across actor routing, run binding, command envelope, and tracing. New principle: actorId addresses the actor, runId names the bound workflow run fact, commandId names the accepted command/envelope, and correlationId remains propagation trace context.
        const string actorId = "workflow-run-actor-1";
        const string runId = "workflow-run-fact-1";
        const string commandId = "resume-command-1";
        const string correlationId = "resume-correlation-1";

        var resolver = new WorkflowResumeCommandTargetResolver(new BindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                actorId,
                "definition-1",
                runId,
                "direct",
                "yaml",
                new Dictionary<string, string>(), ExternalCapabilityExecutionMode.Interactive)));

        var command = new WorkflowResumeCommand(
            actorId,
            runId,
            "step-1",
            commandId,
            true,
            "approved",
            CorrelationId: correlationId);

        var resolution = await resolver.ResolveAsync(command, CancellationToken.None);
        resolution.Succeeded.Should().BeTrue();

        var target = resolution.Target!;
        var context = new CommandContext(target.TargetId, commandId, correlationId, new Dictionary<string, string>());
        var receipt = new WorkflowRunControlAcceptedReceiptFactory().Create(target, context);
        var envelope = new WorkflowResumeCommandEnvelopeFactory().CreateEnvelope(command, context);
        var resumed = envelope.Payload.Unpack<WorkflowResumedEvent>();

        target.ActorId.Should().Be(actorId);
        target.TargetId.Should().Be(actorId);
        target.RunId.Should().Be(runId);

        receipt.ActorId.Should().Be(actorId);
        receipt.RunId.Should().Be(runId);
        receipt.CommandId.Should().Be(commandId);
        receipt.CorrelationId.Should().Be(correlationId);

        envelope.Id.Should().Be(commandId);
        envelope.Route!.GetTargetActorId().Should().Be(actorId);
        envelope.Propagation!.CorrelationId.Should().Be(correlationId);
        resumed.RunId.Should().Be(runId);
    }

    private sealed class BindingReader(WorkflowActorBinding binding) : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            actorId.Should().Be(binding.ActorId);
            return Task.FromResult<WorkflowActorBinding?>(binding);
        }
    }
}
