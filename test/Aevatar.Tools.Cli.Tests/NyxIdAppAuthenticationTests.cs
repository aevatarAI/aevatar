using System.Net.Http;
using System.Net.Sockets;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class NyxIdAppAuthenticationTests
{
    [Fact]
    public void TryBuildNyxIdLoginFailureMessage_ShouldExplainNetworkFailures()
    {
        var exception = new InvalidOperationException(
            "IDX20803: Unable to obtain configuration.",
            new HttpRequestException(
                "nodename nor servname provided, or not known (nyx-api.chrono-ai.fun:443)",
                new SocketException()));

        var success = NyxIdAppAuthentication.TryBuildNyxIdLoginFailureMessage(
            exception,
            new NyxIdAppAuthOptions { Authority = "https://nyx-api.chrono-ai.fun" },
            out var message);

        success.Should().BeTrue();
        message.Should().Contain("could not reach NyxID");
        message.Should().Contain("https://nyx-api.chrono-ai.fun");
        message.Should().Contain("nyx-api.chrono-ai.fun");
    }

    [Fact]
    public void TryBuildNyxIdLoginFailureMessage_ShouldIgnoreUnrelatedFailures()
    {
        var success = NyxIdAppAuthentication.TryBuildNyxIdLoginFailureMessage(
            new InvalidOperationException("some other failure"),
            new NyxIdAppAuthOptions(),
            out var message);

        success.Should().BeFalse();
        message.Should().BeEmpty();
    }
}
