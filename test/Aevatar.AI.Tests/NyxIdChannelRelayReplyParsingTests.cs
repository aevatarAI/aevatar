using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChannelRelayReplyParsingTests
{
    [Fact]
    public async Task SendChannelRelayTextReplyAsync_ShouldParseUpstreamMessageIdAsPlatformMessageId()
    {
        var client = CreateClient("""{"message_id":"reply-1","upstream_message_id":12345}""");

        var result = await client.SendChannelRelayTextReplyAsync(
            "token",
            "message-1",
            "hello",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.MessageId.Should().Be("reply-1");
        result.PlatformMessageId.Should().Be("12345");
    }

    private static NyxIdApiClient CreateClient(string responseBody)
    {
        var handler = new ResponseHandler(responseBody);
        return new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);
    }

    private sealed class ResponseHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
    }
}
