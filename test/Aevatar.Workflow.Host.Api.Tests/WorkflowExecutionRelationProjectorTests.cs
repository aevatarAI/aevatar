using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionRelationProjectorTests
{
    [Fact]
    public async Task ProjectAsync_WhenStepIdIsBlank_ShouldSkipContainsStepEdge()
    {
        var relationStore = new InMemoryProjectionRelationStore();
        var projector = new WorkflowExecutionRelationProjector(relationStore);
        var context = CreateContext();

        await projector.InitializeAsync(context);
        await projector.ProjectAsync(context, Wrap(new StepRequestEvent
        {
            StepId = "   ",
            StepType = "llm_call",
            TargetRole = "assistant",
        }));

        var runEdges = await relationStore.GetNeighborsAsync(new ProjectionRelationQuery
        {
            Scope = WorkflowExecutionRelationConstants.Scope,
            RootNodeId = BuildRunNodeId(context),
            Direction = ProjectionRelationDirection.Outbound,
            Take = 50,
        });

        runEdges.Should().NotContain(x =>
            string.Equals(x.RelationType, WorkflowExecutionRelationConstants.RelationContainsStep, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectAsync_ShouldUpsertStepNodeFromRequestAndCompletionEvents()
    {
        var relationStore = new InMemoryProjectionRelationStore();
        var projector = new WorkflowExecutionRelationProjector(relationStore);
        var context = CreateContext();
        var requestTime = new DateTime(2026, 2, 24, 8, 0, 0, DateTimeKind.Utc);
        var completedTime = requestTime.AddSeconds(5);

        await projector.InitializeAsync(context);
        await projector.ProjectAsync(context, Wrap(new StepRequestEvent
        {
            StepId = "step-1",
            StepType = "llm_call",
            TargetRole = "assistant",
        }, requestTime));
        await projector.ProjectAsync(context, Wrap(new StepCompletedEvent
        {
            StepId = "step-1",
            WorkerId = "assistant-1",
            Success = true,
            Output = "done",
        }, completedTime));

        var subgraph = await relationStore.GetSubgraphAsync(new ProjectionRelationQuery
        {
            Scope = WorkflowExecutionRelationConstants.Scope,
            RootNodeId = BuildRunNodeId(context),
            Direction = ProjectionRelationDirection.Outbound,
            Depth = 2,
            Take = 50,
        });

        subgraph.Edges.Should().ContainSingle(x =>
            string.Equals(x.RelationType, WorkflowExecutionRelationConstants.RelationContainsStep, StringComparison.Ordinal));
        var stepNode = subgraph.Nodes.Single(x =>
            string.Equals(x.NodeType, WorkflowExecutionRelationConstants.StepNodeType, StringComparison.Ordinal));
        stepNode.NodeId.Should().Be("step:root:cmd-1:step-1");
        stepNode.Properties["commandId"].Should().Be("cmd-1");
        stepNode.Properties["stepType"].Should().Be("llm_call");
        stepNode.Properties["targetRole"].Should().Be("assistant");
        stepNode.Properties["workerId"].Should().Be("assistant-1");
        stepNode.Properties["success"].Should().Be("True");
        stepNode.UpdatedAt.Should().Be(new DateTimeOffset(completedTime));
    }

    [Fact]
    public async Task CompleteAsync_WhenTopologyContainsBlankNodes_ShouldSkipUnknownRelations()
    {
        var relationStore = new InMemoryProjectionRelationStore();
        var projector = new WorkflowExecutionRelationProjector(relationStore);
        var context = CreateContext();

        await projector.InitializeAsync(context);
        await projector.CompleteAsync(
            context,
            [
                new WorkflowExecutionTopologyEdge("root-1", "worker-1"),
                new WorkflowExecutionTopologyEdge("   ", "worker-2"),
                new WorkflowExecutionTopologyEdge("root-2", "  "),
            ]);

        var subgraph = await relationStore.GetSubgraphAsync(new ProjectionRelationQuery
        {
            Scope = WorkflowExecutionRelationConstants.Scope,
            RootNodeId = "root-1",
            Direction = ProjectionRelationDirection.Both,
            Depth = 2,
            Take = 100,
        });

        subgraph.Nodes.Should().NotContain(x => string.Equals(x.NodeId, "unknown", StringComparison.Ordinal));
        subgraph.Edges.Should().Contain(x =>
            string.Equals(x.RelationType, WorkflowExecutionRelationConstants.RelationChildOf, StringComparison.Ordinal) &&
            string.Equals(x.FromNodeId, "root-1", StringComparison.Ordinal) &&
            string.Equals(x.ToNodeId, "worker-1", StringComparison.Ordinal));
        subgraph.Edges.Should().NotContain(x =>
            string.Equals(x.FromNodeId, "unknown", StringComparison.Ordinal) ||
            string.Equals(x.ToNodeId, "unknown", StringComparison.Ordinal));
    }

    private static WorkflowExecutionProjectionContext CreateContext() => new()
    {
        ProjectionId = "projection-relation",
        CommandId = "cmd-1",
        RootActorId = "root",
        WorkflowName = "direct",
        StartedAt = new DateTimeOffset(2026, 2, 24, 7, 0, 0, TimeSpan.Zero),
        Input = "hello",
    };

    private static EventEnvelope Wrap(IMessage evt, DateTime? utcTimestamp = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime((utcTimestamp ?? DateTime.UtcNow).ToUniversalTime()),
        Payload = Any.Pack(evt),
        PublisherId = "root",
        Direction = EventDirection.Down,
    };

    private static string BuildRunNodeId(WorkflowExecutionProjectionContext context) =>
        $"run:{context.RootActorId}:{context.CommandId}";
}
