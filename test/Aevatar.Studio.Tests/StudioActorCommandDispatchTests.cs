using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioActorCommandDispatchTests
{
    [Fact]
    public async Task DispatchAsync_ShouldRejectMissingAdmission()
    {
        var actor = new StubActor("actor-alpha");
        var dispatch = new StudioActorCommandDispatch(new FixedDispatchService(
            CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>.Success(
                new StudioActorCommandReceipt(actor.Id, "command-alpha", "correlation-alpha"))));

        var act = () => dispatch.DispatchAsync(actor, new Empty(), "publisher-alpha");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*admission*");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectRejectedAdmission()
    {
        var actor = new StubActor("actor-alpha");
        var dispatch = new StudioActorCommandDispatch(new FixedDispatchService(
            CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>.Success(
                new StudioActorCommandReceipt(actor.Id, "command-alpha", "correlation-alpha"),
                new DispatchAdmission(
                    false,
                    "command-alpha",
                    DateTimeOffset.Parse("2026-07-27T00:00:00Z"),
                    actor.Id,
                    "correlation-alpha"))));

        var act = () => dispatch.DispatchAsync(actor, new Empty(), "publisher-alpha");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*admission*");
    }

    private sealed class FixedDispatchService(
        CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError> result)
        : ICommandDispatchService<StudioActorCommand, StudioActorCommandReceipt, StudioActorCommandStartError>
    {
        public Task<CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>> DispatchAsync(
            StudioActorCommand command,
            CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "stub-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
