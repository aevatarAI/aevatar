using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.Collections;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentRunHistoryTrimTests
{
    [Theory]
    [MemberData(nameof(TrimCases))]
    public void TrimMessagesToRecentFloor_ShouldKeepAllSystemMessagesAndMostRecentNonSystemMessages(
        string scenario,
        string[] encodedMessages,
        int keepRecent,
        int expectedDropped,
        string[] expectedContents)
    {
        var messages = new RepeatedField<AgentRunChatMessage>();
        messages.AddRange(encodedMessages.Select(DecodeMessage));

        var dropped = AgentRunGAgent.TrimMessagesToRecentFloor(messages, keepRecent);

        dropped.Should().Be(expectedDropped, scenario);
        messages.Select(message => message.Content).Should().Equal(expectedContents, scenario);
    }

    public static IEnumerable<object[]> TrimCases()
    {
        yield return
        [
            "below floor is a no-op",
            new[] { "system:sys", "user:u1", "assistant:a1" },
            3,
            0,
            new[] { "sys", "u1", "a1" },
        ];
        yield return
        [
            "exact floor is a no-op",
            new[] { "system:sys", "user:u1", "assistant:a1" },
            2,
            0,
            new[] { "sys", "u1", "a1" },
        ];
        yield return
        [
            "drops oldest non-system messages only",
            new[] { "system:sys", "user:u1", "assistant:a1", "user:u2", "assistant:a2" },
            2,
            2,
            new[] { "sys", "u2", "a2" },
        ];
        yield return
        [
            "interleaved system messages keep their relative order",
            new[] { "system:sys-1", "user:u1", "system:sys-2", "assistant:a1", "user:u2", "system:sys-3", "assistant:a2" },
            2,
            2,
            new[] { "sys-1", "sys-2", "u2", "sys-3", "a2" },
        ];
    }

    private static AgentRunChatMessage DecodeMessage(string encoded)
    {
        var separator = encoded.IndexOf(':', StringComparison.Ordinal);
        separator.Should().BeGreaterThan(0);
        return new AgentRunChatMessage
        {
            Role = encoded[..separator],
            Content = encoded[(separator + 1)..],
        };
    }
}
