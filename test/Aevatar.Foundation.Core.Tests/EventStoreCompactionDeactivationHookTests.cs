using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Actors;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class EventStoreCompactionDeactivationHookTests
{
    [Fact]
    public async Task OnDeactivatedAsync_ShouldInvokeSchedulerWithActorId()
    {
        var scheduler = new RecordingScheduler();
        var hook = new EventStoreCompactionDeactivationHook(scheduler);

        await hook.OnDeactivatedAsync("actor-1");

        scheduler.Calls.ShouldBe(1);
        scheduler.LastActorId.ShouldBe("actor-1");
    }

    [Fact]
    public async Task OnDeactivatedAsync_WhenSchedulerThrows_ShouldBubble()
    {
        var scheduler = new ThrowingScheduler();
        var hook = new EventStoreCompactionDeactivationHook(scheduler);

        await Should.ThrowAsync<InvalidOperationException>(() => hook.OnDeactivatedAsync("actor-2"));

        scheduler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task OnDeactivatedAsync_WhenActorIdIsEmpty_ShouldSkip()
    {
        var scheduler = new RecordingScheduler();
        var hook = new EventStoreCompactionDeactivationHook(scheduler);

        await hook.OnDeactivatedAsync(" ");

        scheduler.Calls.ShouldBe(0);
    }

    private sealed class RecordingScheduler : IEventStoreCompactionScheduler
    {
        public int Calls { get; private set; }
        public string? LastActorId { get; private set; }

        public Task ScheduleAsync(string agentId, long compactToVersion, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunOnIdleAsync(string agentId, CancellationToken ct = default)
        {
            Calls++;
            LastActorId = agentId;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingScheduler : IEventStoreCompactionScheduler
    {
        public int Calls { get; private set; }

        public Task ScheduleAsync(string agentId, long compactToVersion, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunOnIdleAsync(string agentId, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("scheduler-failed");
        }
    }
}
