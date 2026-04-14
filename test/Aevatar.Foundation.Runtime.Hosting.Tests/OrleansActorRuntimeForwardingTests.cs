using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorRuntimeForwardingTests
{
    [Fact]
    public async Task LinkAsync_ShouldRegisterForwardingBinding_AndUpdateTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);

        await runtime.LinkAsync("parent", "child");

        grains["parent"].Children.Should().Contain("child");
        grains["child"].ParentId.Should().Be("parent");
        var bindings = await registry.ListBySourceAsync("parent", CancellationToken.None);
        var binding = bindings.Should().ContainSingle(x => x.TargetStreamId == "child").Subject;
        binding.ForwardingMode.Should().Be(StreamForwardingMode.HandleThenForward);
        binding.DirectionFilter.SetEquals([TopologyAudience.Children, TopologyAudience.ParentAndChildren]).Should().BeTrue();
    }

    [Fact]
    public async Task UnlinkAsync_ShouldRemoveForwardingBinding_AndTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        await runtime.LinkAsync("parent", "child");

        await runtime.UnlinkAsync("child");

        grains["parent"].Children.Should().NotContain("child");
        grains["child"].ParentId.Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task LinkAsync_ShouldCreateCallChainReentrancyScope_ForGrainCalls()
    {
        RequestContext.Clear();
        var runtime = CreateRuntime(out _, out var grains, out _);

        await runtime.LinkAsync("parent", "child");

        grains["parent"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        grains["child"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        RequestContext.ReentrancyId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task UnlinkAsync_ShouldCreateCallChainReentrancyScope_ForGrainCalls()
    {
        RequestContext.Clear();
        var runtime = CreateRuntime(out _, out var grains, out _);
        await runtime.LinkAsync("parent", "child");
        grains["parent"].ObservedReentrancyIds.Clear();
        grains["child"].ObservedReentrancyIds.Clear();

        await runtime.UnlinkAsync("child");

        grains["parent"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        grains["child"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        RequestContext.ReentrancyId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task DestroyAsync_ShouldCleanupIncomingAndOutgoingForwardingBindings()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        await runtime.LinkAsync("parent", "middle");
        await runtime.LinkAsync("middle", "child-1");
        await runtime.LinkAsync("middle", "child-2");

        await runtime.DestroyAsync("middle");

        grains["parent"].Children.Should().NotContain("middle");
        grains["child-1"].ParentId.Should().BeNull();
        grains["child-2"].ParentId.Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync("middle", CancellationToken.None)).Should().BeEmpty();
        grains["middle"].Calls.Should().ContainInOrder("Purge", "Deactivate");
    }

    [Fact]
    public async Task LinkAsync_WhenChildIsNotInitialized_ShouldThrow_AndNotMutateTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        await runtime.ExistsAsync("parent");
        await runtime.ExistsAsync("child");
        grains["child"].Initialized = false;

        var act = () => runtime.LinkAsync("parent", "child");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Child actor child is not initialized.*");
        grains["child"].ParentId.Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task LinkAsync_WhenParentIsNotInitialized_ShouldStillLink_AndSkipParentInitializationProbe()
    {
        var runtime = CreateRuntime(out var registry, out var grains, out _);
        await runtime.ExistsAsync("parent");
        await runtime.ExistsAsync("child");
        grains["parent"].Initialized = false;

        await runtime.LinkAsync("parent", "child");

        grains["parent"].Children.Should().Contain("child");
        grains["child"].ParentId.Should().Be("parent");
        grains["parent"].IsInitializedCallCount.Should().Be(1); // only from ExistsAsync above
        grains["child"].IsInitializedCallCount.Should().Be(2); // ExistsAsync + LinkAsync guard
        (await registry.ListBySourceAsync("parent", CancellationToken.None))
            .Should().ContainSingle(x => x.TargetStreamId == "child");
    }

    [Fact]
    public async Task DestroyAsync_ShouldRemoveStreamFromLifecycleManager()
    {
        var lifecycleManager = new RecordingStreamLifecycleManager();
        var runtime = CreateRuntime(out _, out _, out _, lifecycleManager);

        await runtime.DestroyAsync("actor-1");

        lifecycleManager.RemovedStreamActorIds.Should().ContainSingle("actor-1");
    }

    [Fact]
    public async Task DestroyAsync_ShouldPurgeDurableCallbackSchedulerState()
    {
        var runtime = CreateRuntime(out _, out _, out var callbackSchedulerGrains);

        await runtime.DestroyAsync("actor-1");

        callbackSchedulerGrains["actor-1"].PurgeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DestroyAsync_ShouldCreateCallChainReentrancyScope_ForGrainCalls()
    {
        RequestContext.Clear();
        var runtime = CreateRuntime(out _, out var grains, out _);
        await runtime.LinkAsync("parent", "middle");
        await runtime.LinkAsync("middle", "child");
        grains["parent"].ObservedReentrancyIds.Clear();
        grains["middle"].ObservedReentrancyIds.Clear();
        grains["child"].ObservedReentrancyIds.Clear();

        await runtime.DestroyAsync("middle");

        grains["parent"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        grains["middle"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        grains["child"].ObservedReentrancyIds.Should().Contain(id => id != Guid.Empty);
        RequestContext.ReentrancyId.Should().Be(Guid.Empty);
    }

    private static OrleansActorRuntime CreateRuntime(
        out InMemoryStreamForwardingRegistry registry,
        out Dictionary<string, RecordingRuntimeActorGrain> grains,
        out Dictionary<string, RecordingCallbackSchedulerGrain> callbackSchedulerGrains,
        IStreamLifecycleManager? streamLifecycleManager = null)
    {
        var grainMap = new Dictionary<string, RecordingRuntimeActorGrain>(StringComparer.Ordinal);
        var callbackSchedulerGrainMap = new Dictionary<string, RecordingCallbackSchedulerGrain>(StringComparer.Ordinal);
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grainFactory).ResolveGrain = actorId =>
        {
            if (!grainMap.TryGetValue(actorId, out var grain))
            {
                grain = new RecordingRuntimeActorGrain();
                grainMap[actorId] = grain;
            }

            return grain;
        };
        ((GrainFactoryProxy)(object)grainFactory).ResolveCallbackSchedulerGrain = actorId =>
        {
            if (!callbackSchedulerGrainMap.TryGetValue(actorId, out var grain))
            {
                grain = new RecordingCallbackSchedulerGrain();
                callbackSchedulerGrainMap[actorId] = grain;
            }

            return grain;
        };

        registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(new InMemoryStreamOptions(), NullLoggerFactory.Instance, registry);
        grains = grainMap;
        callbackSchedulerGrains = callbackSchedulerGrainMap;
        return new OrleansActorRuntime(
            grainFactory,
            streams,
            new OrleansActorRuntimeDurableCallbackScheduler(grainFactory),
            streamLifecycleManager: streamLifecycleManager);
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public Func<string, IRuntimeActorGrain>? ResolveGrain { get; set; }

        public Func<string, IRuntimeCallbackSchedulerGrain>? ResolveCallbackSchedulerGrain { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments().Length == 1 &&
                targetMethod.GetGenericArguments()[0] == typeof(IRuntimeActorGrain) &&
                args is { Length: > 0 } &&
                args[0] is string actorId &&
                ResolveGrain != null)
            {
                return ResolveGrain(actorId);
            }

            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments().Length == 1 &&
                targetMethod.GetGenericArguments()[0] == typeof(IRuntimeCallbackSchedulerGrain) &&
                args is { Length: > 0 } &&
                args[0] is string callbackActorId &&
                ResolveCallbackSchedulerGrain != null)
            {
                return ResolveCallbackSchedulerGrain(callbackActorId);
            }

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
    }

    private sealed class RecordingRuntimeActorGrain : IRuntimeActorGrain
    {
        public string? ParentId { get; private set; }

        public HashSet<string> Children { get; } = new(StringComparer.Ordinal);

        public bool Initialized { get; set; } = true;

        public List<string> Calls { get; } = [];
        public List<Guid> ObservedReentrancyIds { get; } = [];

        public int IsInitializedCallCount { get; private set; }

        public Task<bool> InitializeAgentAsync(string agentTypeName)
        {
            _ = agentTypeName;
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            return Task.FromResult(true);
        }

        public Task<bool> IsInitializedAsync()
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            IsInitializedCallCount++;
            return Task.FromResult(Initialized);
        }

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            _ = envelopeBytes;
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            return Task.CompletedTask;
        }

        public Task AddChildAsync(string childId)
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            Children.Add(childId);
            return Task.CompletedTask;
        }

        public Task RemoveChildAsync(string childId)
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            Children.Remove(childId);
            return Task.CompletedTask;
        }

        public Task SetParentAsync(string parentId)
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            ParentId = parentId;
            return Task.CompletedTask;
        }

        public Task ClearParentAsync()
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            ParentId = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetChildrenAsync()
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            return Task.FromResult<IReadOnlyList<string>>(Children.ToList());
        }

        public Task<string?> GetParentAsync()
        {
            ObservedReentrancyIds.Add(RequestContext.ReentrancyId);
            return Task.FromResult(ParentId);
        }

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("recording");

        public Task<string> GetAgentTypeNameAsync() =>
            Task.FromResult(string.Empty);

        public Task DeactivateAsync()
        {
            Calls.Add("Deactivate");
            return Task.CompletedTask;
        }

        public Task PurgeAsync()
        {
            Calls.Add("Purge");
            ParentId = null;
            Children.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStreamLifecycleManager : IStreamLifecycleManager
    {
        public List<string> RemovedStreamActorIds { get; } = [];

        public void RemoveStream(string actorId)
        {
            RemovedStreamActorIds.Add(actorId);
        }
    }

    private sealed class RecordingCallbackSchedulerGrain : IRuntimeCallbackSchedulerGrain
    {
        public int PurgeCalls { get; private set; }

        public Task<long> ScheduleTimeoutAsync(
            string callbackId,
            byte[] envelopeBytes,
            int dueTimeMs,
            RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
        {
            _ = callbackId;
            _ = envelopeBytes;
            _ = dueTimeMs;
            _ = deliveryMode;
            throw new NotSupportedException();
        }

        public Task<long> ScheduleTimerAsync(
            string callbackId,
            byte[] envelopeBytes,
            int dueTimeMs,
            int periodMs,
            RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
        {
            _ = callbackId;
            _ = envelopeBytes;
            _ = dueTimeMs;
            _ = periodMs;
            _ = deliveryMode;
            throw new NotSupportedException();
        }

        public Task CancelAsync(string callbackId, long expectedGeneration = 0)
        {
            _ = callbackId;
            _ = expectedGeneration;
            return Task.CompletedTask;
        }

        public Task PurgeAsync()
        {
            PurgeCalls++;
            return Task.CompletedTask;
        }
    }
}
