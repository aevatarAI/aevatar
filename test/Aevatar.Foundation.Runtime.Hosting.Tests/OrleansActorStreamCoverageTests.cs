using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorStreamCoverageTests
{
    [Fact]
    public async Task ProduceAsync_ShouldValidateInputs()
    {
        var stream = CreateStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            stream.ProduceAsync<StringValue>(null!));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ProduceAsync(new StringValue { Value = "x" }, cts.Token));
    }

    [Fact]
    public async Task ProduceAsync_WithTypedMessage_ShouldWrapAndPublishToSource()
    {
        var provider = new RecordingOrleansStreamProvider();
        var stream = CreateStream(provider: provider);

        await stream.ProduceAsync(new StringValue { Value = "hello" });

        var source = provider.GetRecordedStream("actor-1");
        source.Published.Should().ContainSingle();
        source.Published[0].Payload!.Unpack<StringValue>().Value.Should().Be("hello");
        source.Published[0].Route!.Direction.Should().Be(EventDirection.Down);
    }

    [Fact]
    public async Task ProduceAsync_WithEnvelope_ShouldPreserveEnvelope()
    {
        var provider = new RecordingOrleansStreamProvider();
        var stream = CreateStream(provider: provider);
        var envelope = new EventEnvelope
        {
            Id = "evt-1",
            Route = new EnvelopeRoute
            {
                Direction = EventDirection.Both,
            },
            Payload = Any.Pack(new StringValue { Value = "direct" }),
        };

        await stream.ProduceAsync(envelope);

        var source = provider.GetRecordedStream("actor-1");
        source.Published.Should().ContainSingle();
        source.Published[0].Id.Should().Be("evt-1");
        source.Published[0].Route!.Direction.Should().Be(EventDirection.Both);
        source.Published[0].Payload!.Unpack<StringValue>().Value.Should().Be("direct");
    }

    [Fact]
    public async Task RelayAsync_ShouldHandleForwardingModesAndFilters()
    {
        var provider = new RecordingOrleansStreamProvider();
        var registry = new InMemoryForwardingRegistry();
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "actor-1",
            TargetStreamId = "target-handle",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [EventDirection.Down, EventDirection.Both],
        });
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "actor-1",
            TargetStreamId = "target-transit",
            ForwardingMode = StreamForwardingMode.TransitOnly,
            DirectionFilter = [EventDirection.Down, EventDirection.Both],
        });
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "actor-1",
            TargetStreamId = "target-up-only",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [EventDirection.Up],
        });
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "actor-1",
            TargetStreamId = "actor-1",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [EventDirection.Down, EventDirection.Both],
        });

        var stream = CreateStream(provider: provider, forwardingRegistry: registry);
        await stream.ProduceAsync(new StringValue { Value = "relay" });

        provider.GetRecordedStream("target-handle").Published.Should().ContainSingle();
        provider.GetRecordedStream("target-transit").Published.Should().BeEmpty();
        provider.GetRecordedStream("target-up-only").Published.Should().BeEmpty();
        StreamForwardingEnvelopeState.GetMode(provider.GetRecordedStream("target-handle").Published[0])
            .Should().Be(StreamForwardingHandleMode.HandleThenForward);
    }

    [Fact]
    public async Task RelayAsync_WhenPublishFails_ShouldSwallowAndContinue()
    {
        var provider = new RecordingOrleansStreamProvider();
        provider.GetRecordedStream("bad-target").ThrowOnPublish = true;

        var registry = new InMemoryForwardingRegistry();
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "actor-1",
            TargetStreamId = "bad-target",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
        });
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "actor-1",
            TargetStreamId = "good-target",
            ForwardingMode = StreamForwardingMode.HandleThenForward,
        });

        var stream = CreateStream(provider: provider, forwardingRegistry: registry);

        var act = () => stream.ProduceAsync(new StringValue { Value = "continue" });
        await act.Should().NotThrowAsync();

        provider.GetRecordedStream("good-target").Published.Should().ContainSingle();
    }

    [Fact]
    public async Task RelayAsync_ShouldNotLoopOnCyclicTopology()
    {
        var provider = new RecordingOrleansStreamProvider();
        var registry = new InMemoryForwardingRegistry();
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "actor-1",
            TargetStreamId = "middle",
            ForwardingMode = StreamForwardingMode.TransitOnly,
            DirectionFilter = [EventDirection.Down, EventDirection.Both],
        });
        await registry.UpsertAsync(new StreamForwardingBinding
        {
            SourceStreamId = "middle",
            TargetStreamId = "actor-1",
            ForwardingMode = StreamForwardingMode.TransitOnly,
            DirectionFilter = [EventDirection.Down, EventDirection.Both],
        });

        var stream = CreateStream(provider: provider, forwardingRegistry: registry);
        await stream.ProduceAsync(new StringValue { Value = "cycle" });

        provider.GetRecordedStream("middle").Published.Should().BeEmpty();
        provider.GetRecordedStream("actor-1").Published.Should().ContainSingle();
    }

    [Fact]
    public async Task SubscribeAsync_ShouldValidateHandler()
    {
        var stream = CreateStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            stream.SubscribeAsync<StringValue>(null!));
    }

    [Fact]
    public async Task SubscribeAsync_EventEnvelopeHandler_ShouldReceiveAndDisposeLeaseIdempotently()
    {
        var provider = new RecordingOrleansStreamProvider();
        var stream = CreateStream(provider: provider);
        var received = new List<EventEnvelope>();

        var lease = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            received.Add(envelope.Clone());
            return Task.CompletedTask;
        });

        var source = provider.GetRecordedStream("actor-1");
        await source.PushToObserversAsync(new EventEnvelope
        {
            Id = "evt-envelope",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            Route = new EnvelopeRoute
            {
                Direction = EventDirection.Down,
            },
        });

        received.Should().ContainSingle(x => x.Id == "evt-envelope");
        source.UnsubscribeCount.Should().Be(0);

        await lease.DisposeAsync();
        await lease.DisposeAsync();
        source.UnsubscribeCount.Should().Be(1);
    }

    [Fact]
    public async Task SubscribeAsync_TypedHandler_ShouldFilterPayloadAndDispatchMatchedType()
    {
        var provider = new RecordingOrleansStreamProvider();
        var stream = CreateStream(provider: provider);
        var received = new List<string>();

        await using var lease = await stream.SubscribeAsync<StringValue>(value =>
        {
            received.Add(value.Value);
            return Task.CompletedTask;
        });

        var source = provider.GetRecordedStream("actor-1");
        await source.PushToObserversAsync(new EventEnvelope
        {
            Id = "evt-null",
            Route = new EnvelopeRoute
            {
                Direction = EventDirection.Down,
            },
        });
        await source.PushToObserversAsync(new EventEnvelope
        {
            Id = "evt-mismatch",
            Route = new EnvelopeRoute
            {
                Direction = EventDirection.Down,
            },
            Payload = Any.Pack(new Int32Value { Value = 42 }),
        });
        await source.PushToObserversAsync(new EventEnvelope
        {
            Id = "evt-match",
            Route = new EnvelopeRoute
            {
                Direction = EventDirection.Down,
            },
            Payload = Any.Pack(new StringValue { Value = "ok" }),
        });

        received.Should().Equal("ok");
    }

    [Fact]
    public async Task RelayApis_ShouldValidateAndSupportCancellation()
    {
        var stream = CreateStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() => stream.UpsertRelayAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => stream.RemoveRelayAsync(""));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.UpsertRelayAsync(new StreamForwardingBinding
            {
                TargetStreamId = "t",
            }, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.RemoveRelayAsync("t", cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ListRelaysAsync(cts.Token));
    }

    [Fact]
    public async Task RelayApis_ShouldCloneBindingAndReadBackFromRegistry()
    {
        var registry = new InMemoryForwardingRegistry();
        var stream = CreateStream(forwardingRegistry: registry);

        var binding = new StreamForwardingBinding
        {
            SourceStreamId = "other",
            TargetStreamId = "target-1",
            ForwardingMode = StreamForwardingMode.TransitOnly,
            DirectionFilter = [EventDirection.Up],
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal) { "evt" },
            Version = 3,
            LeaseId = "lease-1",
        };

        await stream.UpsertRelayAsync(binding);
        var listed = await stream.ListRelaysAsync();
        listed.Should().ContainSingle();
        listed[0].SourceStreamId.Should().Be("actor-1");
        listed[0].TargetStreamId.Should().Be("target-1");
        listed[0].ForwardingMode.Should().Be(StreamForwardingMode.TransitOnly);
        listed[0].DirectionFilter.Should().BeEquivalentTo([EventDirection.Up]);
        listed[0].EventTypeFilter.Should().BeEquivalentTo(["evt"]);
        listed[0].Version.Should().Be(3);
        listed[0].LeaseId.Should().Be("lease-1");

        await stream.RemoveRelayAsync("target-1");
        (await stream.ListRelaysAsync()).Should().BeEmpty();
    }

    private static OrleansActorStream CreateStream(
        RecordingOrleansStreamProvider? provider = null,
        IStreamForwardingRegistry? forwardingRegistry = null)
    {
        return new OrleansActorStream(
            streamId: "actor-1",
            streamNamespace: "aevatar.events",
            streamProvider: provider ?? new RecordingOrleansStreamProvider(),
            forwardingRegistry: forwardingRegistry);
    }

    private sealed class InMemoryForwardingRegistry : IStreamForwardingRegistry
    {
        private readonly Dictionary<string, List<StreamForwardingBinding>> _bindings = new(StringComparer.Ordinal);

        public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ct.ThrowIfCancellationRequested();

            if (!_bindings.TryGetValue(binding.SourceStreamId, out var bySource))
            {
                bySource = [];
                _bindings[binding.SourceStreamId] = bySource;
            }

            var index = bySource.FindIndex(x => string.Equals(x.TargetStreamId, binding.TargetStreamId, StringComparison.Ordinal));
            var cloned = Clone(binding);
            if (index >= 0)
                bySource[index] = cloned;
            else
                bySource.Add(cloned);

            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_bindings.TryGetValue(sourceStreamId, out var bySource))
                return Task.CompletedTask;

            bySource.RemoveAll(x => string.Equals(x.TargetStreamId, targetStreamId, StringComparison.Ordinal));
            if (bySource.Count == 0)
                _bindings.Remove(sourceStreamId);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(string sourceStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_bindings.TryGetValue(sourceStreamId, out var bySource))
                return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);

            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(bySource.Select(Clone).ToList());
        }

        private static StreamForwardingBinding Clone(StreamForwardingBinding binding) =>
            new()
            {
                SourceStreamId = binding.SourceStreamId,
                TargetStreamId = binding.TargetStreamId,
                ForwardingMode = binding.ForwardingMode,
                DirectionFilter = new HashSet<EventDirection>(binding.DirectionFilter),
                EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
                Version = binding.Version,
                LeaseId = binding.LeaseId,
            };
    }

    private sealed class RecordingOrleansStreamProvider : global::Orleans.Streams.IStreamProvider
    {
        private readonly Dictionary<string, RecordingAsyncStream> _streams = new(StringComparer.Ordinal);

        public string Name => "recording-provider";

        public bool IsRewindable => false;

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            if (typeof(T) != typeof(EventEnvelope))
                throw new NotSupportedException($"Unsupported stream type: {typeof(T).FullName}");

            var key = streamId.GetKeyAsString();
            if (!_streams.TryGetValue(key, out var stream))
            {
                stream = new RecordingAsyncStream(streamId, Name);
                _streams[key] = stream;
            }

            return (IAsyncStream<T>)(object)stream;
        }

        public RecordingAsyncStream GetRecordedStream(string streamId)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                var id = StreamId.Create("aevatar.events", streamId);
                stream = new RecordingAsyncStream(id, Name);
                _streams[streamId] = stream;
            }

            return stream;
        }
    }

    private sealed class RecordingAsyncStream : IAsyncStream<EventEnvelope>
    {
        private readonly List<IAsyncObserver<EventEnvelope>> _observers = [];
        private readonly List<RecordingStreamSubscriptionHandle> _handles = [];

        public RecordingAsyncStream(StreamId streamId, string providerName)
        {
            StreamId = streamId;
            ProviderName = providerName;
        }

        public List<EventEnvelope> Published { get; } = [];

        public int UnsubscribeCount { get; private set; }

        public bool ThrowOnPublish { get; set; }

        public bool IsRewindable => false;

        public string ProviderName { get; }

        public StreamId StreamId { get; }

        public Task OnNextAsync(EventEnvelope item, StreamSequenceToken? token = null)
        {
            _ = token;
            if (ThrowOnPublish)
                throw new InvalidOperationException("publish failure");

            Published.Add(item.Clone());
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex)
        {
            _ = ex;
            return Task.CompletedTask;
        }

        public Task OnNextBatchAsync(IEnumerable<EventEnvelope> batch, StreamSequenceToken? token = null)
        {
            _ = token;
            foreach (var item in batch)
            {
                Published.Add(item.Clone());
            }

            return Task.CompletedTask;
        }

        public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(IAsyncObserver<EventEnvelope> observer)
        {
            _observers.Add(observer);
            var handle = new RecordingStreamSubscriptionHandle(this);
            _handles.Add(handle);
            return Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(handle);
        }

        public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
            IAsyncObserver<EventEnvelope> observer,
            StreamSequenceToken? token,
            string? filterData = null)
        {
            _ = token;
            _ = filterData;
            return SubscribeAsync(observer);
        }

        public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(IAsyncBatchObserver<EventEnvelope> observer)
        {
            _ = observer;
            throw new NotSupportedException("Batch observer is not used in tests.");
        }

        public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
            IAsyncBatchObserver<EventEnvelope> observer,
            StreamSequenceToken? token)
        {
            _ = observer;
            _ = token;
            throw new NotSupportedException("Batch observer is not used in tests.");
        }

        public Task<IList<StreamSubscriptionHandle<EventEnvelope>>> GetAllSubscriptionHandles()
        {
            return Task.FromResult<IList<StreamSubscriptionHandle<EventEnvelope>>>(
                _handles.Cast<StreamSubscriptionHandle<EventEnvelope>>().ToList());
        }

        public bool Equals(IAsyncStream<EventEnvelope>? other) => ReferenceEquals(this, other);

        public int CompareTo(IAsyncStream<EventEnvelope>? other)
        {
            if (ReferenceEquals(this, other))
                return 0;
            return string.Compare(StreamId.ToString(), other?.StreamId.ToString(), StringComparison.Ordinal);
        }

        public async Task PushToObserversAsync(EventEnvelope envelope)
        {
            foreach (var observer in _observers.ToList())
            {
                await observer.OnNextAsync(envelope.Clone());
            }
        }

        private void OnUnsubscribed()
        {
            UnsubscribeCount++;
        }

        private sealed class RecordingStreamSubscriptionHandle : StreamSubscriptionHandle<EventEnvelope>
        {
            private readonly RecordingAsyncStream _owner;

            public RecordingStreamSubscriptionHandle(RecordingAsyncStream owner)
            {
                _owner = owner;
            }

            public override StreamId StreamId => _owner.StreamId;

            public override string ProviderName => _owner.ProviderName;

            public override Guid HandleId { get; } = Guid.NewGuid();

            public override Task UnsubscribeAsync()
            {
                _owner.OnUnsubscribed();
                return Task.CompletedTask;
            }

            public override Task<StreamSubscriptionHandle<EventEnvelope>> ResumeAsync(
                IAsyncObserver<EventEnvelope> observer,
                StreamSequenceToken? token = null)
            {
                _ = observer;
                _ = token;
                return Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(this);
            }

            public override Task<StreamSubscriptionHandle<EventEnvelope>> ResumeAsync(
                IAsyncBatchObserver<EventEnvelope> observer,
                StreamSequenceToken? token = null)
            {
                _ = observer;
                _ = token;
                return Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(this);
            }

            public override bool Equals(StreamSubscriptionHandle<EventEnvelope>? other) =>
                other is RecordingStreamSubscriptionHandle handle && handle.HandleId == HandleId;
        }
    }
}
