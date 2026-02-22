using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Runtime.Implementations.Orleans;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Propagation;
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
        dispatched!.Metadata.ContainsKey(OrleansRuntimeConstants.PublishersMetadataKey).Should().BeFalse();
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
        inbound.Metadata[OrleansRuntimeConstants.PublishersMetadataKey] = "parent-actor";

        await publisher.PublishAsync(
            new StringValue { Value = "reply" },
            EventDirection.Up,
            CancellationToken.None,
            inbound);

        remoteGrain.LastEnvelope.Should().NotBeNull();
        remoteGrain.LastEnvelope!.Metadata.TryGetValue(OrleansRuntimeConstants.PublishersMetadataKey, out var chain)
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
        dispatched!.Metadata.ContainsKey(OrleansRuntimeConstants.PublishersMetadataKey).Should().BeFalse();
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
        inbound.Metadata[OrleansRuntimeConstants.PublishersMetadataKey] = "upstream";

        await publisher.SendToAsync(
            "receiver",
            new StringValue { Value = "direct" },
            CancellationToken.None,
            inbound);

        remoteGrain.LastEnvelope.Should().NotBeNull();
        remoteGrain.LastEnvelope!.Metadata.TryGetValue(OrleansRuntimeConstants.PublishersMetadataKey, out var chain)
            .Should().BeTrue();
        chain.Should().Be("upstream,sender");
    }

    private static OrleansGrainEventPublisher CreatePublisher(
        string actorId,
        Func<EventEnvelope, Task> onDispatchToSelf,
        Func<string, IRuntimeActorGrain> resolveGrain,
        Func<string?>? getParentId = null,
        Func<IReadOnlyList<string>>? getChildrenIds = null)
    {
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grainFactory).ResolveGrain = resolveGrain;

        return new OrleansGrainEventPublisher(
            actorId,
            grainFactory,
            getParentId ?? (() => null),
            getChildrenIds ?? (() => []),
            onDispatchToSelf,
            new DefaultEnvelopePropagationPolicy(new DefaultCorrelationLinkPolicy()),
            new PublisherChainLoopGuard());
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

        public Task<bool> InitializeAgentAsync(string agentTypeName) => Task.FromResult(true);

        public Task<bool> IsInitializedAsync() => Task.FromResult(true);

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            LastEnvelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
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
}
