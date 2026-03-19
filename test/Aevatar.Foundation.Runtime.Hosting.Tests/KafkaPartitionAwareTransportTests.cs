using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware.DependencyInjection;
using Confluent.Kafka;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class KafkaPartitionAwareTransportTests
{
    [Fact]
    public void StrictQueuePartitionMapper_ShouldProvideStablePartitionQueueMapping()
    {
        var mapper = new StrictQueuePartitionMapper("strict-provider", 4);
        var partitionId1 = mapper.GetPartitionId("aevatar.events", "actor-1");
        var partitionId2 = mapper.GetPartitionId("aevatar.events", "actor-1");
        var queueId = mapper.GetQueueId(partitionId1);

        partitionId1.Should().Be(partitionId2);
        mapper.GetPartitionId(queueId).Should().Be(partitionId1);
        mapper.GetQueueForStream(StreamId.Create("aevatar.events", "actor-1")).Should().Be(queueId);
        mapper.GetAllQueues().Should().HaveCount(4);
    }

    [Fact]
    public async Task PartitionOwnedReceiverRegistry_ShouldCreateStartAndCloseReceiverLifecycle()
    {
        var factory = new RecordingPartitionOwnedReceiverFactory();
        var registry = new PartitionOwnedReceiverRegistry(factory);

        await registry.EnsureStartedAsync(2);
        await registry.EnsureStartedAsync(2);
        await registry.BeginClosingAsync(2);
        await registry.DrainAndCloseAsync(2, TimeSpan.FromSeconds(1));

        factory.Created.Should().ContainSingle(x => x.PartitionId == 2);
        var receiver = factory.Created.Single();
        receiver.StartCallCount.Should().Be(1);
        receiver.BeginClosingCallCount.Should().Be(1);
        receiver.DrainCallCount.Should().Be(1);
        receiver.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task KafkaPartitionAssignmentManager_ShouldTrackOwnedPartitionsAndDriveRegistry()
    {
        var registry = new RecordingPartitionOwnedReceiverRegistry();
        var manager = new KafkaPartitionAssignmentManager(registry);

        await manager.OnAssignedAsync([1, 3]);
        manager.GetOwnedPartitions().Should().BeEquivalentTo([1, 3]);

        await manager.OnRevokedAsync([3]);
        manager.GetOwnedPartitions().Should().BeEquivalentTo([1]);

        registry.Started.Should().BeEquivalentTo([1, 3]);
        registry.BeginClosing.Should().BeEquivalentTo([3]);
        registry.Drained.Should().ContainSingle(x => x.partitionId == 3);
    }

    [Fact]
    public async Task KafkaPartitionAwareQueueAdapterReceiver_ShouldReceiveOnlySubscribedPartitionRecords()
    {
        var mapper = new StrictQueuePartitionMapper("strict-provider", 2);
        var transport = new TestPartitionAwareEnvelopeTransport(mapper);
        var (provider, adapter) = await CreateAdapterAsync(transport, "strict-provider", "aevatar.events", 2);
        await using var serviceProvider = provider;
        var streamId = StreamId.Create("aevatar.events", "actor-1");
        var targetQueue = mapper.GetQueueForStream(streamId);
        var otherPartition = (mapper.GetPartitionId(targetQueue) + 1) % 2;
        var receiver = adapter.CreateReceiver(targetQueue);

        await receiver.Initialize(TimeSpan.FromSeconds(1));
        await serviceProvider.GetRequiredService<IPartitionAssignmentManager>()
            .OnAssignedAsync([mapper.GetPartitionId(targetQueue)]);
        await transport.PushAsync(new PartitionEnvelopeRecord
        {
            PartitionId = otherPartition,
            StreamNamespace = "aevatar.events",
            StreamId = "actor-1",
            Payload = CreateEnvelopeBytes("ignored"),
        });
        var pushTask = transport.PushAsync(new PartitionEnvelopeRecord
        {
            PartitionId = mapper.GetPartitionId(targetQueue),
            StreamNamespace = "aevatar.events",
            StreamId = "actor-1",
            Payload = CreateEnvelopeBytes("accepted"),
        });

        var messages = await receiver.GetQueueMessagesAsync(10);
        await receiver.MessagesDeliveredAsync(messages);
        await pushTask;

        messages.Should().ContainSingle();
        messages[0].GetEvents<EventEnvelope>().Single().Item1.Payload!.Unpack<StringValue>().Value.Should().Be("accepted");
        await receiver.Shutdown(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task KafkaPartitionAwareBackend_ShouldCreateReceiverAndConsumeThroughStrictTransport()
    {
        var services = new ServiceCollection();
        var runtimeOptions = new AevatarActorRuntimeOptions
        {
            Provider = AevatarActorRuntimeOptions.ProviderOrleans,
            OrleansStreamBackend = AevatarActorRuntimeOptions.OrleansStreamBackendKafkaPartitionAware,
            OrleansStreamProviderName = "strict-provider",
            OrleansActorEventNamespace = "aevatar.events",
        };
        var mapper = new StrictQueuePartitionMapper(runtimeOptions.OrleansStreamProviderName, 8);
        var transport = new TestPartitionAwareEnvelopeTransport(mapper);

        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaPartitionAware,
            StreamProviderName = runtimeOptions.OrleansStreamProviderName,
            ActorEventNamespace = runtimeOptions.OrleansActorEventNamespace,
            QueueCount = 8,
            QueueCacheSize = 256,
        });
        services.AddSingleton<IKafkaPartitionAwareEnvelopeTransport>(transport);
        services.AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<Orleans.Streams.IQueueAdapterFactory>();
        var adapter = await factory.CreateAdapter();
        var streamId = StreamId.Create(runtimeOptions.OrleansActorEventNamespace, "actor-42");
        var queueId = factory.GetStreamQueueMapper().GetQueueForStream(streamId);
        var receiver = adapter.CreateReceiver(queueId);

        await receiver.Initialize(TimeSpan.FromSeconds(1));
        await provider.GetRequiredService<IPartitionAssignmentManager>()
            .OnAssignedAsync([mapper.GetPartitionId(queueId)]);
        var publishTask = transport.PublishAsync(
            runtimeOptions.OrleansActorEventNamespace,
            "actor-42",
            CreateEnvelopeBytes("strict"));

        var messages = await receiver.GetQueueMessagesAsync(10);
        await receiver.MessagesDeliveredAsync(messages);
        await publishTask;

        messages.Should().ContainSingle();
        messages[0].GetEvents<EventEnvelope>().Single().Item1.Payload!.Unpack<StringValue>().Value.Should().Be("strict");
        await receiver.Shutdown(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task KafkaPartitionAwareBackend_ShouldHoldEarlyRecordUntilQueueReceiverInitializes()
    {
        var mapper = new StrictQueuePartitionMapper("strict-provider", 1);
        var transport = new TestPartitionAwareEnvelopeTransport(mapper);
        var (provider, adapter) = await CreateAdapterAsync(transport, "strict-provider", "aevatar.events", 1);
        await using var serviceProvider = provider;

        await serviceProvider.GetRequiredService<IPartitionAssignmentManager>().OnAssignedAsync([0]);

        var pushTask = transport.PushAsync(new PartitionEnvelopeRecord
        {
            PartitionId = 0,
            StreamNamespace = "aevatar.events",
            StreamId = "actor-early-handoff",
            Payload = CreateEnvelopeBytes("held-until-initialize"),
        });
        pushTask.IsCompleted.Should().BeFalse("the local router should wait until the Orleans queue receiver subscribes");

        var queueId = mapper.GetAllQueues().Single();
        var receiver = adapter.CreateReceiver(queueId);
        await receiver.Initialize(TimeSpan.FromSeconds(1));

        var pendingMessagesField = receiver.GetType().GetField(
            "_messages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        pendingMessagesField.Should().NotBeNull();
        var pendingMessages = pendingMessagesField!.GetValue(receiver);
        pendingMessages.Should().NotBeNull();
        var pendingCountProperty = pendingMessages!.GetType().GetProperty("Count");
        pendingCountProperty.Should().NotBeNull();
        SpinWait.SpinUntil(
            () => (int)pendingCountProperty!.GetValue(pendingMessages)! > 0,
            TimeSpan.FromSeconds(2)).Should().BeTrue("the waiting local handoff should resume once the queue receiver initializes");

        var messages = await receiver.GetQueueMessagesAsync(10);
        messages.Should().ContainSingle();
        messages[0].GetEvents<EventEnvelope>().Single().Item1.Payload!.Unpack<StringValue>().Value.Should().Be("held-until-initialize");

        await receiver.MessagesDeliveredAsync(messages);
        await pushTask;
        await receiver.Shutdown(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task KafkaPartitionOwnedReceiver_WhenClosing_ShouldAbortConsumedRecordWithoutFakeSuccess()
    {
        var mapper = new StrictQueuePartitionMapper("strict-provider", 2);
        var transport = new TestPartitionAwareEnvelopeTransport(mapper);
        var localAckPort = new RecordingLocalDeliveryAckPort();
        var services = new ServiceCollection();
        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaPartitionAware,
            StreamProviderName = "strict-provider",
            ActorEventNamespace = "aevatar.events",
            QueueCount = 2,
            QueueCacheSize = 256,
        });
        services.AddSingleton<IKafkaPartitionAwareEnvelopeTransport>(transport);
        services.AddSingleton<ILocalDeliveryAckPort>(localAckPort);
        services.AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IPartitionOwnedReceiverFactory>();
        await using var receiver = await factory.CreateAsync(1);
        await receiver.StartAsync();
        await receiver.BeginClosingAsync();

        var act = () => transport.PushAsync(new PartitionEnvelopeRecord
        {
            PartitionId = 1,
            StreamNamespace = "aevatar.events",
            StreamId = "actor-closed",
            Payload = CreateEnvelopeBytes("ignored"),
        });

        var exception = await Assert.ThrowsAnyAsync<Exception>(act);
        exception.GetType().Name.Should().Be("PartitionRecordHandoffAbortedException");
        localAckPort.DeliveryCount.Should().Be(0);
    }

    [Fact]
    public async Task KafkaPartitionAwareQueueAdapterReceiver_ShutdownPendingDelivery_ShouldAbortWithoutCancellation()
    {
        var mapper = new StrictQueuePartitionMapper("strict-provider", 1);
        var transport = new TestPartitionAwareEnvelopeTransport(mapper);
        var (provider, adapter) = await CreateAdapterAsync(transport, "strict-provider", "aevatar.events", 1);
        await using var serviceProvider = provider;
        var queueId = mapper.GetAllQueues().Single();
        var receiver = adapter.CreateReceiver(queueId);

        await receiver.Initialize(TimeSpan.FromSeconds(1));
        await serviceProvider.GetRequiredService<IPartitionAssignmentManager>().OnAssignedAsync([0]);
        var pushTask = transport.PushAsync(new PartitionEnvelopeRecord
        {
            PartitionId = 0,
            StreamNamespace = "aevatar.events",
            StreamId = "actor-pending",
            Payload = CreateEnvelopeBytes("pending"),
        });
        var messages = await receiver.GetQueueMessagesAsync(10);

        messages.Should().ContainSingle();
        await receiver.Shutdown(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => pushTask);
        exception.GetType().Name.Should().Be("PartitionRecordHandoffAbortedException");
    }

    [Fact]
    public async Task KafkaPartitionOwnedReceiver_WhenClosingDuringInFlightHandoff_ShouldAbortPendingDelivery()
    {
        var mapper = new StrictQueuePartitionMapper("strict-provider", 1);
        var transport = new TestPartitionAwareEnvelopeTransport(mapper);
        var localAckPort = new AwaitingLocalDeliveryAckPort();
        var services = new ServiceCollection();
        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaPartitionAware,
            StreamProviderName = "strict-provider",
            ActorEventNamespace = "aevatar.events",
            QueueCount = 1,
            QueueCacheSize = 256,
        });
        services.AddSingleton<IKafkaPartitionAwareEnvelopeTransport>(transport);
        services.AddSingleton<ILocalDeliveryAckPort>(localAckPort);
        services.AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IPartitionOwnedReceiverFactory>();
        await using var receiver = await factory.CreateAsync(0);
        await receiver.StartAsync();

        var pushTask = transport.PushAsync(new PartitionEnvelopeRecord
        {
            PartitionId = 0,
            StreamNamespace = "aevatar.events",
            StreamId = "actor-inflight",
            Payload = CreateEnvelopeBytes("pending"),
        });

        await localAckPort.WaitUntilStartedAsync();
        await receiver.BeginClosingAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => pushTask);
        exception.GetType().Name.Should().Be("PartitionRecordHandoffAbortedException");
    }

    [Fact]
    public async Task LocalPartitionRecordRouter_WhenTokenAlreadyCanceled_ShouldAbortInsteadOfReturningSuccess()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaPartitionAware,
            StreamProviderName = "strict-provider",
            ActorEventNamespace = "aevatar.events",
            QueueCount = 1,
            QueueCacheSize = 256,
        });
        services.AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport();
        await using var provider = services.BuildServiceProvider();
        var router = provider.GetRequiredService<ILocalDeliveryAckPort>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => router.DeliverAsync(0, new PartitionEnvelopeRecord
        {
            PartitionId = 0,
            StreamNamespace = "aevatar.events",
            StreamId = "actor-canceled",
            Payload = CreateEnvelopeBytes("ignored"),
        }, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    [Fact]
    public async Task KafkaPartitionAwareQueueAdapterReceiver_WhenShutdownStarted_ShouldAbortLateCallbackWithoutQueueingBatch()
    {
        var mapper = new StrictQueuePartitionMapper("strict-provider", 1);
        var transport = new TestPartitionAwareEnvelopeTransport(mapper);
        var (provider, adapter) = await CreateAdapterAsync(transport, "strict-provider", "aevatar.events", 1);
        await using var serviceProvider = provider;
        var queueId = mapper.GetAllQueues().Single();
        var receiver = adapter.CreateReceiver(queueId);

        await receiver.Initialize(TimeSpan.FromSeconds(1));
        await receiver.Shutdown(TimeSpan.FromSeconds(1));

        var handleMethod = receiver.GetType().GetMethod(
            "HandleRecordAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        handleMethod.Should().NotBeNull();

        var task = (Task)handleMethod!.Invoke(receiver, [new PartitionEnvelopeRecord
        {
            PartitionId = 0,
            StreamNamespace = "aevatar.events",
            StreamId = "actor-late-callback",
            Payload = CreateEnvelopeBytes("ignored"),
        }])!;

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => task);
        exception.GetType().Name.Should().Be("PartitionRecordHandoffAbortedException");
        (await receiver.GetQueueMessagesAsync(10)).Should().BeEmpty();
    }

    [Fact]
    public async Task KafkaPartitionAwareEnvelopeTransport_WhenLifecycleHandlerFails_ShouldRetryInsteadOfSilentlyDropping()
    {
        var transportType = typeof(IKafkaPartitionAwareEnvelopeTransport).Assembly.GetType(
            "Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware.KafkaPartitionAwareEnvelopeTransport");
        transportType.Should().NotBeNull();
        var transport = Activator.CreateInstance(
            transportType!,
            new KafkaPartitionAwareTransportOptions
            {
                BootstrapServers = "localhost:9092",
                TopicName = "strict-lifecycle-test",
                ConsumerGroup = "strict-lifecycle-group",
                TopicPartitionCount = 1,
            },
            new AevatarOrleansRuntimeOptions
            {
                StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaPartitionAware,
                StreamProviderName = "strict-provider",
                ActorEventNamespace = "aevatar.events",
                QueueCount = 1,
                QueueCacheSize = 256,
            },
            null);
        transport.Should().NotBeNull();

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var subscribeMethod = transportType!.GetMethod("SubscribePartitionLifecycleAsync");
        subscribeMethod.Should().NotBeNull();
        var subscriptionTask = (Task<IAsyncDisposable>)subscribeMethod!.Invoke(transport, [new Func<PartitionLifecycleEvent, Task>(lifecycleEvent =>
        {
            lifecycleEvent.PartitionId.Should().Be(0);
            lifecycleEvent.Kind.Should().Be(PartitionLifecycleEventKind.Assigned);

            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("transient lifecycle failure");

            completion.TrySetResult(true);
            return Task.CompletedTask;
        }), CancellationToken.None])!;
        await using var subscription = await subscriptionTask;

        var fireLifecycleEvent = transportType.GetMethod(
            "FireLifecycleEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        fireLifecycleEvent.Should().NotBeNull();
        fireLifecycleEvent!.Invoke(
            transport,
            [new[] { new TopicPartition("strict-lifecycle-test", new Partition(0)) }, PartitionLifecycleEventKind.Assigned]);

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        attempts.Should().Be(2);
    }

    private static async Task<(ServiceProvider Provider, IQueueAdapter Adapter)> CreateAdapterAsync(
        IKafkaPartitionAwareEnvelopeTransport transport,
        string providerName,
        string actorEventNamespace,
        int queueCount)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaPartitionAware,
            StreamProviderName = providerName,
            ActorEventNamespace = actorEventNamespace,
            QueueCount = queueCount,
            QueueCacheSize = 256,
        });
        services.AddSingleton<IKafkaPartitionAwareEnvelopeTransport>(transport);
        services.AddAevatarFoundationRuntimeOrleansKafkaPartitionAwareTransport();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IQueueAdapterFactory>();
        var adapter = await factory.CreateAdapter();
        return (provider, adapter);
    }

    private static byte[] CreateEnvelopeBytes(string value)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new StringValue { Value = value }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };
        return envelope.ToByteArray();
    }

    private sealed class RecordingPartitionOwnedReceiverFactory : IPartitionOwnedReceiverFactory
    {
        public List<RecordingPartitionOwnedReceiver> Created { get; } = [];

        public Task<IPartitionOwnedReceiver> CreateAsync(int partitionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var receiver = new RecordingPartitionOwnedReceiver(partitionId);
            Created.Add(receiver);
            return Task.FromResult<IPartitionOwnedReceiver>(receiver);
        }
    }

    private sealed class RecordingLocalDeliveryAckPort : ILocalDeliveryAckPort
    {
        public int DeliveryCount { get; private set; }

        public Task DeliverAsync(int partitionId, PartitionEnvelopeRecord record, CancellationToken ct = default)
        {
            _ = partitionId;
            _ = record;
            ct.ThrowIfCancellationRequested();
            DeliveryCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class AwaitingLocalDeliveryAckPort : ILocalDeliveryAckPort
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DeliverAsync(int partitionId, PartitionEnvelopeRecord record, CancellationToken ct = default)
        {
            _ = partitionId;
            _ = record;
            _started.TrySetResult(true);
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(static state =>
            {
                ((TaskCompletionSource<bool>)state!).TrySetCanceled();
            }, pending);
            await pending.Task;
        }

        public Task WaitUntilStartedAsync()
        {
            return _started.Task;
        }
    }

    private sealed class RecordingPartitionOwnedReceiver : IPartitionOwnedReceiver
    {
        public RecordingPartitionOwnedReceiver(int partitionId)
        {
            PartitionId = partitionId;
        }

        public int PartitionId { get; }

        public int StartCallCount { get; private set; }

        public int BeginClosingCallCount { get; private set; }

        public int DrainCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public Task StartAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StartCallCount++;
            return Task.CompletedTask;
        }

        public Task BeginClosingAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BeginClosingCallCount++;
            return Task.CompletedTask;
        }

        public Task DrainAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            _ = timeout;
            ct.ThrowIfCancellationRequested();
            DrainCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPartitionOwnedReceiverRegistry : IPartitionOwnedReceiverRegistry
    {
        public List<int> Started { get; } = [];

        public List<int> BeginClosing { get; } = [];

        public List<(int partitionId, TimeSpan timeout)> Drained { get; } = [];

        public Task EnsureStartedAsync(int partitionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Started.Add(partitionId);
            return Task.CompletedTask;
        }

        public Task BeginClosingAsync(int partitionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BeginClosing.Add(partitionId);
            return Task.CompletedTask;
        }

        public Task DrainAndCloseAsync(int partitionId, TimeSpan timeout, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Drained.Add((partitionId, timeout));
            return Task.CompletedTask;
        }
    }

    private sealed class TestPartitionAwareEnvelopeTransport : IKafkaPartitionAwareEnvelopeTransport
    {
        private readonly IStrictOrleansStreamQueueMapper _mapper;
        private readonly Lock _lock = new();
        private readonly Dictionary<int, List<Func<PartitionEnvelopeRecord, Task>>> _recordHandlers = [];
        private readonly List<Func<PartitionLifecycleEvent, Task>> _lifecycleHandlers = [];

        public TestPartitionAwareEnvelopeTransport(IStrictOrleansStreamQueueMapper mapper)
        {
            _mapper = mapper;
        }

        public Task PublishAsync(
            string streamNamespace,
            string streamId,
            byte[] payload,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var partitionId = _mapper.GetPartitionId(streamNamespace, streamId);
            return PushAsync(new PartitionEnvelopeRecord
            {
                PartitionId = partitionId,
                StreamNamespace = streamNamespace,
                StreamId = streamId,
                Payload = payload,
            });
        }

        public Task StartAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribePartitionLifecycleAsync(
            Func<PartitionLifecycleEvent, Task> handler,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ct.ThrowIfCancellationRequested();

            lock (_lock)
            {
                _lifecycleHandlers.Add(handler);
            }

            return Task.FromResult<IAsyncDisposable>(new CallbackSubscription(
                () =>
                {
                    lock (_lock)
                    {
                        _lifecycleHandlers.Remove(handler);
                    }
                }));
        }

        public Task<IAsyncDisposable> SubscribePartitionRecordsAsync(
            int partitionId,
            Func<PartitionEnvelopeRecord, Task> handler,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ct.ThrowIfCancellationRequested();

            lock (_lock)
            {
                if (!_recordHandlers.TryGetValue(partitionId, out var handlers))
                {
                    handlers = [];
                    _recordHandlers[partitionId] = handlers;
                }

                handlers.Add(handler);
            }

            return Task.FromResult<IAsyncDisposable>(new CallbackSubscription(
                () =>
                {
                    lock (_lock)
                    {
                        if (_recordHandlers.TryGetValue(partitionId, out var handlers))
                            handlers.Remove(handler);
                    }
                }));
        }

        public async Task PushAsync(PartitionEnvelopeRecord record)
        {
            List<Func<PartitionEnvelopeRecord, Task>> handlers;
            lock (_lock)
            {
                handlers = _recordHandlers.TryGetValue(record.PartitionId, out var current)
                    ? [.. current]
                    : [];
            }

            foreach (var handler in handlers)
                await handler(record);
        }

        private sealed class CallbackSubscription : IAsyncDisposable
        {
            private readonly Action _dispose;
            private int _disposed;

            public CallbackSubscription(Action dispose)
            {
                _dispose = dispose;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                    return ValueTask.CompletedTask;

                _dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
