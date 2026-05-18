using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddOrnnSkills_WhenCalledTwice_ShouldRemainIdempotentAndReuseConcreteToolSource()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new NotFoundHttpMessageHandler())));

        services.AddOrnnSkills();
        services.AddOrnnSkills();

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();

        sources.Count(x => x is OrnnAgentToolSource).Should().Be(1);
        sources.OfType<OrnnAgentToolSource>().Should().ContainSingle()
            .Which.Should().BeSameAs(provider.GetRequiredService<OrnnAgentToolSource>());
    }

    private sealed class NotFoundHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
