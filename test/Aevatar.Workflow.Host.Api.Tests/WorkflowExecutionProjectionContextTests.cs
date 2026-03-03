
using Aevatar.Workflow.Projection;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionProjectionContextTests
{
    [Fact]
    public void UpdateRunMetadata_WhenCommandAndWorkflowBlank_ShouldKeepExistingValues()
    {
        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "actor-1",
            CommandId = "cmd-1",
            RootActorId = "actor-1",
            WorkflowName = "wf-1",
            StartedAt = DateTimeOffset.Parse("2026-02-21T00:00:00Z"),
            Input = "input-1",
        };

        var updatedAt = DateTimeOffset.Parse("2026-02-21T01:00:00Z");
        context.UpdateRunMetadata("", "  ", "input-2", updatedAt);

        context.CommandId.Should().Be("cmd-1");
        context.WorkflowName.Should().Be("wf-1");
        context.Input.Should().Be("input-2");
        context.StartedAt.Should().Be(updatedAt);
    }

    [Fact]
    public void UpdateRunMetadata_WhenValuesProvided_ShouldReplaceAndExposeProjectionId()
    {
        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "actor-2",
            CommandId = "cmd-1",
            RootActorId = "actor-2",
            WorkflowName = "wf-1",
            StartedAt = DateTimeOffset.Parse("2026-02-21T00:00:00Z"),
            Input = "input-1",
        };

        var updatedAt = DateTimeOffset.Parse("2026-02-21T02:00:00Z");
        context.UpdateRunMetadata("cmd-2", "wf-2", "input-2", updatedAt);
        context.StreamSubscriptionLease = new DummyStreamLease();

        context.CommandId.Should().Be("cmd-2");
        context.WorkflowName.Should().Be("wf-2");
        context.Input.Should().Be("input-2");
        context.StartedAt.Should().Be(updatedAt);
        ((IProjectionContext)context).ProjectionId.Should().Be("actor-2");
        context.StreamSubscriptionLease.Should().NotBeNull();
    }

    private sealed class DummyStreamLease : IActorStreamSubscriptionLease
    {
        public string ActorId => "actor";

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
