using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using SystemType = System.Type;

namespace Aevatar.Studio.Tests;

public sealed class StudioCommittedStateProjectionActivationPlanProviderTests
{
    [Theory]
    [MemberData(nameof(StudioProjectedActors))]
    public void GetPlans_ShouldMapStudioProjectedActorToDurableStudioMaterialization(
        SystemType actorType,
        string projectionKind)
    {
        var provider = new StudioCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(actorType, "studio-actor-1")).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(StudioMaterializationRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("studio-actor-1");
        plans[0].StartRequest.ProjectionKind.Should().Be(projectionKind);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldIgnoreUnsupportedActorsAndMissingPayload()
    {
        var provider = new StudioCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(typeof(string), "actor-1"))
            .Should().BeEmpty();
        provider.GetPlans(new CommittedStatePublicationContext
            {
                ActorId = "actor-1",
                ActorType = typeof(UserConfigGAgent),
                Published = new CommittedStateEventPublished(),
            })
            .Should().BeEmpty();
    }

    [Fact]
    public async Task CommittedStateHook_ShouldDispatchStudioActivationPlanThroughRegisteredLeaseService()
    {
        var activation = new RecordingStudioActivationService();
        var services = new ServiceCollection()
            .AddSingleton<IProjectionScopeActivationService<StudioMaterializationRuntimeLease>>(activation)
            .BuildServiceProvider();
        var hook = new CommittedStateProjectionActivationHook(
            [new StudioCommittedStateProjectionActivationPlanProvider()],
            new ProjectionActivationPlanDispatcher(services));

        await hook.BeforePublishAsync(
            BuildContext(typeof(UserConfigGAgent), "studio-actor-1"),
            CancellationToken.None);

        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("studio-actor-1");
        activation.Requests[0].ProjectionKind.Should().Be(UserConfigGAgent.ProjectionKind);
        activation.Requests[0].Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
        activation.Requests[0].SessionId.Should().BeEmpty();
    }

    public static TheoryData<SystemType, string> StudioProjectedActors() =>
        new()
        {
            { typeof(UserConfigGAgent), UserConfigGAgent.ProjectionKind },
            { typeof(GAgentRegistryGAgent), GAgentRegistryGAgent.ProjectionKind },
            { typeof(ConnectorCatalogGAgent), ConnectorCatalogGAgent.ProjectionKind },
            { typeof(RoleCatalogGAgent), RoleCatalogGAgent.ProjectionKind },
            { typeof(UserMemoryGAgent), UserMemoryGAgent.ProjectionKind },
            { typeof(ChatConversationGAgent), ChatConversationGAgent.ProjectionKind },
            { typeof(ChatTurnHistoryDeliveryGAgent), ChatTurnHistoryDeliveryGAgent.ProjectionKind },
            { typeof(NyxIdChatConversationGAgent), NyxIdChatConversationGAgent.ProjectionKind },
            { typeof(StudioMemberGAgent), StudioMemberGAgent.ProjectionKind },
            { typeof(StudioMemberBindingRunGAgent), StudioMemberBindingRunGAgent.ProjectionKind },
            { typeof(StudioTeamGAgent), StudioTeamGAgent.ProjectionKind },
            { typeof(StudioWorkspaceGAgent), StudioWorkspaceGAgent.ProjectionKind },
        };

    private static CommittedStatePublicationContext BuildContext(SystemType actorType, string actorId) =>
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
                    EventData = Any.Pack(new StringValue { Value = "event" }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };

    private sealed class RecordingStudioActivationService
        : IProjectionScopeActivationService<StudioMaterializationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<StudioMaterializationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new StudioMaterializationRuntimeLease(
                new StudioMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                }));
        }
    }
}
