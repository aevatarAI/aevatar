using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Runtime.Implementations.Orleans;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Orleans;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class OrleansGrainEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_WhenDirectionIsSelf_ShouldDispatchWithoutPublisherChain()
    {
        EventEnvelope? dispatched = null;
        var publisher = CreatePublisher(
            actorId: "actor-self",
            onDispatchToSelf: envelope =>
            {
                dispatched = envelope;
                return Task.CompletedTask;
            },
            resolveGrain: _ => throw new InvalidOperationException("Remote grain should not be resolved for self dispatch."));

        await publisher.PublishAsync(new StringValue { Value = "hello" }, EventDirection.Self, CancellationToken.None);

        dispatched.Should().NotBeNull();
        dispatched!.Metadata.ContainsKey(PublisherChainMetadata.PublishersMetadataKey).Should().BeFalse();
    }

    [Fact]
    public async Task PublishAsync_WhenSourceContainsPublisherChain_ShouldAppendCurrentPublisher()
    {
        var remoteGrain = new RecordingRuntimeActorGrain();
        var publisher = CreatePublisher(
            actorId: "child-actor",
            onDispatchToSelf: _ => Task.CompletedTask,
            resolveGrain: _ => remoteGrain,
            getParentId: () => "parent-actor");

        var inbound = new EventEnvelope();
        inbound.Metadata[PublisherChainMetadata.PublishersMetadataKey] = "parent-actor";

        await publisher.PublishAsync(
            new StringValue { Value = "reply" },
            EventDirection.Up,
            CancellationToken.None,
            inbound);

        remoteGrain.LastEnvelope.Should().NotBeNull();
        remoteGrain.LastEnvelope!.Metadata.TryGetValue(PublisherChainMetadata.PublishersMetadataKey, out var chain)
            .Should().BeTrue();
        chain.Should().Be("parent-actor,child-actor");
    }

    [Fact]
    public async Task SendToAsync_WhenTargetIsSelf_ShouldDispatchWithoutPublisherChain()
    {
        EventEnvelope? dispatched = null;
        var publisher = CreatePublisher(
            actorId: "actor-self",
            onDispatchToSelf: envelope =>
            {
                dispatched = envelope;
                return Task.CompletedTask;
            },
            resolveGrain: _ => throw new InvalidOperationException("Remote grain should not be resolved for self send."));

        await publisher.SendToAsync("actor-self", new StringValue { Value = "direct" }, CancellationToken.None);

        dispatched.Should().NotBeNull();
        dispatched!.Metadata.ContainsKey(PublisherChainMetadata.PublishersMetadataKey).Should().BeFalse();
    }

    [Fact]
    public async Task SendToAsync_WhenSourceContainsPublisherChain_ShouldAppendCurrentPublisher()
    {
        var remoteGrain = new RecordingRuntimeActorGrain();
        var publisher = CreatePublisher(
            actorId: "sender",
            onDispatchToSelf: _ => Task.CompletedTask,
            resolveGrain: _ => remoteGrain);

        var inbound = new EventEnvelope();
        inbound.Metadata[PublisherChainMetadata.PublishersMetadataKey] = "upstream";

        await publisher.SendToAsync(
            "receiver",
            new StringValue { Value = "direct" },
            CancellationToken.None,
            inbound);

        remoteGrain.LastEnvelope.Should().NotBeNull();
        remoteGrain.LastEnvelope!.Metadata.TryGetValue(PublisherChainMetadata.PublishersMetadataKey, out var chain)
            .Should().BeTrue();
        chain.Should().Be("upstream,sender");
    }

    [Fact]
    public async Task SendToAsync_WhenTransportSenderConfigured_ShouldUseTransportSender()
    {
        var sender = new RecordingTransportEventSender();
        var publisher = CreatePublisher(
            actorId: "sender",
            onDispatchToSelf: _ => Task.CompletedTask,
            resolveGrain: _ => throw new InvalidOperationException("Grain dispatch should not be used when transport sender is configured."),
            transportEventSender: sender);

        await publisher.SendToAsync("receiver", new StringValue { Value = "transport" }, CancellationToken.None);

        sender.Messages.Should().ContainSingle();
        var delivered = sender.Messages.Single();
        delivered.TargetActorId.Should().Be("receiver");
        delivered.Envelope.Payload!.Unpack<StringValue>().Value.Should().Be("transport");
        delivered.Envelope.Metadata.TryGetValue(PublisherChainMetadata.PublishersMetadataKey, out var chain)
            .Should().BeTrue();
        chain.Should().Be("sender");
    }

    [Fact]
    public async Task PublishAsync_WhenDirectionIsDown_ShouldRouteByForwardingRegistry()
    {
        var childA = new RecordingRuntimeActorGrain();
        var childB = new RecordingRuntimeActorGrain();
        var registry = new InMemoryStreamForwardingRegistry();
        await registry.UpsertAsync(
            new StreamForwardingBinding
            {
                SourceStreamId = "root",
                TargetStreamId = "child-a",
                ForwardingMode = StreamForwardingMode.HandleThenForward,
                DirectionFilter =
                [
                    EventDirection.Down,
                    EventDirection.Both,
                ],
            },
            CancellationToken.None);

        var publisher = CreatePublisher(
            actorId: "root",
            onDispatchToSelf: _ => Task.CompletedTask,
            resolveGrain: actorId => actorId switch
            {
                "child-a" => childA,
                "child-b" => childB,
                _ => throw new InvalidOperationException($"Unexpected grain id {actorId}."),
            },
            forwardingRegistry: registry);

        await publisher.PublishAsync(new StringValue { Value = "task" }, EventDirection.Down, CancellationToken.None);

        childA.LastEnvelope.Should().NotBeNull();
        childA.LastEnvelope!.Payload!.Unpack<StringValue>().Value.Should().Be("task");
        childA.LastEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardSourceKey].Should().Be("root");
        childA.LastEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey].Should().Be("child-a");
        childA.LastEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardModeKey]
            .Should().Be(StreamForwardingEnvelopeMetadata.ForwardModeHandle);
        childB.LastEnvelope.Should().BeNull();
    }

    [Fact]
    public async Task PublishAsync_WhenTransitOnlyBindingConfigured_ShouldSkipTransitActorAndReachLeaf()
    {
        var middle = new RecordingRuntimeActorGrain();
        var leaf = new RecordingRuntimeActorGrain();
        var registry = new InMemoryStreamForwardingRegistry();
        await registry.UpsertAsync(
            new StreamForwardingBinding
            {
                SourceStreamId = "root",
                TargetStreamId = "middle",
                ForwardingMode = StreamForwardingMode.TransitOnly,
                DirectionFilter =
                [
                    EventDirection.Down,
                    EventDirection.Both,
                ],
            },
            CancellationToken.None);
        await registry.UpsertAsync(
            new StreamForwardingBinding
            {
                SourceStreamId = "middle",
                TargetStreamId = "leaf",
                ForwardingMode = StreamForwardingMode.HandleThenForward,
                DirectionFilter =
                [
                    EventDirection.Down,
                    EventDirection.Both,
                ],
            },
            CancellationToken.None);

        var publisher = CreatePublisher(
            actorId: "root",
            onDispatchToSelf: _ => Task.CompletedTask,
            resolveGrain: actorId => actorId switch
            {
                "middle" => middle,
                "leaf" => leaf,
                _ => throw new InvalidOperationException($"Unexpected grain id {actorId}."),
            },
            forwardingRegistry: registry);

        await publisher.PublishAsync(new StringValue { Value = "transit" }, EventDirection.Down, CancellationToken.None);

        middle.LastEnvelope.Should().BeNull();
        leaf.LastEnvelope.Should().NotBeNull();
        leaf.LastEnvelope!.Payload!.Unpack<StringValue>().Value.Should().Be("transit");
        leaf.LastEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardSourceKey].Should().Be("middle");
        leaf.LastEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey].Should().Be("leaf");
        leaf.LastEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardModeKey]
            .Should().Be(StreamForwardingEnvelopeMetadata.ForwardModeHandle);
    }

    [Fact]
    public async Task PublishAsync_WhenForwardingGraphContainsCycle_ShouldSkipLoopbackTarget()
    {
        var middle = new RecordingRuntimeActorGrain();
        var selfDispatchCount = 0;
        var registry = new InMemoryStreamForwardingRegistry();
        await registry.UpsertAsync(StreamForwardingRules.CreateHierarchyBinding("root", "middle"), CancellationToken.None);
        await registry.UpsertAsync(StreamForwardingRules.CreateHierarchyBinding("middle", "root"), CancellationToken.None);

        var publisher = CreatePublisher(
            actorId: "root",
            onDispatchToSelf: _ =>
            {
                Interlocked.Increment(ref selfDispatchCount);
                return Task.CompletedTask;
            },
            resolveGrain: actorId => actorId switch
            {
                "middle" => middle,
                _ => throw new InvalidOperationException($"Unexpected grain id {actorId}."),
            },
            forwardingRegistry: registry);

        await publisher.PublishAsync(new StringValue { Value = "cycle" }, EventDirection.Down, CancellationToken.None);

        middle.DispatchCount.Should().Be(1);
        selfDispatchCount.Should().Be(0);
        middle.LastEnvelope.Should().NotBeNull();
        middle.LastEnvelope!.Metadata[PublisherChainMetadata.PublishersMetadataKey].Should().Be("root");
    }

    private static OrleansGrainEventPublisher CreatePublisher(
        string actorId,
        Func<EventEnvelope, Task> onDispatchToSelf,
        Func<string, IRuntimeActorGrain> resolveGrain,
        Func<string?>? getParentId = null,
        IStreamForwardingRegistry? forwardingRegistry = null,
        IOrleansTransportEventSender? transportEventSender = null)
    {
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grainFactory).ResolveGrain = resolveGrain;

        return new OrleansGrainEventPublisher(
            actorId,
            grainFactory,
            getParentId ?? (() => null),
            onDispatchToSelf,
            new DefaultEnvelopePropagationPolicy(new DefaultCorrelationLinkPolicy()),
            forwardingRegistry ?? new InMemoryStreamForwardingRegistry(),
            transportEventSender);
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
        public EventEnvelope? LastEnvelope { get; private set; }
        public int DispatchCount { get; private set; }

        public Task<bool> InitializeAgentAsync(string agentTypeName) => Task.FromResult(true);

        public Task<bool> IsInitializedAsync() => Task.FromResult(true);

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            LastEnvelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
            DispatchCount++;
            return Task.CompletedTask;
        }

        public Task AddChildAsync(string childId) => Task.CompletedTask;

        public Task RemoveChildAsync(string childId) => Task.CompletedTask;

        public Task SetParentAsync(string parentId) => Task.CompletedTask;

        public Task ClearParentAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetChildrenAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> GetParentAsync() => Task.FromResult<string?>(null);

        public Task<string> GetDescriptionAsync() => Task.FromResult("recording");

        public Task<string> GetAgentTypeNameAsync() => Task.FromResult(string.Empty);

        public Task DeactivateAsync() => Task.CompletedTask;
    }

    private sealed class RecordingTransportEventSender : IOrleansTransportEventSender
    {
        public List<(string TargetActorId, EventEnvelope Envelope)> Messages { get; } = [];

        public Task SendAsync(string targetActorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Messages.Add((targetActorId, envelope.Clone()));
            return Task.CompletedTask;
        }
    }
}
