using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Orleans;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorRuntimeForwardingTests
{
    [Fact]
    public async Task LinkAsync_ShouldRegisterForwardingBinding_AndUpdateTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains);

        await runtime.LinkAsync("parent", "child");

        grains["parent"].Children.Should().Contain("child");
        grains["child"].ParentId.Should().Be("parent");
        var bindings = await registry.ListBySourceAsync("parent", CancellationToken.None);
        var binding = bindings.Should().ContainSingle(x => x.TargetStreamId == "child").Subject;
        binding.ForwardingMode.Should().Be(StreamForwardingMode.HandleThenForward);
        binding.DirectionFilter.SetEquals([EventDirection.Down, EventDirection.Both]).Should().BeTrue();
    }

    [Fact]
    public async Task UnlinkAsync_ShouldRemoveForwardingBinding_AndTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains);
        await runtime.LinkAsync("parent", "child");

        await runtime.UnlinkAsync("child");

        grains["parent"].Children.Should().NotContain("child");
        grains["child"].ParentId.Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task DestroyAsync_ShouldCleanupIncomingAndOutgoingForwardingBindings()
    {
        var runtime = CreateRuntime(out var registry, out var grains);
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
    public async Task LinkAsync_WhenParentIsNotInitialized_ShouldThrow_AndNotMutateTopology()
    {
        var runtime = CreateRuntime(out var registry, out var grains);
        await runtime.ExistsAsync("parent");
        await runtime.ExistsAsync("child");
        grains["parent"].Initialized = false;

        var act = () => runtime.LinkAsync("parent", "child");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Parent actor parent is not initialized.*");
        grains["child"].ParentId.Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
    }

    private static OrleansActorRuntime CreateRuntime(
        out InMemoryStreamForwardingRegistry registry,
        out Dictionary<string, RecordingRuntimeActorGrain> grains)
    {
        var grainMap = new Dictionary<string, RecordingRuntimeActorGrain>(StringComparer.Ordinal);
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

        registry = new InMemoryStreamForwardingRegistry();
        grains = grainMap;
        return new OrleansActorRuntime(
            grainFactory,
            new InMemoryManifestStore(),
            registry,
            new InMemoryStreamProvider());
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public Func<string, IRuntimeActorGrain>? ResolveGrain { get; set; }

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

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
    }

    private sealed class RecordingRuntimeActorGrain : IRuntimeActorGrain
    {
        public string? ParentId { get; private set; }

        public HashSet<string> Children { get; } = new(StringComparer.Ordinal);

        public bool Initialized { get; set; } = true;

        public List<string> Calls { get; } = [];

        public Task<bool> InitializeAgentAsync(string agentTypeName)
        {
            _ = agentTypeName;
            return Task.FromResult(true);
        }

        public Task<bool> IsInitializedAsync() => Task.FromResult(Initialized);

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            _ = envelopeBytes;
            return Task.CompletedTask;
        }

        public Task AddChildAsync(string childId)
        {
            Children.Add(childId);
            return Task.CompletedTask;
        }

        public Task RemoveChildAsync(string childId)
        {
            Children.Remove(childId);
            return Task.CompletedTask;
        }

        public Task SetParentAsync(string parentId)
        {
            ParentId = parentId;
            return Task.CompletedTask;
        }

        public Task ClearParentAsync()
        {
            ParentId = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetChildrenAsync() =>
            Task.FromResult<IReadOnlyList<string>>(Children.ToList());

        public Task<string?> GetParentAsync() =>
            Task.FromResult(ParentId);

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
}
