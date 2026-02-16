using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.RunIdResolvers;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Host.Api.Tests;

public class WorkflowExecutionRunIdResolverTests
{
    [Fact]
    public void WorkflowCoreRunIdResolver_ShouldResolveRunId_ForAllWorkflowCoreEvents()
    {
        var resolver = new WorkflowCoreRunIdResolver();

        Resolve(resolver, new StartWorkflowEvent { RunId = "r1", WorkflowName = "w", Input = "x" })
            .Should().Be("r1");

        Resolve(resolver, new StepRequestEvent { RunId = "r2", StepId = "s", StepType = "llm_call" })
            .Should().Be("r2");

        Resolve(resolver, new StepCompletedEvent { RunId = "r3", StepId = "s", Success = true, Output = "o" })
            .Should().Be("r3");

        Resolve(resolver, new WorkflowSuspendedEvent { RunId = "r4", StepId = "s", SuspensionType = "human_input" })
            .Should().Be("r4");

        Resolve(resolver, new WorkflowResumedEvent { RunId = "r5", StepId = "s", Approved = true, UserInput = "ok" })
            .Should().Be("r5");

        Resolve(resolver, new WorkflowCompletedEvent { RunId = "r6", WorkflowName = "w", Success = true, Output = "done" })
            .Should().Be("r6");
    }

    [Fact]
    public void WorkflowCoreRunIdResolver_UnknownPayload_ShouldReturnFalse()
    {
        var resolver = new WorkflowCoreRunIdResolver();
        var envelope = Wrap(new ParentChangedEvent { OldParent = "a", NewParent = "b" });

        resolver.TryResolve(envelope, out var runId).Should().BeFalse();
        runId.Should().BeNull();
    }

    private static string Resolve(IWorkflowExecutionRunIdResolver resolver, IMessage evt)
    {
        var envelope = Wrap(evt);
        resolver.TryResolve(envelope, out var runId).Should().BeTrue();
        runId.Should().NotBeNullOrWhiteSpace();
        return runId!;
    }

    private static EventEnvelope Wrap(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = "test",
        Direction = EventDirection.Down,
    };
}

