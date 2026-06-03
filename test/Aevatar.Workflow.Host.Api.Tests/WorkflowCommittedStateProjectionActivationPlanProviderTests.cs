using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCommittedStateProjectionActivationPlanProviderTests
{
    [Fact]
    public void GetPlans_ShouldMapWorkflowDefinitionBindToBindingScope()
    {
        var provider = new WorkflowCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(WorkflowGAgent),
            new BindWorkflowDefinitionEvent { WorkflowName = "wf" })).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(WorkflowBindingRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("workflow-actor");
        plans[0].StartRequest.ProjectionKind.Should().Be(WorkflowProjectionKinds.Binding);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldMapWorkflowRunBindToBindingAndExecutionMaterializationScopes()
    {
        var provider = new WorkflowCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(WorkflowRunGAgent),
            new BindWorkflowRunDefinitionEvent { RunId = "run-1" })).ToArray();

        plans.Should().HaveCount(2);
        plans.Select(x => x.LeaseType).Should().Equal(
            typeof(WorkflowBindingRuntimeLease),
            typeof(WorkflowExecutionMaterializationRuntimeLease));
        plans.Select(x => x.StartRequest.ProjectionKind).Should().Equal(
            WorkflowProjectionKinds.Binding,
            WorkflowProjectionKinds.ExecutionMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldNotMatchUnrelatedActorOrStateEvent()
    {
        var provider = new WorkflowCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(typeof(WorkflowGAgent), new StringValue { Value = "not-workflow" }))
            .Should().BeEmpty();
        provider.GetPlans(BuildContext(typeof(string), new BindWorkflowDefinitionEvent { WorkflowName = "wf" }))
            .Should().BeEmpty();
    }

    private static CommittedStatePublicationContext BuildContext(System.Type actorType, IMessage evt) =>
        new()
        {
            ActorId = "workflow-actor",
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "workflow-actor",
                    EventId = "evt-1",
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };
}
