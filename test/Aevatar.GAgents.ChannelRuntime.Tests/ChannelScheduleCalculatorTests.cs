using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelScheduleCalculatorTests
{
    [Fact]
    public void TryGetNextOccurrence_ReturnsNextUtcOccurrence_ForUtcSchedule()
    {
        var fromUtc = new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero);

        var ok = ChannelScheduleCalculator.TryGetNextOccurrence(
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

        var ok = ChannelScheduleCalculator.TryGetNextOccurrence(
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
        var ok = ChannelScheduleCalculator.TryResolveTimeZone(
            "Mars/Olympus",
            out var timeZone,
            out var error);

        ok.Should().BeFalse();
        timeZone.Should().Be(TimeZoneInfo.Utc);
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ComputeDueTime_ReturnsFutureDelta_WhenNextRunInFuture()
    {
        var now = new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero);
        var next = now.AddMinutes(5);

        var due = ChannelScheduleCalculator.ComputeDueTime(next, now);

        due.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ComputeDueTime_ReturnsOneSecond_WhenNextRunAlreadyElapsed()
    {
        var now = new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero);
        var past = now.AddMinutes(-1);

        var due = ChannelScheduleCalculator.ComputeDueTime(past, now);

        due.Should().Be(TimeSpan.FromSeconds(1));
    }
}
