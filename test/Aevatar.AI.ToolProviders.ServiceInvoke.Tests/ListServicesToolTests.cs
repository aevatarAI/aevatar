using System.Text.Json;
using Aevatar.AI.ToolProviders.ServiceInvoke.Tools;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Tests;

public class ListServicesToolTests
{
    private static ServiceInvokeOptions DefaultOptions() => new()
    {
        TenantId = "t1",
        AppId = "a1",
        Namespace = "ns1",
    };

    [Fact]
    public async Task ExecuteAsync_ReturnsServicesFromCatalog()
    {
        var reader = new StubCatalogReader(
        [
            new ServiceCatalogSnapshot(
                "t1:a1:ns1:svc1", "t1", "a1", "ns1", "svc1", "My Service",
                "rev1", "rev1", "dep1", "actor1", "ACTIVE",
                [new ServiceEndpointSnapshot("cmd1", "Submit", "COMMAND", "type.googleapis.com/test.Submit", "", "Submit stuff")],
                [], DateTimeOffset.UtcNow),
        ]);

        var tool = new ListServicesTool(reader, DefaultOptions());
        var result = await tool.ExecuteAsync("{}");

        using var doc = JsonDocument.Parse(result);
        var services = doc.RootElement.GetProperty("services");
        services.GetArrayLength().Should().Be(1);
        services[0].GetProperty("service_id").GetString().Should().Be("svc1");
        services[0].GetProperty("endpoints").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersByServiceId()
    {
        var reader = new StubCatalogReader(
        [
            new ServiceCatalogSnapshot("t1:a1:ns1:svc1", "t1", "a1", "ns1", "svc1", "Svc1", "", "", "", "", "ACTIVE", [], [], DateTimeOffset.UtcNow),
            new ServiceCatalogSnapshot("t1:a1:ns1:svc2", "t1", "a1", "ns1", "svc2", "Svc2", "", "", "", "", "ACTIVE", [], [], DateTimeOffset.UtcNow),
        ]);

        var tool = new ListServicesTool(reader, DefaultOptions());
        var result = await tool.ExecuteAsync("""{"service_id":"svc2"}""");

        using var doc = JsonDocument.Parse(result);
        var services = doc.RootElement.GetProperty("services");
        services.GetArrayLength().Should().Be(1);
        services[0].GetProperty("service_id").GetString().Should().Be("svc2");
    }

    [Fact]
    public async Task ExecuteAsync_FiltersByText()
    {
        var reader = new StubCatalogReader(
        [
            new ServiceCatalogSnapshot("k", "t1", "a1", "ns1", "alpha", "Alpha Service", "", "", "", "", "ACTIVE", [], [], DateTimeOffset.UtcNow),
            new ServiceCatalogSnapshot("k", "t1", "a1", "ns1", "beta", "Beta Service", "", "", "", "", "ACTIVE",
                [new ServiceEndpointSnapshot("ep1", "Search", "COMMAND", "", "", "Full text search endpoint")],
                [], DateTimeOffset.UtcNow),
        ]);

        var tool = new ListServicesTool(reader, DefaultOptions());
        var result = await tool.ExecuteAsync("""{"filter":"search"}""");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("services").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEmptyWhenNoMatch()
    {
        var reader = new StubCatalogReader([]);
        var tool = new ListServicesTool(reader, DefaultOptions());
        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("No services found");
    }

    private sealed class StubCatalogReader(IReadOnlyList<ServiceCatalogSnapshot> snapshots) : IServiceCatalogQueryReader
    {
        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(snapshots.FirstOrDefault(s =>
                s.TenantId == identity.TenantId &&
                s.AppId == identity.AppId &&
                s.Namespace == identity.Namespace &&
                s.ServiceId == identity.ServiceId));

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(snapshots.Take(take).ToList());

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(snapshots
                .Where(s => s.TenantId == tenantId && s.AppId == appId && s.Namespace == @namespace)
                .Take(take).ToList());
    }
}
