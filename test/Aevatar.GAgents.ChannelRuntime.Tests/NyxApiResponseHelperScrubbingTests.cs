using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

// Security hardening (§3.2): NyxApiResponseHelper.ExtractErrorDetail surfaces the upstream
// (NyxID -> platform) error body into the registration failure string, which is logged. A
// misbehaving upstream that echoes a submitted secret back in its error body must not be able
// to leak it into our logs, so the body/message fields are run through SecretScrubber.
public sealed class NyxApiResponseHelperScrubbingTests
{
    [Fact]
    public void ExtractErrorDetail_ShouldScrubJwtInBody_BeforeItReachesLogs()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJsZWFrIn0.QwErTyUiOpAsDfGhJkLzXcVbNm1234567890QwErTy";
        var envelope = $"{{\"error\":true,\"status\":401,\"body\":\"upstream rejected token {jwt}\"}}";

        var detail = NyxApiResponseHelper.ExtractErrorDetail(envelope);

        detail.Should().Contain("nyx_status=401");
        detail.Should().NotContain(jwt);
        detail.Should().Contain(SecretScrubber.Marker);
    }

    [Fact]
    public void ExtractErrorDetail_ShouldScrubSecretJsonValueInMessage()
    {
        var envelope = "{\"error\":true,\"status\":403,\"message\":\"{\\\"client_secret\\\":\\\"cs-supersecretvalue0011223344\\\"}\"}";

        var detail = NyxApiResponseHelper.ExtractErrorDetail(envelope);

        detail.Should().Contain("nyx_status=403");
        detail.Should().NotContain("cs-supersecretvalue0011223344");
        detail.Should().Contain(SecretScrubber.Marker);
    }

    [Fact]
    public void ExtractErrorDetail_ShouldPreserveNonSecretBody()
    {
        var envelope = "{\"error\":true,\"status\":404,\"body\":\"channel bot not found\"}";

        var detail = NyxApiResponseHelper.ExtractErrorDetail(envelope);

        detail.Should().Be("nyx_status=404 body=channel bot not found");
        detail.Should().NotContain(SecretScrubber.Marker);
    }
}
