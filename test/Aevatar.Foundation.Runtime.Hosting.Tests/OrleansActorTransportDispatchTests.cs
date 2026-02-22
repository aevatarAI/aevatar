using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorTransportDispatchTests
{
    [Fact]
    public async Task HandleEventAsync_WhenTransportSenderConfigured_ShouldDispatchViaTransport()
    {
        var grain = new RecordingRuntimeActorGrain();
        var sender = new RecordingTransportEventSender();
        var actor = new OrleansActor("actor-1", grain, sender);
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "payload" }) };

        await actor.HandleEventAsync(envelope, CancellationToken.None);

        sender.Messages.Should().ContainSingle();
        sender.Messages.Single().TargetActorId.Should().Be("actor-1");
        grain.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task AgentProxy_WhenTransportSenderConfigured_ShouldDispatchViaTransport()
    {
        var grain = new RecordingRuntimeActorGrain();
        var sender = new RecordingTransportEventSender();
        var actor = new OrleansActor("actor-2", grain, sender);
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "payload" }) };

        await actor.Agent.HandleEventAsync(envelope, CancellationToken.None);

        sender.Messages.Should().ContainSingle();
        sender.Messages.Single().TargetActorId.Should().Be("actor-2");
        grain.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleEventAsync_WhenNoTransportSender_ShouldDispatchToGrain()
    {
        var grain = new RecordingRuntimeActorGrain();
        var actor = new OrleansActor("actor-3", grain);
        var envelope = new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "payload" }) };

        await actor.HandleEventAsync(envelope, CancellationToken.None);

        grain.DispatchCount.Should().Be(1);
    }

    private sealed class RecordingRuntimeActorGrain : IRuntimeActorGrain
    {
        public int DispatchCount { get; private set; }

        public Task<bool> InitializeAgentAsync(string agentTypeName) => Task.FromResult(true);

        public Task<bool> IsInitializedAsync() => Task.FromResult(true);

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            _ = EventEnvelope.Parser.ParseFrom(envelopeBytes);
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
