using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Tests;

/// <summary>
/// Pins the shared relay callback-URL security policy: absolute https is accepted, http only for
/// loopback hosts; cleartext public hosts, non-http(s) schemes, and non-absolute URLs are rejected
/// so a registered callback can never ship the relay user token in the clear.
/// </summary>
public class NyxRelayCallbackUrlPolicyTests
{
    [Theory]
    [InlineData("https://relay.example.com", true)]
    [InlineData("https://relay.example.com/api/webhooks/nyxid-relay", true)]
    [InlineData("http://localhost/cb", true)]
    [InlineData("http://127.0.0.1/cb", true)]
    [InlineData("http://[::1]/cb", true)]
    [InlineData("http://aevatar.example.com", false)]
    [InlineData("http://192.168.1.10/cb", false)]
    [InlineData("ftp://relay.example.com", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSecureUrl_MatchesSchemeAndLoopbackPolicy(string? url, bool expected) =>
        NyxRelayCallbackUrlPolicy.IsSecureUrl(url).Should().Be(expected);
}
