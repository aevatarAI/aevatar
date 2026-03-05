using Aevatar.App.Application.Concurrency;
using FluentAssertions;

namespace Aevatar.App.Application.Tests;

public sealed class ImageConcurrencyCoordinatorTests
{
    private static ImageConcurrencyCoordinator Create(
        int maxTotal = 2,
        int maxQueueSize = 0,
        int queueTimeoutMs = 50)
        => new(maxTotal, maxQueueSize, queueTimeoutMs);

    [Fact]
    public async Task TryAcquireGenerate_WithSlots_ReturnsAcquired()
    {
        var coordinator = Create();

        var result = await coordinator.TryAcquireGenerateAsync();

        result.Acquired.Should().BeTrue();
        result.Reason.Should().Be(AcquireFailureReason.None);
    }

    [Fact]
    public async Task TryAcquireGenerate_QueueFull_ReturnsQueueFull()
    {
        var coordinator = Create(maxTotal: 1, maxQueueSize: 0);

        await coordinator.TryAcquireGenerateAsync();
        var result = await coordinator.TryAcquireGenerateAsync();

        result.Acquired.Should().BeFalse();
        result.Reason.Should().Be(AcquireFailureReason.Overloaded);
    }

    [Fact]
    public async Task ReleaseGenerate_AfterAcquire_FreesSlot()
    {
        var coordinator = Create(maxTotal: 1, maxQueueSize: 0);

        await coordinator.TryAcquireGenerateAsync();
        await coordinator.ReleaseGenerateAsync();
        var result = await coordinator.TryAcquireGenerateAsync();

        result.Acquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireUpload_WithSlots_ReturnsAcquired()
    {
        var coordinator = Create();

        var result = await coordinator.TryAcquireUploadAsync();

        result.Acquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireUpload_QueueFull_ReturnsQueueFull()
    {
        var coordinator = Create(maxTotal: 1, maxQueueSize: 0);

        await coordinator.TryAcquireUploadAsync();
        var result = await coordinator.TryAcquireUploadAsync();

        result.Acquired.Should().BeFalse();
        result.Reason.Should().Be(AcquireFailureReason.Overloaded);
    }

    [Fact]
    public async Task ReleaseUpload_AfterAcquire_FreesSlot()
    {
        var coordinator = Create(maxTotal: 1, maxQueueSize: 0);

        await coordinator.TryAcquireUploadAsync();
        await coordinator.ReleaseUploadAsync();
        var result = await coordinator.TryAcquireUploadAsync();

        result.Acquired.Should().BeTrue();
    }

    [Fact]
    public async Task GetStats_ReturnsCurrentState()
    {
        var coordinator = Create(maxTotal: 5);

        await coordinator.TryAcquireGenerateAsync();
        await coordinator.TryAcquireUploadAsync();

        var stats = await coordinator.GetStatsAsync();

        stats.ActiveGenerates.Should().Be(1);
        stats.ActiveUploads.Should().Be(1);
        stats.AvailableSlots.Should().Be(3);
        stats.MaxTotal.Should().Be(5);
    }

    [Fact]
    public void AcquireAttemptResult_FactoryMethods_SetFieldsCorrectly()
    {
        var acquired = AcquireAttemptResult.AcquiredResult();
        acquired.Acquired.Should().BeTrue();
        acquired.Reason.Should().Be(AcquireFailureReason.None);

        var queueFull = AcquireAttemptResult.QueueFullResult("full");
        queueFull.Acquired.Should().BeFalse();
        queueFull.Reason.Should().Be(AcquireFailureReason.Overloaded);
        queueFull.Message.Should().Be("full");

        var timeout = AcquireAttemptResult.TimeoutResult("timed out");
        timeout.Acquired.Should().BeFalse();
        timeout.Reason.Should().Be(AcquireFailureReason.Overloaded);

        var rateLimited = AcquireAttemptResult.RateLimitedResult("slow");
        rateLimited.Acquired.Should().BeFalse();
        rateLimited.Reason.Should().Be(AcquireFailureReason.RateLimit);
    }
}
