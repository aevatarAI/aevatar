using FluentAssertions;
using Aevatar.GAgents.NyxidChat;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdConnectedServiceInventoryIntentTests
{
    [Theory]
    [InlineData("我在 nyxid 上有什么服务")]
    [InlineData("我在 NyxID 上连接了哪些服务？")]
    [InlineData("我能用的 nyxId service 有哪些")]
    [InlineData("请列出我的 NyxID 服务列表")]
    [InlineData("Show my connected NyxID services")]
    public void Matches_WhenCallerAsksForOwnConnectedServiceInventory(string text)
    {
        NyxIdConnectedServiceInventoryIntent.Matches(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("NyxID 支持哪些服务")]
    [InlineData("NyxID 的服务目录")]
    [InlineData("怎么连接 NyxID 服务")]
    [InlineData("帮我连接 GitHub 服务到 NyxID")]
    [InlineData("删除我的 NyxID GitHub 服务")]
    [InlineData("我有什么服务")]
    [InlineData("NyxID 是什么")]
    public void Matches_WhenRequestIsCatalogMutationOrUnscoped_DoesNotClaimIntent(string text)
    {
        NyxIdConnectedServiceInventoryIntent.Matches(text).Should().BeFalse();
    }
}
