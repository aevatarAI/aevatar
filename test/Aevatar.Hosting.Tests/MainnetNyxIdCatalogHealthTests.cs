using System.Net;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Hosting;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetNyxIdCatalogHealthTests
{
    [Fact]
    public async Task NyxIdCatalogHealthContributor_WhenSpecFetchTokenMissing_ShouldBeUnhealthy()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        using var catalog = new NyxIdSpecCatalog(options, new HttpClient(new FakeHttpHandler()));
        using var provider = new ServiceCollection()
            .AddSingleton(catalog)
            .BuildServiceProvider();
        var contributor = MainnetHostBuilderExtensions.CreateNyxIdCatalogHealthContributor();

        var result = await contributor.ProbeAsync!(provider, CancellationToken.None);

        result.Status.Should().Be(AevatarHealthStatuses.Unhealthy);
        result.Message.Should().Contain("spec fetch token is missing");
        result.Details.Should().Contain("baseUrlConfigured", "true");
        result.Details.Should().Contain("specFetchTokenConfigured", "false");
        result.Details.Should().Contain("operationCount", "0");
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
