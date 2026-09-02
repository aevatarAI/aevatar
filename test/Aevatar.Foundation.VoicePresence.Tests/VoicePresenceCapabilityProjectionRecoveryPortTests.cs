using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Projection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

/// <summary>
/// Coverage for the voice-side capability projection self-heal port. Verifies it re-materializes ONLY
/// when the capability actor actually exists (so an unprovisioned actor never leaves an empty
/// projection-scope grain behind), re-fires the SAME plan a commit fires, and never creates/activates the
/// source grain. Mirrors <c>ChatRoutePolicyProjectionRecoveryPortTests</c>.
/// </summary>
public sealed class VoicePresenceCapabilityProjectionRecoveryPortTests
{
    [Fact]
    public async Task TryRematerialize_WhenCapabilityActorExists_DispatchesForCorrectActorAndReturnsTrue()
    {
        var runtime = new StubActorRuntime(exists: true);
        var activation = new RecordingActivationService();
        var port = CreatePort(runtime, activation);

        var result = await port.TryRematerializeAsync(" voice-agent-default ");

        result.ShouldBeTrue();
        runtime.ExistsCalls.ShouldHaveSingleItem().ShouldBe("voice-agent-default");
        activation.Calls.ShouldBe(1);
        activation.LastRequest!.RootActorId.ShouldBe("voice-agent-default");
        activation.LastRequest.ProjectionKind.ShouldBe(VoicePresenceProjectionKinds.CapabilityMaterialization);
        activation.LastRequest.Mode.ShouldBe(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public async Task TryRematerialize_WhenCapabilityActorMissing_SkipsDispatchAndReturnsFalse()
    {
        // No capability was ever enabled for this actor. The guard must skip the dispatch so no
        // projection-scope grain is created for an unprovisioned actor.
        var runtime = new StubActorRuntime(exists: false);
        var activation = new RecordingActivationService();
        var port = CreatePort(runtime, activation);

        var result = await port.TryRematerializeAsync("voice-agent-default");

        result.ShouldBeFalse();
        runtime.ExistsCalls.ShouldHaveSingleItem().ShouldBe("voice-agent-default");
        activation.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task TryRematerialize_WhenActorIdBlank_ReturnsFalseWithoutTouchingActorOrDispatch()
    {
        var runtime = new StubActorRuntime(exists: true);
        var activation = new RecordingActivationService();
        var port = CreatePort(runtime, activation);

        var result = await port.TryRematerializeAsync("   ");

        result.ShouldBeFalse();
        runtime.ExistsCalls.ShouldBeEmpty();
        activation.Calls.ShouldBe(0);
    }

    private static IVoicePresenceCapabilityProjectionRecoveryPort CreatePort(
        IActorRuntime actorRuntime,
        RecordingActivationService activation)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionScopeActivationService<VoicePresenceCapabilityMaterializationRuntimeLease>>(activation);
        var dispatcher = new ProjectionActivationPlanDispatcher(services.BuildServiceProvider());
        return new VoicePresenceCapabilityProjectionRecoveryPort(actorRuntime, dispatcher);
    }

    private sealed class RecordingActivationService
        : IProjectionScopeActivationService<VoicePresenceCapabilityMaterializationRuntimeLease>
    {
        public int Calls { get; private set; }
        public ProjectionScopeStartRequest? LastRequest { get; private set; }

        public Task<VoicePresenceCapabilityMaterializationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            LastRequest = request;
            // ProjectionActivationPlanDispatcher discards the lease (`_ = await EnsureAsync(...)`), so a
            // null lease is sufficient for asserting the dispatch happened.
            return Task.FromResult<VoicePresenceCapabilityMaterializationRuntimeLease>(null!);
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
