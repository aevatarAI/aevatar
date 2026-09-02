using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkflowBoardProjectionActivationPlanProviderTests
{
    [Fact]
    public void GetPlans_ShouldMapWorkflowRunCommittedEventToStudioBoardMaterialization()
    {
        var provider = new StudioWorkflowBoardProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(typeof(WorkflowRunGAgent), "workflow-run-1")).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(StudioWorkflowBoardMaterializationRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("workflow-run-1");
        plans[0].StartRequest.ProjectionKind.Should()
            .Be(StudioWorkflowBoardProjectionActivationPlanProvider.ProjectionKind);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldIgnoreUnsupportedActorsAndMissingPayload()
    {
        var provider = new StudioWorkflowBoardProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(typeof(string), "actor-1")).Should().BeEmpty();
        provider.GetPlans(new CommittedStatePublicationContext
            {
                ActorId = "workflow-run-1",
                ActorType = typeof(WorkflowRunGAgent),
                Published = new CommittedStateEventPublished(),
            })
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task CommittedStateHook_ShouldDispatchStudioBoardActivationPlanThroughRegisteredLeaseService()
    {
        var activation = new RecordingBoardActivationService();
        var services = new ServiceCollection()
            .AddSingleton<IProjectionScopeActivationService<StudioWorkflowBoardMaterializationRuntimeLease>>(activation)
            .BuildServiceProvider();
        var hook = new CommittedStateProjectionActivationHook(
            [new StudioWorkflowBoardProjectionActivationPlanProvider()],
            new ProjectionActivationPlanDispatcher(services));

        await hook.BeforePublishAsync(
            BuildContext(typeof(WorkflowRunGAgent), "workflow-run-1"),
            CancellationToken.None);

        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("workflow-run-1");
        activation.Requests[0].ProjectionKind.Should()
            .Be(StudioWorkflowBoardProjectionActivationPlanProvider.ProjectionKind);
        activation.Requests[0].Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    private static CommittedStatePublicationContext BuildContext(System.Type actorType, string actorId) =>
        new()
        {
            ActorId = actorId,
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = actorId,
                    EventId = "evt-1",
                    EventData = Any.Pack(new StepRequestEvent { RunId = "run-1", StepId = "step-1" }),
                },
                StateRoot = Any.Pack(new WorkflowRunState { RunId = "run-1", Status = "running" }),
            },
        };

    private sealed class RecordingBoardActivationService
        : IProjectionScopeActivationService<StudioWorkflowBoardMaterializationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<StudioWorkflowBoardMaterializationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new StudioWorkflowBoardMaterializationRuntimeLease(
                new StudioWorkflowBoardMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                }));
        }
    }
}
