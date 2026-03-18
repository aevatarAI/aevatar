using Aevatar.Workflow.Projection;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionProjectionContextTests
{
    [Fact]
    public void ConstructorShape_ShouldExposeOnlySessionScopeFields()
    {
        var context = new WorkflowExecutionProjectionContext
        {
            SessionId = "cmd-1",
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution",
        };

        context.SessionId.Should().Be("cmd-1");
        context.RootActorId.Should().Be("actor-1");
        context.ProjectionKind.Should().Be("workflow-execution");
    }

    [Fact]
    public void DifferentSessions_ShouldShareRootActorButKeepIndependentSessionIds()
    {
        var first = new WorkflowExecutionProjectionContext
        {
            SessionId = "cmd-1",
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution",
        };
        var second = new WorkflowExecutionProjectionContext
        {
            SessionId = "cmd-2",
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution",
        };

        first.RootActorId.Should().Be(second.RootActorId);
        first.SessionId.Should().NotBe(second.SessionId);
    }
}
