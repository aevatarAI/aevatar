using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for the Channel.Identity CQRS dispatch adapter.
/// </summary>
public sealed class ChannelIdentityOAuthCommandDispatchTests
{
    [Fact]
    public async Task DispatchAsync_BuildsEnvelopeDispatchesThroughPortAndReturnsAcceptedReceipt()
    {
        var actor = new StubActor("actor-wrapper-id");
        var runtime = new RecordingActorRuntime(actor);
        var dispatchPort = new RecordingDispatchPort();
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant-1",
            ExternalUserId = "user-1",
        };
        var targetActorId = subject.ToActorId();
        var service = new ChannelIdentityOAuthCommandDispatch<CommitBindingCommand, ExternalIdentityBindingGAgent>(
            runtime,
            dispatchPort,
            new ChannelIdentityOAuthCommandRoute<CommitBindingCommand>(
                _ => new ChannelIdentityOAuthCommandTarget(targetActorId, "publisher-1")));

        var command = new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd-1",
            OwnerScopeId = "owner-user-1",
        };

        var result = await service.DispatchAsync(command);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().NotBeNull();
        result.Receipt!.ActorId.Should().Be(targetActorId);
        result.Receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        result.Receipt.CorrelationId.Should().Be(result.Receipt.CommandId);
        runtime.Created.Should().ContainSingle().Which.Should().Be(targetActorId);
        dispatchPort.Dispatched.Should().ContainSingle();
        var dispatched = dispatchPort.Dispatched[0];
        dispatched.ActorId.Should().Be(actor.Id);
        dispatched.Envelope.Id.Should().Be(result.Receipt.CommandId);
        dispatched.Envelope.Route.PublisherActorId.Should().Be("publisher-1");
        dispatched.Envelope.Route.Direct.TargetActorId.Should().Be(targetActorId);
        dispatched.Envelope.Payload.Unpack<CommitBindingCommand>().Should().Be(command);
    }

    [Fact]
    public async Task DispatchAsync_ReturnsInvalidTargetWithoutActivatingActor()
    {
        var runtime = new RecordingActorRuntime(new StubActor("unused"));
        var dispatchPort = new RecordingDispatchPort();
        var service = new ChannelIdentityOAuthCommandDispatch<CommitBindingCommand, ExternalIdentityBindingGAgent>(
            runtime,
            dispatchPort,
            new ChannelIdentityOAuthCommandRoute<CommitBindingCommand>(
                _ => new ChannelIdentityOAuthCommandTarget(" ", "publisher-1")));

        var result = await service.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = new ExternalSubjectRef
            {
                Platform = "lark",
                Tenant = "tenant-1",
                ExternalUserId = "user-1",
            },
            BindingId = "bnd-1",
            OwnerScopeId = "owner-user-1",
        });

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(ChannelIdentityOAuthDispatchError.InvalidTarget);
        result.Receipt.Should().BeNull();
        runtime.Created.Should().BeEmpty();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    private sealed class RecordingActorRuntime(IActor actor) : IActorRuntime
    {
        public List<string?> Created { get; } = new();

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            Created.Add(id);
            return Task.FromResult(actor);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            Created.Add(id);
            return Task.FromResult(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<DispatchedEnvelope> Dispatched { get; } = new();

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatched.Add(new DispatchedEnvelope(actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed record DispatchedEnvelope(string ActorId, EventEnvelope Envelope);

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new StubAgent(id);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
