using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class MemoryCacheDeduplicatorTests
{
    [Fact]
    public async Task ForgetAsync_ShouldAllowSameAttemptToBeReservedAgain()
    {
        var deduplicator = new MemoryCacheDeduplicator();

        (await deduplicator.TryRecordAsync("evt-failed")).Should().BeTrue();
        await deduplicator.ForgetAsync("evt-failed");

        (await deduplicator.TryRecordAsync("evt-failed")).Should().BeTrue();
    }

    [Fact]
    public async Task TryRecordAsync_WhenCalledConcurrently_ShouldGrantExactlyOneReservation()
    {
        var deduplicator = new MemoryCacheDeduplicator();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await deduplicator.TryRecordAsync("evt-concurrent");
            }))
            .ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Should().ContainSingle(result => result);
    }
}
