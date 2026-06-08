using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.RunForks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunForkCoordinatorTests
{
    [Fact]
    public async Task BeforePublishAsync_WhenCommittedForkRequestedEvent_ShouldCallForkService()
    {
        var forkService = new RecordingForkRunService();
        var coordinator = new WorkflowRunForkCoordinator(forkService);
        var requested = new WorkflowRunForkRequestedEvent
        {
            SourceRunId = "run-source",
            StartAtStepId = "failed-step",
            Attempt = 2,
            ScopeId = "scope-1",
        };

        await coordinator.BeforePublishAsync(CreateContext(requested), CancellationToken.None);

        forkService.Commands.Should().ContainSingle();
        var command = forkService.Commands.Single();
        command.SourceRunId.Should().Be("run-source");
        command.StartAtStepId.Should().Be("failed-step");
        command.InlineYaml.Should().BeNull();
        command.Attempt.Should().Be(2);
    }

    private static CommittedStatePublicationContext CreateContext(IMessage evt) =>
        new()
        {
            ActorId = "run-source",
            ActorType = typeof(object),
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "run-source",
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = 1,
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                },
            },
        };

    private sealed class RecordingForkRunService : IWorkflowForkRunService
    {
        public List<WorkflowForkRunCommand> Commands { get; } = [];

        public Task<WorkflowForkRunResult> ForkAsync(
            WorkflowForkRunCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(WorkflowForkRunResult.Accepted(new WorkflowForkRunAcceptedReceipt(
                command.SourceRunId,
                "new-run",
                "wf",
                true,
                "cmd",
                "corr",
                DateTimeOffset.UtcNow)));
        }
    }
}
