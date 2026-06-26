using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxApiResponseHelperTests
{
    [Fact]
    public void ExtractOptionalProxyUrlSlug_Prefers_ProxyUrlSlug_Over_Slug()
    {
        // NyxID auto-numbers a taken base slug; the connect response carries the per-connection slug.
        NyxApiResponseHelper.ExtractOptionalProxyUrlSlug(
                """{"id":"svc-3","slug":"api-lark-bot-3","proxy_url_slug":"api-lark-bot-3"}""")
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
    [InlineData("""{"error":true,"status":409,"message":"taken"}""")]   // Nyx error envelope
    [InlineData("not-json")]                                            // unparseable
    [InlineData("")]                                                    // empty
    public void ExtractOptionalProxyUrlSlug_Returns_Null_When_No_Usable_Slug(string response)
    {
        NyxApiResponseHelper.ExtractOptionalProxyUrlSlug(response).Should().BeNull();
    }
}
