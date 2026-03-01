using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Hosting.Ports;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Hosting.Tests;

public class RuntimeGAgentInvocationPortTests
{
    [Fact]
    public async Task InvokeAsync_ShouldDispatchEventEnvelope_ToTargetActor()
    {
        var runtime = new FakeRuntime();
        var actor = new FakeActor("actor-1");
        runtime.Actor = actor;
        var port = new RuntimeGAgentInvocationPort(runtime);

        await port.InvokeAsync("actor-1", new StringValue { Value = "hello" }, "run-1", CancellationToken.None);

        actor.ReceivedEnvelope.Should().NotBeNull();
        actor.ReceivedEnvelope!.TargetActorId.Should().Be("actor-1");
        actor.ReceivedEnvelope!.PublisherId.Should().Be("scripting.gagent.invocation");
        actor.ReceivedEnvelope!.Direction.Should().Be(EventDirection.Self);
        actor.ReceivedEnvelope!.CorrelationId.Should().Be("run-1");
        actor.ReceivedEnvelope!.Payload!.Is(StringValue.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenTargetActorNotFound()
    {
        var runtime = new FakeRuntime();
        var port = new RuntimeGAgentInvocationPort(runtime);

        var act = () => port.InvokeAsync("missing", new StringValue { Value = "x" }, "run-2", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Target GAgent not found*");
    }

    private sealed class FakeRuntime : IActorRuntime
    {
        public IActor? Actor { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(Actor?.Id == id ? Actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(Actor?.Id == id);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RestoreAllAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public EventEnvelope? ReceivedEnvelope { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ReceivedEnvelope = envelope;
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
