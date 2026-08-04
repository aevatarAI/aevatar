using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.UserMemory;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class UserMemoryGAgentCommandTests
{
    private const string ActorId = "user-memory-user-gamma";

    [Fact]
    public async Task TypedCommands_ShouldCommitUserMemoryFactsAndChangeOnlyOwnerState()
    {
        const string conversationId = "conversation-alpha";
        const string sessionId = "session-beta";
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(eventStore);

        await agent.HandleEventAsync(Envelope(new AddUserMemoryEntryCommand
        {
            Entry = new UserMemoryEntryProto
            {
                Id = "memory-delta",
                Category = UserMemoryCategory.Preference,
                Content = "Prefer concise status updates",
                Source = UserMemorySource.Explicit,
                CreatedAtMs = 1_750_000_000_000,
                UpdatedAtMs = 1_750_000_001_000,
            },
        }));

        ActorId.Should().NotBe(conversationId);
        ActorId.Should().NotBe(sessionId);
        var entry = agent.State.Entries.Should().ContainSingle().Subject;
        entry.Id.Should().Be("memory-delta");
        entry.Category.Should().Be(UserMemoryCategory.Preference);
        entry.Source.Should().Be(UserMemorySource.Explicit);
        var added = (await eventStore.GetEventsAsync(ActorId)).Should().ContainSingle().Subject;
        added.EventData.Is(MemoryEntryAddedEvent.Descriptor).Should().BeTrue();

        await agent.HandleEventAsync(Envelope(new RemoveUserMemoryEntryCommand
        {
            EntryId = "memory-delta",
        }));

        agent.State.Entries.Should().BeEmpty();
        var events = await eventStore.GetEventsAsync(ActorId);
        events.Should().HaveCount(2);
        events[1].EventData.Is(MemoryEntryRemovedEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task AddCommand_WithUntypedCategory_ShouldFailClosedWithoutCommitting()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(eventStore);
        var command = new AddUserMemoryEntryCommand
        {
            Entry = new UserMemoryEntryProto
            {
                Id = "memory-delta",
                Category = UserMemoryCategory.Unspecified,
                Content = "Do not accept an untyped category",
                Source = UserMemorySource.Explicit,
            },
        };

        var act = () => agent.HandleEventAsync(Envelope(command));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("user_memory_category_invalid");
        agent.State.Entries.Should().BeEmpty();
        (await eventStore.GetEventsAsync(ActorId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddCommand_WithUnreadableTimestamp_ShouldFailClosedWithoutCommitting()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(eventStore);
        var command = new AddUserMemoryEntryCommand
        {
            Entry = new UserMemoryEntryProto
            {
                Id = "memory-delta",
                Category = UserMemoryCategory.Preference,
                Content = "Do not persist timestamps that the read model cannot map",
                Source = UserMemorySource.Explicit,
                CreatedAtMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(),
                UpdatedAtMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() + 1,
            },
        };

        var act = () => agent.HandleEventAsync(Envelope(command));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("user_memory_timestamp_invalid");
        agent.State.Entries.Should().BeEmpty();
        (await eventStore.GetEventsAsync(ActorId)).Should().BeEmpty();
    }

    private static EventEnvelope Envelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", ActorId),
        };

    private static async Task<UserMemoryGAgent> CreateAgentAsync(IEventStore eventStore)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IActorDispatchPort, RecordingActorDispatchPort>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new UserMemoryGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<UserMemoryState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [ActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(new DispatchAdmission(
                true,
                actorId,
                DateTimeOffset.UtcNow,
                envelope.Id,
                envelope.Propagation?.CorrelationId ?? string.Empty));
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(Lease(request.ActorId, request.CallbackId));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(Lease(request.ActorId, request.CallbackId));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;

        private static RuntimeCallbackLease Lease(string actorId, string callbackId) =>
            new(actorId, callbackId, 1, RuntimeCallbackBackend.InMemory);
    }
}
