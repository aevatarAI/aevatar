using Aevatar.AI.ToolProviders.Web;
using FluentAssertions;

namespace Aevatar.Hosting.Tests;

public sealed class WebFetchUrlGuardTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_ShouldReject_EmptyOrWhitespace(string? candidate)
    {
        var result = WebFetchUrlGuard.Validate(candidate);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("empty_url");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("://missing-scheme")]
    [InlineData("just-text-no-scheme")]
    public void Validate_ShouldReject_NonAbsoluteOrUnparseable(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("invalid_url");
    }

    [Fact]
    public void Validate_ShouldReject_NonHttpScheme()
    {
        var result = WebFetchUrlGuard.Validate("file:///etc/passwd");

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("unsupported_scheme");
    }

    [Fact]
    public void Validate_ShouldReject_FtpScheme()
    {
        var result = WebFetchUrlGuard.Validate("ftp://example.com/file");

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("unsupported_scheme");
    }

    [Theory]
    [InlineData("http://localhost/api")]
    [InlineData("http://LOCALHOST/api")]
    [InlineData("http://ip6-localhost/api")]
    [InlineData("https://app.localhost/api")]
    public void Validate_ShouldReject_LoopbackHostnames(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_loopback_hostname");
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.5.5.5:8080/path")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://10.255.255.255/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.254/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/")]  // AWS instance metadata
    [InlineData("http://0.0.0.0/")]
    public void Validate_ShouldReject_PrivateIpv4Addresses(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_private_address");
    }

    [Theory]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    public void Validate_ShouldReject_PrivateIpv6Addresses(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_private_address");
    }

    [Fact]
    public void Validate_ShouldReject_Ipv4MappedIpv6_PrivateAddress()
    {
        // 127.0.0.1 mapped: ::ffff:127.0.0.1
        var result = WebFetchUrlGuard.Validate("http://[::ffff:7f00:1]/");

        result.IsAllowed.Should().BeFalse();
        result.RejectionCode.Should().Be("blocked_private_address");
    }

    [Theory]
    [InlineData("http://172.15.0.1/")]   // just outside 172.16/12
    [InlineData("http://172.32.0.1/")]   // just outside 172.16/12
    [InlineData("http://11.0.0.1/")]     // just outside 10/8
    public void Validate_ShouldAccept_AdjacentNonPrivateRanges(string url)
    {
        var result = WebFetchUrlGuard.Validate(url);

        result.IsAllowed.Should().BeTrue();
        result.RejectionCode.Should().BeNull();
        result.NormalizedUrl.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("https://example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("  https://example.com  ", "https://example.com/")]
    public void Validate_ShouldAccept_PublicHosts_NormalizingTrim(string input, string expected)
    {
        var result = WebFetchUrlGuard.Validate(input);

        result.IsAllowed.Should().BeTrue();
        result.NormalizedUrl.Should().Be(expected);
        result.RejectionCode.Should().BeNull();
    }

    [Fact]
    public void Validate_ShouldAccept_PublicIpv4()
    {
        var result = WebFetchUrlGuard.Validate("http://8.8.8.8/");

        result.IsAllowed.Should().BeTrue();
    }
}
