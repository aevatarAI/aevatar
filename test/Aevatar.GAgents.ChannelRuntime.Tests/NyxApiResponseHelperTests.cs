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
    public void ExtractLarkChannelBotIds_Returns_Only_Lark_Bot_Ids_From_Real_List_Shape()
    {
        // The REAL `GET /api/v1/channel-bots` list item carries `id` + `platform` but NOT
        // `platform_bot_id`/`app_id` (those live only on the per-bot detail). Cleanup must select by
        // platform here, then disambiguate the app via each bot's detail. A list-time match on
        // `platform_bot_id` (the prior fix) always returned empty against this shape.
        var response = """
        {"bots":[
          {"id":"0b19376b","platform":"lark","label":"Aevatar Lark Bot","platform_bot_username":"lark_bot","webhook_registered":true,"status":"active","is_active":true},
          {"id":"39a20d2b","platform":"telegram","label":"Aevatar Telegram Bot","platform_bot_username":"x_bot","status":"active"},
          {"id":"133ddba3","platform":"lark","label":"Lark Bot (cli)","platform_bot_username":"lark_bot","status":"active"}
        ],"total":3}
        """;
        NyxApiResponseHelper.ExtractLarkChannelBotIds(response)
            .Should().BeEquivalentTo(new[] { "0b19376b", "133ddba3" });
    }

    [Theory]
    [InlineData("""{"error":true,"status":409}""")]                                                      // Nyx error envelope
    [InlineData("not-json")]                                                                              // unparseable
    [InlineData("""{"bots":[],"total":0}""")]                                                             // empty list
    [InlineData("""{"bots":[{"id":"t1","platform":"telegram"}],"total":1}""")]                            // no lark bot
    [InlineData("")]                                                                                      // empty
    public void ExtractLarkChannelBotIds_Returns_Empty_On_Error_Or_NoLarkBot(string response)
    {
        NyxApiResponseHelper.ExtractLarkChannelBotIds(response).Should().BeEmpty();
    }

    [Fact]
    public void ChannelBotDetailMatchesApp_Matches_When_Detail_PlatformBotId_Equals_App()
    {
        // The REAL `GET /api/v1/channel-bots/{id}` detail DOES carry `platform_bot_id` (the Lark app id).
        var detail = """
        {"id":"0b19376b","platform":"lark","label":"Aevatar Lark Bot","platform_bot_id":"cli_aab147d27238deed","platform_bot_username":"lark_bot","status":"active"}
        """;
        NyxApiResponseHelper.ChannelBotDetailMatchesApp(detail, "cli_aab147d27238deed").Should().BeTrue();
        NyxApiResponseHelper.ChannelBotDetailMatchesApp(detail, " cli_aab147d27238deed ").Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"id":"b","platform":"lark","platform_bot_id":"cli_other"}""", "cli_x")]   // different Lark app
    [InlineData("""{"id":"b","platform":"telegram","platform_bot_id":"cli_x"}""", "cli_x")]   // not a Lark bot
    [InlineData("""{"id":"b","platform":"lark"}""", "cli_x")]                                  // detail missing platform_bot_id
    [InlineData("""{"error":true,"status":404}""", "cli_x")]                                   // Nyx error envelope
    [InlineData("not-json", "cli_x")]                                                          // unparseable
    [InlineData("""{"id":"b","platform":"lark","platform_bot_id":"cli_x"}""", "")]             // empty app id
    public void ChannelBotDetailMatchesApp_Returns_False_When_Not_This_Lark_App(string detail, string appId)
    {
        NyxApiResponseHelper.ChannelBotDetailMatchesApp(detail, appId).Should().BeFalse();
    }
}
