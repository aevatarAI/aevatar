using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Hosting.Migration;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ServiceGovernanceLegacyMigrationHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldImportEachCatalogService_AndHandleImportedAndSkippedPaths()
    {
        var services = new[]
        {
            new ServiceCatalogSnapshot("tenant-a/app-a/default/service-a", "tenant-a", "app-a", "default", "service-a", "Service A", "r1", "r1", "dep-a", "actor-a", "active", [], [], DateTimeOffset.UtcNow),
            new ServiceCatalogSnapshot("tenant-a/app-a/default/service-b", "tenant-a", "app-a", "default", "service-b", "Service B", "r1", "r1", "dep-b", "actor-b", "active", [], [], DateTimeOffset.UtcNow),
        };
        var reader = new StubServiceCatalogQueryReader(services);
        var importer = new RecordingLegacyImporter(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["tenant-a/app-a/default/service-a"] = true,
            ["tenant-a/app-a/default/service-b"] = false,
        });
        var hostedService = new ServiceGovernanceLegacyMigrationHostedService(
            reader,
            importer,
            NullLogger<ServiceGovernanceLegacyMigrationHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);

        importer.Requests.Should().HaveCount(2);
        importer.Requests.Select(x => x.ServiceId).Should().BeEquivalentTo(["service-a", "service-b"]);
    }

    [Fact]
    public async Task StopAsync_ShouldComplete()
    {
        var hostedService = new ServiceGovernanceLegacyMigrationHostedService(
            new StubServiceCatalogQueryReader([]),
            new RecordingLegacyImporter(new Dictionary<string, bool>(StringComparer.Ordinal)),
            NullLogger<ServiceGovernanceLegacyMigrationHostedService>.Instance);

        await hostedService.Invoking(x => x.StopAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    private sealed class StubServiceCatalogQueryReader : IServiceCatalogQueryReader
    {
        private readonly IReadOnlyList<ServiceCatalogSnapshot> _services;

        public StubServiceCatalogQueryReader(IReadOnlyList<ServiceCatalogSnapshot> services)
        {
            _services = services;
        }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            var key = $"{identity.TenantId}/{identity.AppId}/{identity.Namespace}/{identity.ServiceId}";
            return Task.FromResult(_services.FirstOrDefault(x => string.Equals(x.ServiceKey, key, StringComparison.Ordinal)));
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default)
        {
            IReadOnlyList<ServiceCatalogSnapshot> filtered = _services
                .Where(x =>
                    string.Equals(x.TenantId, tenantId, StringComparison.Ordinal) &&
                    string.Equals(x.AppId, appId, StringComparison.Ordinal) &&
                    string.Equals(x.Namespace, @namespace, StringComparison.Ordinal))
                .Take(take)
                .ToList();
            return Task.FromResult(filtered);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default)
        {
            IReadOnlyList<ServiceCatalogSnapshot> filtered = _services.Take(take).ToList();
            return Task.FromResult(filtered);
        }
    }

    private sealed class RecordingLegacyImporter : IServiceGovernanceLegacyImporter
    {
        private readonly IReadOnlyDictionary<string, bool> _results;

        public RecordingLegacyImporter(IReadOnlyDictionary<string, bool> results)
        {
            _results = results;
        }

        public List<ServiceIdentity> Requests { get; } = [];

        public Task<bool> ImportIfNeededAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Requests.Add(identity.Clone());
            var key = $"{identity.TenantId}/{identity.AppId}/{identity.Namespace}/{identity.ServiceId}";
            return Task.FromResult(_results.TryGetValue(key, out var imported) && imported);
        }
    }
}
