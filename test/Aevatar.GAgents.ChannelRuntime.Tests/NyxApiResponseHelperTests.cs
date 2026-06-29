using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxApiResponseHelperTests
{
    [Fact]
    public void ExtractOptionalProxyUrlSlug_Prefers_Slug_Over_ProxyUrlSlug_Template()
    {
        // NyxID auto-numbers a taken base slug; the connect response carries the per-connection slug.
        NyxApiResponseHelper.ExtractOptionalProxyUrlSlug(
                """{"id":"svc-3","slug":"api-lark-bot-3","proxy_url_slug":"https://nyx.example.com/api/v1/proxy/s/wrong/{path}"}""")
            .Should().Be("api-lark-bot-3");
    }

    [Theory]
    [InlineData("""{"proxy_url_slug":"https://nyx.example.com/api/v1/proxy/s/api-lark-bot-3/{path}"}""")]
    [InlineData("""{"proxy_url_slug":"/api/v1/proxy/s/api-lark-bot-3/{path}"}""")]
    [InlineData("""{"proxy_url_slug":"api-lark-bot-3"}""")]
    public void ExtractOptionalProxyUrlSlug_Normalizes_ProxyUrlSlug_To_Bare_Slug(string response)
    {
        NyxApiResponseHelper.ExtractOptionalProxyUrlSlug(response)
            .Should().Be("api-lark-bot-3");
    }

    [Fact]
    public void ExtractOptionalProxyUrlSlug_Falls_Back_To_Slug_When_ProxyUrlSlug_Absent()
    {
        NyxApiResponseHelper.ExtractOptionalProxyUrlSlug("""{"id":"svc-2","slug":"api-lark-bot-2"}""")
            .Should().Be("api-lark-bot-2");
    }

    [Theory]
    [InlineData("""{"id":"svc-1"}""")]                                  // neither field present
    [InlineData("""{"proxy_url_slug":"   "}""")]                        // whitespace-only
    [InlineData("""{"proxy_url_slug":"https://nyx.example.com/proxy/by-id/svc-1/{path}"}""")]
    [InlineData("""{"error":true,"status":409,"message":"taken"}""")]   // Nyx error envelope
    [InlineData("not-json")]                                            // unparseable
    [InlineData("")]                                                    // empty
    public void ExtractOptionalProxyUrlSlug_Returns_Null_When_No_Usable_Slug(string response)
    {
        NyxApiResponseHelper.ExtractOptionalProxyUrlSlug(response).Should().BeNull();
    }

    [Fact]
    public void ExtractChannelBotIdsForApp_Returns_Ids_Matching_App_By_PlatformBotId_Or_AppId()
    {
        var response = """[{"id":"b1","platform_bot_id":"cli_x"},{"id":"b2","platform_bot_id":"cli_y"},{"id":"b3","app_id":"cli_x"}]""";
        NyxApiResponseHelper.ExtractChannelBotIdsForApp(response, "cli_x")
            .Should().BeEquivalentTo(new[] { "b1", "b3" });
    }

    [Fact]
    public void ExtractChannelBotIdsForApp_Reads_Wrapped_Array_And_Ignores_NonMatches()
    {
        NyxApiResponseHelper.ExtractChannelBotIdsForApp("""{"data":[{"id":"b1","platform_bot_id":"cli_y"}]}""", "cli_x")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"error":true,"status":409}""")]   // Nyx error envelope
    [InlineData("not-json")]                          // unparseable
    [InlineData("[]")]                                // empty list
    [InlineData("")]                                  // empty
    public void ExtractChannelBotIdsForApp_Returns_Empty_On_Error_Or_NoMatch(string response)
    {
        NyxApiResponseHelper.ExtractChannelBotIdsForApp(response, "cli_x").Should().BeEmpty();
    }
}
