using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Core.EventSourcing;
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
// Refactor (iter32/cluster-034-chat-route-policy-request-path-projection-activation):
//   Old pattern: Chat route policy admin endpoints + voice demo bootstrap 在 request path 调 EnsureProjectionForActorAsync 同步 priming projection,违反 query-time priming forbidden + 命令骨架内聚
//   New principle: 加 ChatRoutePolicyCommittedStateProjectionActivationPlanProvider(committed-state hook 触发);删 ChatRoutePolicyProjectionPort + request-path activation;DI 注册 dispatcher + hook + provider;query_projection_priming_guard 加 chat route policy endpoint 扫描
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
