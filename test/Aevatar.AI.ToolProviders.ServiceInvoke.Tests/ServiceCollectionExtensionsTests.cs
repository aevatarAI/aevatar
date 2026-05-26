using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddServiceInvokeTools_WhenCalledTwice_ShouldRemainIdempotent()
    {
        var services = new ServiceCollection();
        RegisterStubDependencies(services);

        services.AddServiceInvokeTools(o => { o.TenantId = "t1"; o.AppId = "a1"; o.Namespace = "ns1"; });
        services.AddServiceInvokeTools(o => { o.TenantId = "t1"; o.AppId = "a1"; o.Namespace = "ns1"; });

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();

        sources.Count(x => x is ServiceInvokeAgentToolSource).Should().Be(1);
    }

    [Fact]
    public async Task AddServiceInvokeTools_RegistersToolSource()
    {
        var services = new ServiceCollection();
        RegisterStubDependencies(services);

        services.AddServiceInvokeTools(o => { o.TenantId = "t1"; o.AppId = "a1"; o.Namespace = "ns1"; });

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();

        sources.Should().ContainSingle(x => x is ServiceInvokeAgentToolSource);
    }

    [Fact]
    public async Task AddServiceInvokeTools_WorksWithoutArtifactStore()
    {
        var services = new ServiceCollection();
        // Only register required dependencies, NOT IServiceRevisionCatalogQueryReader.
        services.AddSingleton<IServiceCatalogQueryReader, StubCatalogReader>();
        services.AddSingleton<IServiceInvocationPort, StubInvocationPort>();

        services.AddServiceInvokeTools(o => { o.TenantId = "t1"; o.AppId = "a1"; o.Namespace = "ns1"; });

        await using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>().ToList();
        sources.Should().ContainSingle(x => x is ServiceInvokeAgentToolSource);

        var source = sources.OfType<ServiceInvokeAgentToolSource>().Single();
        var tools = await source.DiscoverToolsAsync();
        tools.Should().HaveCount(2);
    }

    private static void RegisterStubDependencies(ServiceCollection services)
    {
        services.AddSingleton<IServiceCatalogQueryReader, StubCatalogReader>();
        services.AddSingleton<IServiceInvocationPort, StubInvocationPort>();
        services.AddSingleton<IServiceRevisionCatalogQueryReader, StubRevisionCatalogReader>();
    }

    private sealed class StubCatalogReader : IServiceCatalogQueryReader
    {
        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceCatalogSnapshot?>(null);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class StubInvocationPort : IServiceInvocationPort
    {
        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class StubRevisionCatalogReader : IServiceRevisionCatalogQueryReader
    {
        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);
    }
}
