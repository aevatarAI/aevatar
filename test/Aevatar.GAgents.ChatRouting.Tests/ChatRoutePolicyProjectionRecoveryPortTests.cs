using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatRouting.Tests;

/// <summary>
/// Coverage for the voice-side projection self-heal port. Verifies it re-materializes ONLY when the
/// source policy actor actually exists, so a policyless caller never leaves an empty projection-scope
/// grain behind, and that it never creates/activates the source policy grain.
/// </summary>
public sealed class ChatRoutePolicyProjectionRecoveryPortTests
{
    [Fact]
    public async Task TryRematerialize_WhenSourcePolicyExists_DispatchesForCorrectActorAndReturnsTrue()
    {
        var runtime = new StubActorRuntime(exists: true);
        var activation = new RecordingActivationService();
        using var provider = CreateProvider(runtime, activation);
        var port = provider.GetRequiredService<IChatRoutePolicyProjectionRecoveryPort>();

        var result = await port.TryRematerializeAsync(OwnerScope.ForNyxIdNative("user-42"));

        result.Should().BeTrue();
        runtime.ExistsCalls.Should().ContainSingle().Which.Should().Be("chat-route-policy:user-42");
        activation.Calls.Should().Be(1);
        activation.LastRequest!.RootActorId.Should().Be("chat-route-policy:user-42");
        activation.LastRequest.ProjectionKind.Should().Be("chat-route-policy");
        activation.LastRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public async Task TryRematerialize_WhenSourcePolicyMissing_SkipsDispatchAndReturnsFalse()
    {
        // No policy was ever written for this scope. The guard must skip the dispatch so no
        // projection-scope grain is created for a policyless caller.
        var runtime = new StubActorRuntime(exists: false);
        var activation = new RecordingActivationService();
        using var provider = CreateProvider(runtime, activation);
        var port = provider.GetRequiredService<IChatRoutePolicyProjectionRecoveryPort>();

        var result = await port.TryRematerializeAsync(OwnerScope.ForNyxIdNative("user-99"));

        result.Should().BeFalse();
        runtime.ExistsCalls.Should().ContainSingle().Which.Should().Be("chat-route-policy:user-99");
        activation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task TryRematerialize_WhenNoNyxUserId_ReturnsFalseWithoutTouchingActorOrDispatch()
    {
        var runtime = new StubActorRuntime(exists: true);
        var activation = new RecordingActivationService();
        using var provider = CreateProvider(runtime, activation);
        var port = provider.GetRequiredService<IChatRoutePolicyProjectionRecoveryPort>();

        var result = await port.TryRematerializeAsync(new OwnerScope());

        result.Should().BeFalse();
        runtime.ExistsCalls.Should().BeEmpty();
        activation.Calls.Should().Be(0);
    }

    private static ServiceProvider CreateProvider(IActorRuntime actorRuntime, RecordingActivationService activation)
    {
        var services = new ServiceCollection();
        services.AddSingleton(actorRuntime);
        services.AddChatRoutingAgents();
        // Registered last so the real ProjectionActivationPlanDispatcher resolves this recording fake
        // instead of the production materialization activation service.
        services.AddSingleton<IProjectionScopeActivationService<ChatRoutePolicyMaterializationRuntimeLease>>(activation);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingActivationService
        : IProjectionScopeActivationService<ChatRoutePolicyMaterializationRuntimeLease>
    {
        public int Calls { get; private set; }
        public ProjectionScopeStartRequest? LastRequest { get; private set; }

        public Task<ChatRoutePolicyMaterializationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            LastRequest = request;
            // ProjectionActivationPlanDispatcher discards the lease (`_ = await EnsureAsync(...)`),
            // so a null lease is sufficient for asserting the dispatch happened.
            return Task.FromResult<ChatRoutePolicyMaterializationRuntimeLease>(null!);
        }
    }

    private sealed class StubActorRuntime(bool exists) : IActorRuntime
    {
        public List<string> ExistsCalls { get; } = [];

        public Task<bool> ExistsAsync(string id)
        {
            ExistsCalls.Add(id);
            return Task.FromResult(exists);
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();
        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IActor?> GetAsync(string id) => throw new NotSupportedException();
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UnlinkAsync(string childId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
