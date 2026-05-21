using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Web;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Tests;

public sealed class ToolProviderHttpClientRegistrationTests
{
    [Fact]
    public void AddNyxIdTools_RegistersProductionHttpClientsThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        services.ShouldContainTypedHttpClient<NyxIdApiClient>();
        services.ShouldContainNamedHttpClient(NyxIdSpecCatalog.HttpClientName);
        services.ShouldContainNamedHttpClient(ConnectedServiceSpecCache.HttpClientName);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<NyxIdApiClient>().Should().NotBeNull();
        provider.GetRequiredService<IRemoteToolApprovalPort>().Should()
            .BeOfType<NyxIdRemoteToolApprovalPort>();
        provider.GetServices<IToolApprovalHandler>().Should().BeEmpty();
    }

    [Fact]
    public void AddWebTools_RegistersWebApiClientThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddWebTools();

        services.ShouldContainTypedHttpClient<WebApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<WebApiClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddChronoStorageTools_RegistersChronoStorageApiClientThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddChronoStorageTools(options => options.ApiBaseUrl = "https://storage.test");

        services.ShouldContainTypedHttpClient<ChronoStorageApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<ChronoStorageApiClient>().Should().NotBeNull();
    }
}

file static class HttpClientRegistrationAssertions
{
    public static void ShouldContainTypedHttpClient<TClient>(
        this IServiceCollection services)
        where TClient : class
    {
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(TClient) &&
            descriptor.Lifetime == ServiceLifetime.Transient);
    }

    public static void ShouldContainNamedHttpClient(
        this IServiceCollection services,
        string name)
    {
        services.ShouldContainHttpClientOptions(name);
    }

    private static void ShouldContainHttpClientOptions(
        this IServiceCollection services,
        string name)
    {
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IConfigureOptions<HttpClientFactoryOptions>) &&
            descriptor.ImplementationInstance is ConfigureNamedOptions<HttpClientFactoryOptions> options &&
            options.Name == name)
            .Should()
            .BeTrue("AddHttpClient should register HttpClientFactoryOptions for '{0}'", name);
    }
}
