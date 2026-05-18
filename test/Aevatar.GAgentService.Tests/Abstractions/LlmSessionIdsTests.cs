using Aevatar.GAgentService.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class LlmSessionIdsTests
{
    [Theory]
    [InlineData("resp_1", "response-sessions/response:resp_1")]
    [InlineData(" resp_1 ", "response-sessions/response:resp_1")]
    [InlineData("resp/with/slash", "response-sessions/response:resp%2Fwith%2Fslash")]
    [InlineData("resp:with:colon", "response-sessions/response:resp%3Awith%3Acolon")]
    [InlineData("resp emoji 😀", "response-sessions/response:resp%20emoji%20%F0%9F%98%80")]
    public void BuildActorId_ShouldReturnReadableDeterministicActorId(string responseId, string expected)
    {
        LlmSessionIds.BuildActorId(responseId).Should().Be(expected);
    }

    [Fact]
    public void BuildActorId_ShouldCapLongIds_WithDeterministicHashTail()
    {
        var responseId = new string('r', 700);

        var first = LlmSessionIds.BuildActorId(responseId);
        var second = LlmSessionIds.BuildActorId(responseId);

        first.Length.Should().Be(LlmSessionIds.MaxActorIdLength);
        second.Should().Be(first);
        first.Should().MatchRegex("response-sessions/response:r+~[0-9a-f]{16}");
    }

    [Fact]
    public void BuildActorId_ShouldRejectBlankResponseId()
    {
        var act = () => LlmSessionIds.BuildActorId(" ");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == "responseId");
    }
}
