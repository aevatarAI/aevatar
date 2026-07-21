using Aevatar.GAgents.ChatHistory;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ChatHistoryActorIdsTests
{
    [Fact]
    public void Conversation_ShouldEncodeCompositeTupleWithoutDelimiterCollision()
    {
        var left = ChatHistoryActorIds.Conversation("tenant", "admin-c1");
        var right = ChatHistoryActorIds.Conversation("tenant-admin", "c1");

        left.Should().NotBe(right);
        left.Should().NotBe("chat-tenant-admin-c1");
        right.Should().NotBe("chat-tenant-admin-c1");
    }
}
