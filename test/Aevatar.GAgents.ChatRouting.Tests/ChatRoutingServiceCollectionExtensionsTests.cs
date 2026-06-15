using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatRouting.Tests;

/// <summary>
/// Guards the ChatRoutePolicy projection wiring. The earlier Phase 1 cut only
/// registered the document store, which left the projector with nowhere to
/// subscribe — committed <c>ChatRoutePolicyUpdated</c> events landed in event
/// storage but never materialized into <see cref="ChatRoutePolicyCurrentStateDocument"/>.
/// These assertions lock in the full materialization-runtime + materializer +
/// document-store triple so the readmodel actually populates.
/// </summary>
// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Mainnet Host endpoints inject IActorRuntime/IActorDispatchPort and build EventEnvelope + dispatch directly in Host code.
//   New principle: Host calls Application command ports that normalize, resolve target, build envelope, dispatch, return honest accepted receipt.
//   Host endpoint stays minimal (auth + body parsing). NO direct dependency on IActorRuntime/IActorDispatchPort in Host.
public sealed class ChatRoutingServiceCollectionExtensionsTests
{
    [Fact]
    public void AddChatRoutingAgents_RegistersChatRoutePolicyDocumentStore()
    {
        using var provider = new ServiceCollection()
            .AddChatRoutingAgents()
            .BuildServiceProvider();

        provider.GetService<IProjectionDocumentReader<ChatRoutePolicyCurrentStateDocument, string>>()
            .Should().NotBeNull("the readmodel cannot be queried without its document reader");
        provider.GetService<IProjectionDocumentWriter<ChatRoutePolicyCurrentStateDocument>>()
            .Should().NotBeNull("the projector cannot upsert without its document writer");
    }

    [Fact]
    public void AddChatRoutingAgents_RegistersMaterializationRuntimeAndProjector()
    {
        using var provider = new ServiceCollection()
            .AddChatRoutingAgents()
            .BuildServiceProvider();

        provider.GetService<Func<ProjectionRuntimeScopeKey, ChatRoutePolicyMaterializationContext>>()
            .Should().NotBeNull("the materialization runtime needs a scope-key → context factory");
        provider.GetService<ChatRoutePolicyCurrentStateProjector>()
            .Should().NotBeNull("the current-state projector must be resolvable as a concrete singleton");
        provider.GetService<ICurrentStateProjectionMaterializer<ChatRoutePolicyMaterializationContext>>()
            .Should().NotBeNull(
                "without the materializer subscription, committed ChatRoutePolicyUpdated events would " +
                "land in event storage but never reach ChatRoutePolicyCurrentStateDocument");
    }

    [Fact]
    public void AddChatRoutingAgents_RegistersCommittedStateProjectionActivationHook()
    {
        using var provider = new ServiceCollection()
            .AddChatRoutingAgents()
            .BuildServiceProvider();

        provider.GetService<ProjectionActivationPlanDispatcher>()
            .Should().NotBeNull("the committed-state hook dispatches activation plans through the shared dispatcher");
        provider.GetServices<ICommittedStatePublicationHook>()
            .Should().ContainSingle(hook => hook is CommittedStateProjectionActivationHook);
        provider.GetServices<IProjectionActivationPlanProvider>()
            .Should().ContainSingle(planProvider =>
                planProvider is ChatRoutePolicyCommittedStateProjectionActivationPlanProvider);
    }

    [Fact]
    public void AddChatRoutingAgents_RegistersChatRoutePolicyCommandPort()
    {
        var services = new ServiceCollection()
            .AddChatRoutingAgents();

        services.Should().ContainSingle(
            descriptor => descriptor.ServiceType == typeof(IChatRoutePolicyCommandPort),
                "Mainnet Host endpoint must call the feature command port instead of actor runtime/dispatch directly");
    }

    [Fact]
    public void AddChatRoutingAgents_ContextFactory_MirrorsScopeKey()
    {
        using var provider = new ServiceCollection()
            .AddChatRoutingAgents()
            .BuildServiceProvider();

        var factory = provider.GetRequiredService<
            Func<ProjectionRuntimeScopeKey, ChatRoutePolicyMaterializationContext>>();

        var key = new ProjectionRuntimeScopeKey(
            "chat-route-policy:scope-1",
            ChatRoutePolicyGAgent.ProjectionKind,
            ProjectionRuntimeMode.DurableMaterialization);

        var context = factory(key);

        context.RootActorId.Should().Be("chat-route-policy:scope-1");
        context.ProjectionKind.Should().Be(ChatRoutePolicyGAgent.ProjectionKind);
    }
}
