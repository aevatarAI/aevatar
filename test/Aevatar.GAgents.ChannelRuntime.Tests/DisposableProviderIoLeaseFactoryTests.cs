using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class DisposableProviderIoLeaseFactoryTests
{
    [Fact]
    public void Acquire_ReturnsDisposableLease_ForSingleProviderCall()
    {
        var factory = new DisposableProviderIoLeaseFactory();

        using var lease = factory.Acquire("actor-1", "lark-card-create", "corr-1");

        lease.Should().NotBeNull();
    }

    [Fact]
    public void Acquire_RejectsMissingOperationIdentity()
    {
        var factory = new DisposableProviderIoLeaseFactory();

        var act = () => factory.Acquire("actor-1", string.Empty, "corr-1");

        act.Should().Throw<ArgumentException>();
    }
}
