using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkBotIdentityResolverTests
{
    [Fact]
    public void TryParseBotOpenId_ShouldExtractOpenId_FromBotInfoResponse()
    {
        // Lark GET /open-apis/bot/v3/info returns the bot's own open_id at the root under "bot"
        // on a successful (HTTP 200, code 0) proxy response.
        var response = """
            {"code":0,"msg":"success","bot":{"activate_status":2,"app_name":"Aevatar","open_id":"ou_bot_self_1"}}
            """;

        LarkBotIdentityResolver.TryParseBotOpenId(response, out var openId).Should().BeTrue();
        openId.Should().Be("ou_bot_self_1");
    }

    [Fact]
    public void TryParseBotOpenId_ShouldExtractOpenId_WhenNestedUnderData()
    {
        var response = """
            {"code":0,"data":{"bot":{"open_id":"ou_bot_self_2"}}}
            """;

        LarkBotIdentityResolver.TryParseBotOpenId(response, out var openId).Should().BeTrue();
        openId.Should().Be("ou_bot_self_2");
    }

    [Theory]
    [InlineData("""{"code":99991663,"msg":"invalid access token"}""")]
    [InlineData("""{"error":true,"status":400,"body":"{\"code\":230002}"}""")]
    [InlineData("""{"code":0,"bot":{"app_name":"Aevatar"}}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void TryParseBotOpenId_ShouldReturnFalse_ForErrorOrMissingBotOpenId(string response)
    {
        LarkBotIdentityResolver.TryParseBotOpenId(response, out var openId).Should().BeFalse();
        openId.Should().BeEmpty();
    }
}
