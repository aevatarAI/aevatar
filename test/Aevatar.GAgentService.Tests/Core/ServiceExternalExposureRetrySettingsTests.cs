using Aevatar.GAgentService.Core.Models;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceExternalExposureRetrySettingsTests
{
    [Fact]
    public void Create_ShouldRejectInvalidValues()
    {
        var zeroAttempts = () => ServiceExternalExposureRetrySettings.Create(
            0,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        var zeroBaseDelay = () => ServiceExternalExposureRetrySettings.Create(
            1,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));
        var maxDelayBeforeBaseDelay = () => ServiceExternalExposureRetrySettings.Create(
            1,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1));

        zeroAttempts.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxAttempts");
        zeroBaseDelay.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("baseDelay");
        maxDelayBeforeBaseDelay.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxDelay");
    }

    [Fact]
    public void ComputeDelay_ShouldGrowExponentiallyAndClampToMaxDelay()
    {
        var settings = ServiceExternalExposureRetrySettings.Create(
            5,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10));

        settings.ComputeDelay(0).Should().Be(TimeSpan.FromSeconds(2));
        settings.ComputeDelay(1).Should().Be(TimeSpan.FromSeconds(2));
        settings.ComputeDelay(2).Should().Be(TimeSpan.FromSeconds(4));
        settings.ComputeDelay(3).Should().Be(TimeSpan.FromSeconds(8));
        settings.ComputeDelay(4).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ComputeDelay_ShouldClampLargeAttemptsWithoutOverflow()
    {
        var settings = ServiceExternalExposureRetrySettings.Create(
            5,
            TimeSpan.FromDays(1000),
            TimeSpan.FromDays(2000));

        settings.ComputeDelay(int.MaxValue).Should().Be(TimeSpan.FromDays(2000));
    }
}
