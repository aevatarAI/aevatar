using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class ToolProviderHttpClientRegistrationTests
{
    [Fact]
    public void AddNyxIdTools_RegistersProductionHttpClientsThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        services.ShouldContainTypedHttpClient<NyxIdApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<NyxIdApiClient>().Should().NotBeNull();
        provider.GetRequiredService<NyxIdServiceInstanceClient>().Should().NotBeNull();
        provider.GetRequiredService<INyxIdApiClientFactory>()
            .CreateClient()
            .Should()
            .NotBeNull();
        provider.GetRequiredService<IRemoteToolApprovalPort>().Should()
            .BeOfType<NyxIdRemoteToolApprovalPort>();
    }

    [Fact]
    public void AddNyxIdTools_ShouldRegisterFileArtifactIngressOnlyWhenWorkflowIngressExists()
    {
        var withoutWorkflowIngress = new ServiceCollection();
        withoutWorkflowIngress.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        withoutWorkflowIngress.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(INyxIdProxyFileArtifactIngress));

        var withWorkflowIngress = new ServiceCollection();
        withWorkflowIngress.AddSingleton<IFileArtifactIngressPort, StubWorkflowFileIngressPort>();
        withWorkflowIngress.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        withWorkflowIngress.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdProxyFileArtifactIngress) &&
            descriptor.ImplementationFactory != null);
        using var provider = withWorkflowIngress.BuildServiceProvider();
        provider.GetRequiredService<INyxIdProxyFileArtifactIngress>()
            .Should()
            .BeOfType<NyxIdProxyWorkflowFileArtifactIngress>();
    }

    [Fact]
    public async Task AddNyxIdTools_ResolvesToolSourceWithoutDeletedCatalogServices()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        services.Any(HttpClientRegistrationAssertions.IsDeletedNyxIdDiscoveryRegistration)
            .Should()
            .BeFalse("AddNyxIdTools must not expose deleted catalog/cache services");

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IAgentToolSource>()
            .Should()
            .ContainSingle(toolSource => toolSource is NyxIdAgentToolSource)
            .Which;

        var tools = await source.DiscoverToolsAsync();
        var names = tools.Select(tool => tool.Name).ToList();

        names.Should().Contain("nyxid_proxy");
        names.Should().NotContain("nyxid_search_capabilities");
        names.Should().NotContain("nyxid_proxy_execute");
        tools.Should().ContainSingle(tool => tool is NyxIdProxyTool);
    }

    [Fact]
    public async Task AddNyxIdTools_WithSshOptIn_DiscoversToolsThatAlwaysRequireApproval()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.EnableSshExecTool = true;
        });

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IAgentToolSource>().OfType<NyxIdAgentToolSource>().Single();

        var tools = await source.DiscoverToolsAsync();
        var sshExec = tools.Should().ContainSingle(tool => tool is NyxIdSshExecTool).Subject;
        var codexExec = tools.Should().ContainSingle(tool => tool is NyxIdCodexExecTool).Subject;
        codexExec.Name.Should().Be("codex_exec");
        sshExec.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        sshExec.IsDestructive.Should().BeTrue();
        codexExec.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        codexExec.IsDestructive.Should().BeTrue();
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

    public static bool IsDeletedNyxIdDiscoveryRegistration(ServiceDescriptor descriptor)
    {
        var serviceName = descriptor.ServiceType.Name;
        var implementationName = descriptor.ImplementationType?.Name;

        return serviceName is "NyxIdSpecCatalog" or "IServiceDiscoveryCache" ||
               implementationName is "NyxIdSpecCatalog" or "InMemoryServiceDiscoveryCache";
    }

}

file sealed class StubWorkflowFileIngressPort : IFileArtifactIngressPort
{
    public ValueTask<FileArtifactIngressResult> IngestAsync(
        FileArtifactIngressRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new FileArtifactIngressResult(new FileArtifactRef
        {
            FileId = "file-1",
            ArtifactId = "artifact-1",
            SourceKind = request.SourceKind,
        }));
}
