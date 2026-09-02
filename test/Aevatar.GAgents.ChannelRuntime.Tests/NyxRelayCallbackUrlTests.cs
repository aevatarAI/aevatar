using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxRelayCallbackUrlTests
{
    [Theory]
    [InlineData("https://aevatar.example.com", true)]      // https public host
    [InlineData("https://aevatar.example.com/", true)]
    [InlineData("http://localhost", true)]                 // loopback dev exemption
    [InlineData("http://localhost:5051", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]", true)]
    [InlineData("http://aevatar.example.com", false)]      // cleartext public host -> rejected
    [InlineData("ftp://aevatar.example.com", false)]       // non-http(s) scheme
    [InlineData("aevatar.example.com", false)]             // not an absolute URL
    [InlineData("/api/webhooks/nyxid-relay", false)]       // relative path
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSecureBaseUrl_allows_https_and_loopback_http_only(string? webhookBaseUrl, bool expected)
    {
        NyxRelayCallbackUrl.IsSecureBaseUrl(webhookBaseUrl).Should().Be(expected);
    }

    [Fact]
    public void Build_appends_relay_callback_path_and_trims_trailing_slash()
    {
        NyxRelayCallbackUrl.Build("https://aevatar.example.com/")
            .Should().Be("https://aevatar.example.com/api/webhooks/nyxid-relay");
    }
}
