using Aevatar.AI.ToolProviders.ServiceInvoke.Tools;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Tests;

public class ServiceInvokeAgentToolSourceTests
{
    [Fact]
    public async Task DiscoverToolsAsync_ReturnsEmptyWhenTenantNotConfigured()
    {
        var source = new ServiceInvokeAgentToolSource(
            new ServiceInvokeOptions(),
            new NullCatalogReader(),
            new NullInvocationPort());

        var tools = await source.DiscoverToolsAsync();
        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_ReturnsEmptyWhenOnlyTenantConfigured()
    {
        var source = new ServiceInvokeAgentToolSource(
            new ServiceInvokeOptions { TenantId = "t1" },
            new NullCatalogReader(),
            new NullInvocationPort());

        var tools = await source.DiscoverToolsAsync();
        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_ReturnsEmptyWhenNamespaceMissing()
    {
        var source = new ServiceInvokeAgentToolSource(
            new ServiceInvokeOptions { TenantId = "t1", AppId = "a1" },
            new NullCatalogReader(),
            new NullInvocationPort());

        var tools = await source.DiscoverToolsAsync();
        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverToolsAsync_ReturnsBothToolsWhenFullyConfigured()
    {
        var source = new ServiceInvokeAgentToolSource(
            new ServiceInvokeOptions { TenantId = "t1", AppId = "a1", Namespace = "ns1" },
            new NullCatalogReader(),
            new NullInvocationPort());

        var tools = await source.DiscoverToolsAsync();
        tools.Should().HaveCount(2);
        tools.Should().Contain(t => t is ListServicesTool);
        tools.Should().Contain(t => t is InvokeServiceTool);
    }

    [Fact]
    public async Task DiscoverToolsAsync_ReturnsOnlyListWhenInvokeDisabled()
    {
        var source = new ServiceInvokeAgentToolSource(
            new ServiceInvokeOptions { TenantId = "t1", AppId = "a1", Namespace = "ns1", EnableInvoke = false },
            new NullCatalogReader(),
            new NullInvocationPort());

        var tools = await source.DiscoverToolsAsync();
        tools.Should().HaveCount(1);
        tools.Should().Contain(t => t is ListServicesTool);
    }

    private sealed class NullCatalogReader : IServiceCatalogQueryReader
    {
        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceCatalogSnapshot?>(null);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class NullInvocationPort : IServiceInvocationPort
    {
        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
