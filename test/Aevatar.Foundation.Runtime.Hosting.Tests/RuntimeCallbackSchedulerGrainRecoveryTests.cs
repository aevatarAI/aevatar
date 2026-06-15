using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeCallbackSchedulerGrainRecoveryTests
{
    [Fact]
    public async Task OnActivateAsync_WhenDurableTimeoutIsOverdue_ShouldPublishAndClearOneShotCallback()
    {
        const string actorId = "scheduled-recovery-actor";
        const string callbackId = "scheduled-dispatch-next-fire";
        var streamProvider = new RecordingStreamProvider();
        var storage = new TestRuntimeCallbackSchedulerStateStorage();
        using var host = await StartSiloHostAsync(streamProvider, storage);

        var grain = host.Services
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        storage.SeedSchedulerState(grain.GetGrainId(), new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                [callbackId] = new RuntimeScheduledCallback
                {
                    ActorId = actorId,
                    CallbackId = callbackId,
                    Generation = 7,
                    SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
                    Periodic = false,
                    DueTimeMillis = 1000,
                    PeriodMillis = 0,
                    FireIndex = 0,
                    DeliveryMode = RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
                    TriggerEnvelope = CreateEnvelope("evt-overdue"),
                    NextDueAtUnixTimeMs = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                    OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
                },
            },
        });

        await grain.CancelAsync("unrelated-callback");

        var produced = streamProvider.GetProduced(actorId).Should().ContainSingle().Subject;
        produced.Payload.Unpack<StringValue>().Value.Should().Be("payload");
        produced.Runtime!.Callback.CallbackId.Should().Be(callbackId);
        produced.Runtime.Callback.Generation.Should().Be(7);
        produced.Runtime.Callback.FireIndex.Should().Be(1);
        produced.Runtime.Callback.SlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.OrleansSchedulerV2);

        storage.ReadSchedulerState(grain.GetGrainId())
            .ReminderCallbacks.Should().NotContainKey(callbackId);
    }

    private static async Task<IHost> StartSiloHostAsync(
        RecordingStreamProvider streamProvider,
        TestRuntimeCallbackSchedulerStateStorage storage) =>
        await SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: ports.SiloPort,
                    gatewayPort: ports.GatewayPort,
                    serviceId: $"aevatar-runtime-callback-recovery-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-runtime-callback-recovery-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                {
                    services.RemoveAll<IStreamProvider>();
                    services.RemoveAll<OrleansStreamProviderAdapter>();
                    services.RemoveAll<IStreamLifecycleManager>();
                    services.AddSingleton<IStreamProvider>(streamProvider);
                    services.AddSingleton<IStreamLifecycleManager, NoopStreamLifecycleManager>();
                    services.RemoveAllKeyed<IGrainStorage>(OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName);
                    services.AddSingleton(storage);
                    services.AddGrainStorage<TestRuntimeCallbackSchedulerStateStorage>(
                        OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName,
                        (sp, _) => sp.GetRequiredService<TestRuntimeCallbackSchedulerStateStorage>());
                });
            })
            .Build());

    private static EventEnvelope CreateEnvelope(string id) => new()
    {
        Id = id,
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Any.Pack(new StringValue { Value = "payload" }),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("scheduled-recovery-actor", TopologyAudience.Self),
    };

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, List<EventEnvelope>> _produced = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId) => new RecordingStream(actorId, this);

        public IReadOnlyList<EventEnvelope> GetProduced(string actorId) =>
            _produced.GetValueOrDefault(actorId) ?? [];

        private void AddProduced(string actorId, EventEnvelope envelope)
        {
            var values = _produced.GetValueOrDefault(actorId) ?? [];
            values.Add(envelope.Clone());
            _produced[actorId] = values;
        }

        private sealed class RecordingStream : IStream
        {
            private readonly RecordingStreamProvider _owner;

            public RecordingStream(string actorId, RecordingStreamProvider owner)
            {
                StreamId = actorId;
                _owner = owner;
            }

            public string StreamId { get; }

            public Task ProduceAsync<T>(T message, CancellationToken ct = default)
                where T : IMessage
            {
                ct.ThrowIfCancellationRequested();
                if (message is EventEnvelope envelope)
                    _owner.AddProduced(StreamId, envelope);

                return Task.CompletedTask;
            }

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : IMessage, new()
            {
                _ = handler;
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
            }

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
            {
                _ = binding;
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
            {
                _ = targetStreamId;
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
            }
        }
    }

    private sealed class NoopStreamLifecycleManager : IStreamLifecycleManager
    {
        public void RemoveStream(string actorId)
        {
            _ = actorId;
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRuntimeCallbackSchedulerStateStorage : IGrainStorage
    {
        private const string SchedulerStateName = "runtime-callback-scheduler-v2";
        private readonly Dictionary<(string StateName, GrainId GrainId), object> _states = new();

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            if (_states.TryGetValue((stateName, grainId), out var state))
            {
                grainState.State = CloneState((T)state);
                grainState.RecordExists = true;
                grainState.ETag = string.Empty;
            }

            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            _states[(stateName, grainId)] = CloneState(grainState.State)
                ?? throw new InvalidOperationException("Runtime callback scheduler state cannot be null.");
            grainState.RecordExists = true;
            grainState.ETag = string.Empty;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            _states.Remove((stateName, grainId));
            grainState.RecordExists = false;
            grainState.ETag = string.Empty;
            return Task.CompletedTask;
        }

        public void SeedSchedulerState(GrainId grainId, RuntimeCallbackSchedulerState state)
        {
            _states[(SchedulerStateName, grainId)] = state.Clone();
        }

        public RuntimeCallbackSchedulerState ReadSchedulerState(GrainId grainId)
        {
            var state = _states[(SchedulerStateName, grainId)];
            return ((RuntimeCallbackSchedulerState)state).Clone();
        }

        private static T CloneState<T>(T state)
        {
            if (state is IDeepCloneable<T> cloneable)
                return cloneable.Clone();

            return state;
        }
    }
}
