using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelScheduleRunnerTests
{
    [Fact]
    public async Task ScheduleNextRunAsync_UsesFakeClockSample_ForDueTimeAndCronBase()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var resolver = new FakeTimeZoneResolver(TimeZoneInfo.Utc);
        var source = new TestSchedulable
        {
            Schedule =
            {
                Enabled = true,
                Cron = "30 9 * * *",
                Timezone = "Custom/Test",
            },
        };
        var scheduled = new List<ScheduledTimeout>();
        var persisted = new List<DateTimeOffset>();
        var runner = CreateRunner(source, clock, resolver, scheduled, persisted);

        await runner.ScheduleNextRunAsync(CancellationToken.None);

        clock.ReadCount.Should().Be(1);
        resolver.RequestedTimezones.Should().ContainSingle().Which.Should().Be("Custom/Test");
        scheduled.Should().ContainSingle();
        scheduled[0].DueTime.Should().Be(TimeSpan.FromMinutes(30));
        persisted.Should().ContainSingle().Which.Should().Be(
            new DateTimeOffset(2026, 4, 14, 9, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task BootstrapOnActivateAsync_WhenNextRunIsFuture_DoesNotReschedule()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var resolver = new FakeTimeZoneResolver(TimeZoneInfo.Utc);
        var source = new TestSchedulable
        {
            Schedule =
            {
                Enabled = true,
                Cron = "30 9 * * *",
                Timezone = "UTC",
                NextRunAt = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 4, 14, 9, 30, 0, TimeSpan.Zero)),
            },
        };
        var scheduled = new List<ScheduledTimeout>();
        var persisted = new List<DateTimeOffset>();
        var runner = CreateRunner(source, clock, resolver, scheduled, persisted);

        await runner.BootstrapOnActivateAsync(CancellationToken.None);

        clock.ReadCount.Should().Be(1);
        resolver.RequestedTimezones.Should().BeEmpty();
        scheduled.Should().BeEmpty();
        persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task BootstrapOnActivateAsync_WhenNextRunElapsed_UsesSameFakeClockSample_ForReschedule()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var resolver = new FakeTimeZoneResolver(TimeZoneInfo.Utc);
        var source = new TestSchedulable
        {
            Schedule =
            {
                Enabled = true,
                Cron = "30 9 * * *",
                Timezone = "UTC",
                NextRunAt = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 4, 14, 8, 30, 0, TimeSpan.Zero)),
            },
        };
        var scheduled = new List<ScheduledTimeout>();
        var persisted = new List<DateTimeOffset>();
        var runner = CreateRunner(source, clock, resolver, scheduled, persisted);

        await runner.BootstrapOnActivateAsync(CancellationToken.None);

        clock.ReadCount.Should().Be(1);
        scheduled.Should().ContainSingle();
        scheduled[0].DueTime.Should().Be(TimeSpan.FromMinutes(30));
        persisted.Should().ContainSingle().Which.Should().Be(
            new DateTimeOffset(2026, 4, 14, 9, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ScheduleNextRunAsync_WhenOneShot_UsesFixedUtcRunAt()
    {
        var now = new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(now);
        var resolver = new FakeTimeZoneResolver(TimeZoneInfo.Utc);
        var runAt = now.AddMinutes(12);
        var source = new TestSchedulable
        {
            Schedule =
            {
                Enabled = true,
                Mode = ScheduleState.ModeOneShot,
                RunAt = Timestamp.FromDateTimeOffset(runAt),
            },
        };
        var scheduled = new List<ScheduledTimeout>();
        var persisted = new List<DateTimeOffset>();
        var runner = CreateRunner(source, clock, resolver, scheduled, persisted);

        await runner.ScheduleNextRunAsync(CancellationToken.None);

        resolver.RequestedTimezones.Should().BeEmpty();
        scheduled.Should().ContainSingle();
        scheduled[0].DueTime.Should().Be(TimeSpan.FromMinutes(12));
        persisted.Should().ContainSingle().Which.Should().Be(runAt);
    }

    [Fact]
    public async Task ScheduleNextRunAsync_WhenRetired_ShouldSkipScheduling()
    {
        var now = new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero);
        var source = new TestSchedulable
        {
            Schedule =
            {
                Enabled = true,
                Mode = ScheduleState.ModeOneShot,
                RunAt = Timestamp.FromDateTimeOffset(now.AddMinutes(12)),
                RetiredAt = Timestamp.FromDateTimeOffset(now.AddMinutes(13)),
            },
        };
        var scheduled = new List<ScheduledTimeout>();
        var persisted = new List<DateTimeOffset>();
        var runner = CreateRunner(
            source,
            new FakeClock(now),
            new FakeTimeZoneResolver(TimeZoneInfo.Utc),
            scheduled,
            persisted);

        await runner.ScheduleNextRunAsync(CancellationToken.None);

        scheduled.Should().BeEmpty();
        persisted.Should().BeEmpty();
    }

    private static ChannelScheduleRunner CreateRunner(
        TestSchedulable source,
        FakeClock clock,
        ITimeZoneResolver resolver,
        List<ScheduledTimeout> scheduled,
        List<DateTimeOffset> persisted) =>
        new(
            callbackId: "trigger",
            schedulableSource: () => source,
            triggerFactory: static () => new TriggerSkillRunnerExecutionCommand { Reason = "schedule" },
            persistNextRunEventAsync: next =>
            {
                persisted.Add(next);
                return Task.CompletedTask;
            },
            scheduleTimeoutAsync: (id, dueTime, evt, _) =>
            {
                scheduled.Add(new ScheduledTimeout(id, dueTime, evt));
                return Task.FromResult(new RuntimeCallbackLease(
                    "actor-1",
                    id,
                    scheduled.Count,
                    RuntimeCallbackBackend.InMemory));
            },
            cancelCallbackAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            timeZoneResolver: resolver,
            logger: NullLogger.Instance,
            ownerDescription: "test runner");

    private sealed class TestSchedulable : ISchedulable
    {
        public ScheduleState Schedule { get; } = new();
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public int ReadCount { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                ReadCount++;
                return utcNow;
            }
        }
    }

    private sealed class FakeTimeZoneResolver(TimeZoneInfo timeZone) : ITimeZoneResolver
    {
        public List<string?> RequestedTimezones { get; } = [];

        public bool TryResolve(string? timeZoneId, out TimeZoneInfo resolved, out string? error)
        {
            RequestedTimezones.Add(timeZoneId);
            resolved = timeZone;
            error = null;
            return true;
        }
    }

    private sealed record ScheduledTimeout(string CallbackId, TimeSpan DueTime, IMessage Event);
}
