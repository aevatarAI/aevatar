using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ScopeServiceIdentityQueryTests
{
    [Fact]
    public void BuildQueryString_ShouldMapScopeToPinnedScopeServiceIdentity()
    {
        var query = ScopeServiceIdentityQuery.BuildQueryString(" scope-a ", ("take", "20"));

        query.Should().Be("tenantId=scope-a&appId=default&namespace=default&take=20");
    }

    [Fact]
    public void BuildQueryString_ShouldSkipScopeIdentity_WhenScopeIsMissing()
    {
        var query = ScopeServiceIdentityQuery.BuildQueryString(" ", ("take", "20"));

        query.Should().Be("take=20");
    }
}
