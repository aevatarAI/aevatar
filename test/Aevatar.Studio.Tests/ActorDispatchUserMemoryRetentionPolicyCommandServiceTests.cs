using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class ActorDispatchUserMemoryRetentionPolicyCommandServiceTests
{
    [Fact]
    public async Task ReplaceAsync_ShouldDispatchTypedScopeCommandAndReturnHonestReceipt()
    {
        var bootstrap = new RecordingBootstrap();
        var ackedAt = DateTimeOffset.Parse("2026-08-25T08:00:00Z");
        var dispatch = new RecordingDispatchPort(new DispatchAdmission(
            true,
            "command-alpha",
            ackedAt,
            "user-memory-scope-alpha",
            "correlation-alpha"));
        var service = new ActorDispatchUserMemoryRetentionPolicyCommandService(bootstrap, dispatch);

        var receipt = await service.ReplaceAsync(new ReplaceUserMemoryRetentionPolicy(
            UserMemoryOwnerKey.ForScope("scope-alpha"),
            [
                new(
                    Aevatar.Studio.Application.Studio.Abstractions.UserMemoryCategory.Preference,
                    8,
                    20),
            ],
            7,
            "mutation-alpha"));

        bootstrap.ActorIds.Should().ContainSingle("user-memory-scope-alpha");
        var dispatched = dispatch.Dispatches.Should().ContainSingle().Subject;
        dispatched.ActorId.Should().Be("user-memory-scope-alpha");
        dispatched.Envelope.Route.PublisherActorId.Should()
            .Be("aevatar.studio.projection.user-memory-retention-policy");
        var payload = dispatched.Envelope.Payload.Unpack<ReplaceUserMemoryRetentionPolicyCommand>();
        payload.ExpectedStateVersion.Should().Be(7);
        payload.MutationId.Should().Be("mutation-alpha");
        payload.Rules.Should().ContainSingle();
        payload.Rules[0].Category.Should().Be(Aevatar.GAgents.UserMemory.UserMemoryCategory.Preference);
        payload.Rules[0].MaxEntries.Should().Be(8);
        payload.Rules[0].EvictionRank.Should().Be(20);
        receipt.Should().Be(new UserConfigSaveReceipt(
            true,
            "command-alpha",
            UserConfigCommandAckStage.Accepted,
            "user-memory-scope-alpha",
            "correlation-alpha",
            ackedAt));
    }

    [Fact]
    public async Task ReplaceAsync_WithInvalidRule_ShouldRejectBeforeActorBootstrap()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = RecordingDispatchPort.Accepting();
        var service = new ActorDispatchUserMemoryRetentionPolicyCommandService(bootstrap, dispatch);
        var command = new ReplaceUserMemoryRetentionPolicy(
            UserMemoryOwnerKey.ForScope("scope-alpha"),
            [
                new(
                    Aevatar.Studio.Application.Studio.Abstractions.UserMemoryCategory.Unspecified,
                    0,
                    100),
            ],
            0,
            "mutation-invalid");

        var act = () => service.ReplaceAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("user_memory_policy_category_invalid");
        bootstrap.ActorIds.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public void AddStudioProjectionComponents_ShouldRegisterRetentionPolicyCommandPort()
    {
        var services = new ServiceCollection();

        services.AddStudioProjectionComponents();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IUserMemoryRetentionPolicyCommandPort) &&
            descriptor.ImplementationType ==
                typeof(ActorDispatchUserMemoryRetentionPolicyCommandService));
    }

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> ActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            ActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingDispatchPort(DispatchAdmission admission) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public static RecordingDispatchPort Accepting() => new(new DispatchAdmission(
            true,
            "command-alpha",
            DateTimeOffset.Parse("2026-08-25T08:00:00Z"),
            "user-memory-scope-alpha",
            "correlation-alpha"));

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(admission);
        }
    }
}
