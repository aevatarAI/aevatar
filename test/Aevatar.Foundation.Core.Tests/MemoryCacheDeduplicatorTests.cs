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
}
