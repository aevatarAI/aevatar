using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class NyxIdTokenStoreTests
{
    [Fact]
    public void ResolveAuthority_WhenNoConfigured_ShouldReturnDefaultAuthority()
    {
        // The default authority is used when no config is set.
        // This test verifies the method returns a non-empty HTTPS URL.
        var authority = NyxIdTokenStore.ResolveAuthority();

        authority.Should().NotBeNullOrWhiteSpace();
        authority.Should().StartWith("https://");
        authority.Should().NotEndWith("/");
    }
}
