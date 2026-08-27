using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansStreamForwardingAuthorityTests
{
    [Fact]
    public async Task Registry_ShouldDelegateCloneEvidenceAndValidateInputs()
    {
        var grains = new Dictionary<string, TopologyGrainStub>(StringComparer.Ordinal);
        var grainFactory = CreateGrainFactory((grainType, sourceId) =>
        {
            grainType.Should().Be(typeof(IStreamTopologyGrain));
            if (!grains.TryGetValue(sourceId, out var grain))
            {
                grain = new TopologyGrainStub();
                grains[sourceId] = grain;
            }

            return grain;
        });

        var registry = new OrleansDistributedStreamForwardingRegistry(grainFactory);
        var binding = new StreamForwardingBinding
        {
            SourceStreamId = "source-1",
            TargetStreamId = "target-1",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [TopologyAudience.Parent, TopologyAudience.Children],
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal) { "evt-z", "evt-a" },
            LeaseId = "lease-1",
            Version = 3,
            TargetActorKind = "projection.test-scope",
            ActivationGeneration = 11,
        };

        await registry.UpsertAsync(binding);
        var persistedEntry = grains["source-1"].LastUpsert;
        persistedEntry.Should().NotBeNull();
        persistedEntry!.DirectionFilter.Should().BeEquivalentTo([TopologyAudience.Children, TopologyAudience.Parent]);
        persistedEntry.EventTypeFilter.Should().Equal("evt-a", "evt-z");
        persistedEntry.TargetActorKind.Should().Be("projection.test-scope");
        persistedEntry.ActivationGeneration.Should().Be(11);
        (await registry.ListBySourceAsync("source-1")).Should().ContainSingle(x => x.TargetStreamId == "target-1");
        var exact = await registry.GetAsync("source-1", "target-1");
        exact!.TargetActorKind.Should().Be("projection.test-scope");
        exact.ActivationGeneration.Should().Be(11);

        await registry.RemoveAsync("source-1", "target-1");
        (await registry.ListBySourceAsync("source-1")).Should().BeEmpty();

        await Assert.ThrowsAsync<ArgumentNullException>(() => registry.UpsertAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "",
            TargetStreamId = "target",
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.RemoveAsync("", "target"));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.RemoveAsync("source", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.ListBySourceAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.GetAsync("", "target"));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.GetAsync("source", ""));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => registry.UpsertAsync(binding, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => registry.RemoveAsync("source-1", "target-1", cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => registry.ListBySourceAsync("source-1", cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => registry.GetAsync("source-1", "target-1", cts.Token));
    }

    [Fact]
    public void GrainContract_ShouldPreserveRollingCompatibleRpcSurfaceAndSerializableEvidence()
    {
        var upsertMethod = typeof(IStreamTopologyGrain).GetMethod(
            nameof(IStreamTopologyGrain.UpsertAsync),
            [typeof(StreamForwardingBindingEntry)]);
        upsertMethod.Should().NotBeNull();

        var listMethod = typeof(IStreamTopologyGrain).GetMethod(nameof(IStreamTopologyGrain.ListAsync));
        listMethod.Should().NotBeNull();
        listMethod!.ReturnType.Should().Be(typeof(Task<IReadOnlyList<StreamForwardingBindingEntry>>));
        typeof(IStreamTopologyGrain).GetMethod("GetAsync", [typeof(string)])
            .Should().BeNull("adding an Orleans RPC breaks mixed-version rolling upgrades");

        var entryType = typeof(StreamForwardingBindingEntry);
        entryType.GetCustomAttributes(inherit: false)
            .Select(attribute => attribute.GetType().Name)
            .Should().Contain("GenerateSerializerAttribute");
        entryType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Count(property => property.GetCustomAttributes(inherit: false)
                .Any(attribute => attribute.GetType().Name == "IdAttribute"))
            .Should().Be(9);
    }

    [Fact]
    public async Task AuthoritativeGet_ShouldBypassCrossInstanceListCache()
    {
        var grain = new TopologyGrainStub();
        var grainFactory = CreateGrainFactory((grainType, _) =>
        {
            grainType.Should().Be(typeof(IStreamTopologyGrain));
            return grain;
        });
        var writer = new OrleansDistributedStreamForwardingRegistry(grainFactory, TimeSpan.FromHours(1));
        var reader = new OrleansDistributedStreamForwardingRegistry(grainFactory, TimeSpan.FromHours(1));
        var binding = new StreamForwardingBinding
        {
            SourceStreamId = "source-authority",
            TargetStreamId = "target-authority",
            TargetActorKind = "projection.test-scope",
            ActivationGeneration = 5,
        };
        await writer.UpsertAsync(binding);
        (await reader.ListBySourceAsync(binding.SourceStreamId)).Should().ContainSingle();

        await writer.RemoveAsync(binding.SourceStreamId, binding.TargetStreamId);

        (await reader.ListBySourceAsync(binding.SourceStreamId)).Should().ContainSingle();
        (await reader.GetAsync(binding.SourceStreamId, binding.TargetStreamId)).Should().BeNull();
    }

    [Fact]
    public async Task AuthoritativeGet_ShouldHonorCancellationWhileGrainCallIsPending()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<StreamForwardingBindingEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var grain = new TopologyGrainStub
        {
            ListHandler = () => pending.Task,
        };
        var registry = new OrleansDistributedStreamForwardingRegistry(
            CreateGrainFactory((_, _) => grain));
        using var cts = new CancellationTokenSource();

        var lookup = registry.GetAsync("source-pending", "target-pending", cts.Token);
        lookup.IsCompleted.Should().BeFalse();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }

    [Fact]
    public async Task AuthoritativeGet_WhenTargetSiloIsTemporarilyUnavailable_ShouldRetry()
    {
        var grain = new TopologyGrainStub();
        grain.ListExceptions.Enqueue(CreateSiloUnavailableException());
        var registry = new OrleansDistributedStreamForwardingRegistry(
            CreateGrainFactory((_, _) => grain),
            TimeSpan.Zero,
            NullLogger<OrleansDistributedStreamForwardingRegistry>.Instance,
            topologyAttemptLimit: 3,
            topologyRetryDelay: TimeSpan.Zero);

        var binding = await registry.GetAsync("source-retry", "target-retry");

        binding.Should().BeNull();
        grain.ListCallCount.Should().Be(2);
    }

    [Fact]
    public async Task AuthoritativeGet_WhenOrleansRejectsStaleSiloRoute_ShouldRetry()
    {
        var grain = new TopologyGrainStub();
        grain.ListExceptions.Enqueue(CreateMessageRejectionException());
        var registry = new OrleansDistributedStreamForwardingRegistry(
            CreateGrainFactory((_, _) => grain),
            TimeSpan.Zero,
            NullLogger<OrleansDistributedStreamForwardingRegistry>.Instance,
            topologyAttemptLimit: 3,
            topologyRetryDelay: TimeSpan.Zero);

        await registry.GetAsync("source-retry", "target-retry");

        grain.ListCallCount.Should().Be(2);
    }

    [Fact]
    public async Task AuthoritativeGet_WhenOrleansDirectoryIsConverging_ShouldRetry()
    {
        var grain = new TopologyGrainStub();
        grain.ListExceptions.Enqueue(CreateDirectoryConvergenceException());
        var registry = new OrleansDistributedStreamForwardingRegistry(
            CreateGrainFactory((_, _) => grain),
            TimeSpan.Zero,
            NullLogger<OrleansDistributedStreamForwardingRegistry>.Instance,
            topologyAttemptLimit: 3,
            topologyRetryDelay: TimeSpan.Zero);

        await registry.GetAsync("source-retry", "target-retry");

        grain.ListCallCount.Should().Be(2);
    }

    [Fact]
    public async Task AuthoritativeGet_WhenTopologyDoesNotConverge_ShouldStopAfterAttemptLimit()
    {
        var grain = new TopologyGrainStub();
        for (var i = 0; i < 3; i++)
            grain.ListExceptions.Enqueue(CreateSiloUnavailableException());
        var registry = new OrleansDistributedStreamForwardingRegistry(
            CreateGrainFactory((_, _) => grain),
            TimeSpan.Zero,
            NullLogger<OrleansDistributedStreamForwardingRegistry>.Instance,
            topologyAttemptLimit: 3,
            topologyRetryDelay: TimeSpan.Zero);

        var act = () => registry.GetAsync("source-retry", "target-retry");

        await act.Should().ThrowAsync<SiloUnavailableException>();
        grain.ListCallCount.Should().Be(3);
    }

    [Fact]
    public async Task AuthoritativeGet_WhenFailureIsNotTopologyConvergence_ShouldNotRetry()
    {
        var grain = new TopologyGrainStub();
        grain.ListExceptions.Enqueue(new InvalidOperationException("topology read failure"));
        var registry = new OrleansDistributedStreamForwardingRegistry(
            CreateGrainFactory((_, _) => grain),
            TimeSpan.Zero,
            NullLogger<OrleansDistributedStreamForwardingRegistry>.Instance,
            topologyAttemptLimit: 3,
            topologyRetryDelay: TimeSpan.Zero);

        var act = () => registry.GetAsync("source-retry", "target-retry");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("topology read failure");
        grain.ListCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ListBySource_ShouldCacheByRevision()
    {
        var grains = new Dictionary<string, TopologyGrainStub>(StringComparer.Ordinal);
        var grainFactory = CreateGrainFactory((grainType, sourceId) =>
        {
            grainType.Should().Be(typeof(IStreamTopologyGrain));
            if (!grains.TryGetValue(sourceId, out var grain))
            {
                grain = new TopologyGrainStub();
                grains[sourceId] = grain;
            }

            return grain;
        });

        var registry = new OrleansDistributedStreamForwardingRegistry(grainFactory, TimeSpan.Zero);
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "source-cache",
            TargetStreamId = "target-1",
            Version = 1,
            LeaseId = "lease-1",
        });

        var grain = grains["source-cache"];
        (await registry.ListBySourceAsync("source-cache")).Should().ContainSingle();
        grain.ListCallCount.Should().Be(1);
        (await registry.ListBySourceAsync("source-cache")).Should().ContainSingle();
        grain.ListCallCount.Should().Be(1);
        grain.RevisionCallCount.Should().BeGreaterThanOrEqualTo(2);

        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "source-cache",
            TargetStreamId = "target-1",
            Version = 2,
            LeaseId = "lease-2",
        });

        var updated = await registry.ListBySourceAsync("source-cache");
        updated.Should().ContainSingle(binding => binding.Version == 2 && binding.LeaseId == "lease-2");
        grain.ListCallCount.Should().Be(2);
    }

    private static IGrainFactory CreateGrainFactory(Func<System.Type, string, object> resolver)
    {
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grainFactory).Resolver = resolver;
        return grainFactory;
    }

    private static OrleansMessageRejectionException CreateMessageRejectionException() =>
        (OrleansMessageRejectionException)Activator.CreateInstance(
            typeof(OrleansMessageRejectionException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["stale silo route"],
            culture: null)!;

    private static SiloUnavailableException CreateSiloUnavailableException() =>
        (SiloUnavailableException)Activator.CreateInstance(
            typeof(SiloUnavailableException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["stale silo route"],
            culture: null)!;

    private static OrleansException CreateDirectoryConvergenceException() =>
        (OrleansException)Activator.CreateInstance(
            typeof(OrleansException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["Current directory is not stable to perform the lookup. Retry later."],
            culture: null)!;

    private class GrainFactoryProxy : DispatchProxy
    {
        public Func<System.Type, string, object>? Resolver { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                args is { Length: > 0 } &&
                args[0] is string id &&
                Resolver != null)
            {
                return Resolver(targetMethod.GetGenericArguments()[0], id);
            }

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
    }

    private sealed class TopologyGrainStub : IStreamTopologyGrain
    {
        private readonly List<StreamForwardingBindingEntry> _bindings = [];
        private long _revision;

        public int ListCallCount { get; private set; }
        public int RevisionCallCount { get; private set; }
        public StreamForwardingBindingEntry? LastUpsert { get; private set; }
        public Func<Task<IReadOnlyList<StreamForwardingBindingEntry>>>? ListHandler { get; init; }
        public Queue<Exception> ListExceptions { get; } = new();

        public Task UpsertAsync(StreamForwardingBindingEntry binding)
        {
            var index = _bindings.FindIndex(item =>
                string.Equals(item.TargetStreamId, binding.TargetStreamId, StringComparison.Ordinal));
            var clone = Clone(binding);
            LastUpsert = clone;
            if (index >= 0)
                _bindings[index] = clone;
            else
                _bindings.Add(clone);
            _revision++;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string targetStreamId)
        {
            if (_bindings.RemoveAll(item =>
                    string.Equals(item.TargetStreamId, targetStreamId, StringComparison.Ordinal)) > 0)
            {
                _revision++;
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBindingEntry>> ListAsync()
        {
            ListCallCount++;
            if (ListExceptions.TryDequeue(out var exception))
                return Task.FromException<IReadOnlyList<StreamForwardingBindingEntry>>(exception);
            if (ListHandler is not null)
                return ListHandler();
            return Task.FromResult<IReadOnlyList<StreamForwardingBindingEntry>>(_bindings.Select(Clone).ToList());
        }

        public Task<long> GetRevisionAsync()
        {
            RevisionCallCount++;
            return Task.FromResult(_revision);
        }

        public Task ClearAsync()
        {
            if (_bindings.Count > 0)
                _revision++;
            _bindings.Clear();
            return Task.CompletedTask;
        }

        private static StreamForwardingBindingEntry Clone(StreamForwardingBindingEntry binding) =>
            new()
            {
                SourceStreamId = binding.SourceStreamId,
                TargetStreamId = binding.TargetStreamId,
                ForwardingMode = binding.ForwardingMode,
                DirectionFilter = [.. binding.DirectionFilter],
                EventTypeFilter = [.. binding.EventTypeFilter],
                Version = binding.Version,
                LeaseId = binding.LeaseId,
                TargetActorKind = binding.TargetActorKind,
                ActivationGeneration = binding.ActivationGeneration,
            };
    }
}
