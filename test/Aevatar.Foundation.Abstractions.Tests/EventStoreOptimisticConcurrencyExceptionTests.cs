using Aevatar.Foundation.Abstractions.Persistence;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public class EventStoreOptimisticConcurrencyExceptionTests
{
    [Fact]
    public void Constructor_ShouldCapturePropertiesAndFormatMessage()
    {
        var exception = new EventStoreOptimisticConcurrencyException("agent-1", expectedVersion: 4, actualVersion: 7);

        exception.AgentId.ShouldBe("agent-1");
        exception.ExpectedVersion.ShouldBe(4);
        exception.ActualVersion.ShouldBe(7);
        exception.Message.ShouldBe("Optimistic concurrency conflict: expected 4, actual 7");
        exception.ShouldBeAssignableTo<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_ShouldDefaultAgentIdToEmpty_WhenNullProvided()
    {
        var exception = new EventStoreOptimisticConcurrencyException(agentId: null!, expectedVersion: 1, actualVersion: 2);

        exception.AgentId.ShouldBe(string.Empty);
        exception.ExpectedVersion.ShouldBe(1);
        exception.ActualVersion.ShouldBe(2);
    }
}
