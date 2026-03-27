using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Application.DependencyInjection;
using Aevatar.GroupChat.Projection.DependencyInjection;
using Aevatar.GroupChat.Projection.Orchestration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GroupChat.Tests.Hosting;

public sealed class GroupChatDependencyInjectionTests
{
    [Fact]
    public void AddGroupChatProjection_ShouldReplaceNoOpParticipantReplyProjectionPort()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime, StubActorRuntime>();
        services.AddSingleton<IActorDispatchPort, StubActorDispatchPort>();
        services.AddGroupChatApplication();
        services.AddGroupChatProjection();

        using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IGroupParticipantReplyProjectionPort>()
            .Should()
            .BeOfType<GroupParticipantReplyProjectionPort>();
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubActorDispatchPort : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
