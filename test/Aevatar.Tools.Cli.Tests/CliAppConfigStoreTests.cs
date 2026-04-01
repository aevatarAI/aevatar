using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class CliAppConfigStoreTests
{
    // ─── TryNormalizeApiBaseUrl ───

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeApiBaseUrl_EmptyUrl_ShouldReturnFalse(string? rawUrl)
    {
        var result = CliAppConfigStore.TryNormalizeApiBaseUrl(rawUrl, out _, out var error);

        result.Should().BeFalse();
        error.Should().Contain("empty");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("just-text")]
    public void TryNormalizeApiBaseUrl_RelativeUrl_ShouldReturnFalse(string rawUrl)
    {
        var result = CliAppConfigStore.TryNormalizeApiBaseUrl(rawUrl, out _, out var error);

        result.Should().BeFalse();
        error.Should().Contain("absolute");
    }

    [Fact]
    public void TryNormalizeApiBaseUrl_FileScheme_ShouldReturnFalse()
    {
        var result = CliAppConfigStore.TryNormalizeApiBaseUrl("/relative/path", out _, out var error);

        result.Should().BeFalse();
        error.Should().Contain("http/https");
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("ws://example.com")]
    [InlineData("file:///path")]
    public void TryNormalizeApiBaseUrl_NonHttpScheme_ShouldReturnFalse(string rawUrl)
    {
        var result = CliAppConfigStore.TryNormalizeApiBaseUrl(rawUrl, out _, out var error);

        result.Should().BeFalse();
        error.Should().Contain("http/https");
    }

    [Fact]
    public void TryNormalizeApiBaseUrl_UrlWithQueryString_ShouldReturnFalse()
    {
        var result = CliAppConfigStore.TryNormalizeApiBaseUrl(
            "https://example.com?foo=bar", out _, out var error);

        result.Should().BeFalse();
        error.Should().Contain("Query string");
    }

    [Fact]
    public void TryNormalizeApiBaseUrl_UrlWithFragment_ShouldReturnFalse()
    {
        var result = CliAppConfigStore.TryNormalizeApiBaseUrl(
            "https://example.com#section", out _, out var error);

        result.Should().BeFalse();
        error.Should().Contain("fragment");
    }

    [Theory]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1")]
    [InlineData("  https://example.com/  ", "https://example.com")]
    public void TryNormalizeApiBaseUrl_ValidUrl_ShouldNormalize(string rawUrl, string expected)
    {
        var result = CliAppConfigStore.TryNormalizeApiBaseUrl(rawUrl, out var normalized, out _);

        result.Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Fact]
    public void TryNormalizeApiBaseUrl_HttpScheme_ShouldSucceed()
    {
        var result = CliAppConfigStore.TryNormalizeApiBaseUrl(
            "http://localhost:5000", out var normalized, out _);

        result.Should().BeTrue();
        normalized.Should().Be("http://localhost:5000");
    }

    // ─── ResolveApiBaseUrl ───

    [Fact]
    public void ResolveApiBaseUrl_InvalidFallback_ShouldThrow()
    {
        var act = () => CliAppConfigStore.ResolveApiBaseUrl(
            overrideApiBaseUrl: null,
            localFallbackUrl: "not-valid",
            out _);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("localFallbackUrl");
    }

    [Fact]
    public void ResolveApiBaseUrl_InvalidOverride_ShouldThrow()
    {
        var act = () => CliAppConfigStore.ResolveApiBaseUrl(
            overrideApiBaseUrl: "ftp://bad",
            localFallbackUrl: "http://localhost:5000",
            out _);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("overrideApiBaseUrl");
    }

    [Fact]
    public void ResolveApiBaseUrl_WithValidOverride_ShouldReturnOverride()
    {
        var result = CliAppConfigStore.ResolveApiBaseUrl(
            overrideApiBaseUrl: "https://override.example.com/",
            localFallbackUrl: "http://localhost:5000",
            out _);

        result.Should().Be("https://override.example.com");
    }
}
