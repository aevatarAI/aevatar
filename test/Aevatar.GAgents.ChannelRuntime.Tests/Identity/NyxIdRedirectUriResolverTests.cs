using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins the redirect-URI resolver against the aismart-app-mainnet
/// 2026-04-30 incident: a Kestrel wildcard listen address
/// (<c>http://+:8080</c>) propagated into the OAuth callback URL and
/// every /init's authorize URL was unreachable. Resolver now hardcodes
/// the production default and only accepts <c>AEVATAR_OAUTH_REDIRECT_BASE_URL</c>
/// as override; wildcard hosts are filtered.
/// </summary>
public sealed class NyxIdRedirectUriResolverTests : IDisposable
{
    private readonly string? _savedOverride;

    public NyxIdRedirectUriResolverTests()
    {
        _savedOverride = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar);
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, _savedOverride);
    }

    [Fact]
    public void DefaultsToProductionPublicBaseUrl_WhenOverrideUnset()
    {
        var url = NyxIdRedirectUriResolver.Resolve();

        url.Should().Be(
            $"{NyxIdRedirectUriResolver.DefaultPublicBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}");
    }

    [Fact]
    public void HonorsOverride_WhenSet()
    {
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, "https://staging.example.com");

        var url = NyxIdRedirectUriResolver.Resolve();

        url.Should().Be("https://staging.example.com" + NyxIdRedirectUriResolver.CallbackPath);
    }

    [Fact]
    public void TrimsTrailingSlashOnOverride()
    {
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, "https://staging.example.com/");

        var url = NyxIdRedirectUriResolver.Resolve();

        url.Should().Be("https://staging.example.com" + NyxIdRedirectUriResolver.CallbackPath);
    }

    [Theory]
    [InlineData("http://+:8080")]                     // Kestrel any-IPv4 + IPv6
    [InlineData("http://*:8080")]                     // wildcard alias
    [InlineData("http://0.0.0.0:8080")]               // IPv4 unspecified
    [InlineData("http://[::]:8080")]                  // IPv6 unspecified
    public void RejectsWildcardListenAddress_FallsBackToDefault(string wildcardOverride)
    {
        // Pin the aismart-app-mainnet 2026-04-30 incident: ASPNETCORE_URLS-
        // shaped values must not propagate into a registered redirect URI
        // even if some operator misconfigures the override env var with one.
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, wildcardOverride);

        var url = NyxIdRedirectUriResolver.Resolve(NullLogger.Instance);

        url.Should().Be(
            $"{NyxIdRedirectUriResolver.DefaultPublicBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}");
    }

    [Fact]
    public void IgnoresEmptyOverride()
    {
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, "   ");

        var url = NyxIdRedirectUriResolver.Resolve();

        url.Should().Be(
            $"{NyxIdRedirectUriResolver.DefaultPublicBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}");
    }

    [Fact]
    public void DoesNotReadAspnetcoreUrls()
    {
        // Even when ASPNETCORE_URLS contains a wildcard listen address (the
        // typical K8s shape), the resolver MUST NOT use it — the listen
        // address is an internal-only Kestrel binding, not an OAuth
        // callback target. Earlier shapes of this resolver had a priority
        // chain that read ASPNETCORE_URLS as a fallback; the prod incident
        // proved that path is never safe.
        var savedAspnet = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://+:8080");

            var url = NyxIdRedirectUriResolver.Resolve();

            url.Should().Be(
                $"{NyxIdRedirectUriResolver.DefaultPublicBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", savedAspnet);
        }
    }
}
