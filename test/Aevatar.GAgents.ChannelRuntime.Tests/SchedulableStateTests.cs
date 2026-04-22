using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SchedulableStateTests
{
    [Fact]
    public void SkillRunnerState_ExposesScheduleStateThroughISchedulable()
    {
        var state = new SkillRunnerState
        {
            Enabled = true,
            ScheduleCron = "0 9 * * *",
            ScheduleTimezone = "Asia/Singapore",
            NextRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 20, 1, 0, 0, TimeSpan.Zero)),
            LastRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 19, 1, 0, 0, TimeSpan.Zero)),
            ErrorCount = 2,
        };

        var schedule = ((ISchedulable)state).Schedule;

        schedule.Enabled.Should().BeTrue();
        schedule.Cron.Should().Be("0 9 * * *");
        schedule.Timezone.Should().Be("Asia/Singapore");
        schedule.NextRunAt.Should().Be(state.NextRunAt);
        schedule.LastRunAt.Should().Be(state.LastRunAt);
        schedule.ErrorCount.Should().Be(2);
    }

    [Fact]
    public void WorkflowAgentState_ExposesScheduleStateThroughISchedulable()
    {
        var state = new WorkflowAgentState
        {
            Enabled = false,
            ScheduleCron = "0 * * * *",
            ScheduleTimezone = "UTC",
            ErrorCount = 0,
        };

        var schedule = ((ISchedulable)state).Schedule;

        schedule.Enabled.Should().BeFalse();
        schedule.Cron.Should().Be("0 * * * *");
        schedule.Timezone.Should().Be("UTC");
        schedule.NextRunAt.Should().BeNull();
        schedule.LastRunAt.Should().BeNull();
        schedule.ErrorCount.Should().Be(0);
    }
}
