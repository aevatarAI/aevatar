using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.RunIdResolvers;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Host.Api.Tests;

public class AIChatMessageRunIdResolverTests
{
    [Fact]
    public void TryResolve_ForMessageEvents_ShouldExtractRunId()
    {
        var resolver = new AIChatMessageRunIdResolver();
        const string expectedRunId = "run-42";
        const string messageId = "run-42:step-a";

        resolver.TryResolve(Wrap(new TextMessageStartEvent { MessageId = messageId }), out var fromStart)
            .Should().BeTrue();
        fromStart.Should().Be(expectedRunId);

        resolver.TryResolve(Wrap(new TextMessageContentEvent { MessageId = messageId, Delta = "x" }), out var fromContent)
            .Should().BeTrue();
        fromContent.Should().Be(expectedRunId);

        resolver.TryResolve(Wrap(new TextMessageEndEvent { MessageId = messageId, Content = "done" }), out var fromEnd)
            .Should().BeTrue();
        fromEnd.Should().Be(expectedRunId);

        resolver.TryResolve(Wrap(new ChatResponseEvent { MessageId = messageId, Content = "done" }), out var fromResponse)
            .Should().BeTrue();
        fromResponse.Should().Be(expectedRunId);
    }

    [Fact]
    public void TryResolve_WithInvalidMessageId_ShouldReturnFalse()
    {
        var resolver = new AIChatMessageRunIdResolver();
        var envelope = Wrap(new TextMessageStartEvent { MessageId = "invalid-id" });

        resolver.TryResolve(envelope, out var runId).Should().BeFalse();
        runId.Should().BeNull();
    }

    private static EventEnvelope Wrap<T>(T evt) where T : class, Google.Protobuf.IMessage<T> =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "test",
            Direction = EventDirection.Down,
        };
}
