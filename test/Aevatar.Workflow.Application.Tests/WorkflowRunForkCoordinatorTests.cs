using Aevatar.CQRS.Core.Abstractions.Commands;
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
    public async Task BeforePublishAsync_WhenCommittedEventIsNotForkRequest_ShouldNotDispatchForkCommand()
    {
        var forkDispatchService = new RecordingForkDispatchService();
        var coordinator = new WorkflowRunForkCoordinator(forkDispatchService);

        await coordinator.BeforePublishAsync(
            CreateContext(new StringValue { Value = "ignored" }),
            CancellationToken.None);

        forkDispatchService.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_WhenCommittedForkRequestedEvent_ShouldDispatchForkCommand()
    {
        var forkDispatchService = new RecordingForkDispatchService();
        var coordinator = new WorkflowRunForkCoordinator(forkDispatchService);
        var requested = new WorkflowRunForkRequestedEvent
        {
            SourceRunId = "run-source",
            StartAtStepId = "failed-step",
            Attempt = 2,
            ScopeId = "scope-1",
        };

        await coordinator.BeforePublishAsync(CreateContext(requested), CancellationToken.None);

        forkDispatchService.Commands.Should().ContainSingle();
        var command = forkDispatchService.Commands.Single();
        command.SourceRunId.Should().Be("run-source");
        command.StartAtStepId.Should().Be("failed-step");
        command.InlineYaml.Should().BeNull();
        command.Attempt.Should().Be(2);
        command.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task BeforePublishAsync_WhenForkAccepted_ShouldLeaveLineageRecordingToForkCommandDispatch()
    {
        var forkDispatchService = new RecordingForkDispatchService
        {
            Receipt = new WorkflowForkRunAcceptedReceipt(
                "run-source-gamma",
                "actor-child-delta",
                "wf",
                true,
                "cmd",
                "corr",
                DateTimeOffset.UtcNow,
                "run-child-beta",
                "run-original-alpha"),
        };
        var coordinator = new WorkflowRunForkCoordinator(
            new Lazy<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>>(
                () => forkDispatchService));

        await coordinator.BeforePublishAsync(
            CreateContext(new WorkflowRunForkRequestedEvent
            {
                SourceRunId = "run-source-gamma",
                StartAtStepId = "step-retry",
                Attempt = 4,
                ScopeId = "scope-alpha",
            }, actorId: "actor-source-epsilon"),
            CancellationToken.None);

        forkDispatchService.Commands.Should().ContainSingle();
    }

    private static CommittedStatePublicationContext CreateContext(IMessage evt, string actorId = "run-source") =>
        new()
        {
            ActorId = actorId,
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

    private sealed class RecordingForkDispatchService
        : ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>
    {
        public List<WorkflowForkRunCommand> Commands { get; } = [];

        public WorkflowForkRunAcceptedReceipt? Receipt { get; init; }

        public Task<CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>> DispatchAsync(
            WorkflowForkRunCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(CommandDispatchResult<WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>.Success(
                Receipt ?? new WorkflowForkRunAcceptedReceipt(
                    command.SourceRunId,
                    "new-run",
                    "wf",
                    true,
                    "cmd",
                    "corr",
                    DateTimeOffset.UtcNow)));
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<RecordedDispatch> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatched.Add(new RecordedDispatch(actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed record RecordedDispatch(string ActorId, EventEnvelope Envelope);
}
