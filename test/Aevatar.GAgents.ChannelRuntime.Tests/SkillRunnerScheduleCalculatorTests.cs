using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerScheduleCalculatorTests
{
    [Fact]
    public void TryGetNextOccurrence_ReturnsNextUtcOccurrence_ForUtcSchedule()
    {
        var fromUtc = new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero);

        var ok = SkillRunnerScheduleCalculator.TryGetNextOccurrence(
            "30 9 * * *",
            "UTC",
            fromUtc,
            out var nextRunAtUtc,
            out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        nextRunAtUtc.Should().Be(new DateTimeOffset(2026, 4, 14, 9, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryGetNextOccurrence_RespectsTimezoneOffset()
    {
        var fromUtc = new DateTimeOffset(2026, 4, 14, 0, 0, 0, TimeSpan.Zero);

        var ok = SkillRunnerScheduleCalculator.TryGetNextOccurrence(
            "0 9 * * *",
            "Asia/Singapore",
            fromUtc,
            out var nextRunAtUtc,
            out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        nextRunAtUtc.Should().Be(new DateTimeOffset(2026, 4, 14, 1, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryResolveTimeZone_ReturnsFalse_ForUnknownTimezone()
    {
        var ok = SkillRunnerScheduleCalculator.TryResolveTimeZone(
            "Mars/Olympus",
            out var timeZone,
            out var error);

        ok.Should().BeFalse();
        timeZone.Should().Be(TimeZoneInfo.Utc);
        error.Should().NotBeNullOrWhiteSpace();
    }
}
