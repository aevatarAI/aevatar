using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Application.Services;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceGovernanceQueryApplicationServiceTests
{
    [Fact]
    public async Task QueryService_ShouldDelegateToGovernanceReaders()
    {
        var identity = new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "svc",
        };
        var now = DateTimeOffset.UtcNow;
        var bindingSnapshot = new ServiceBindingCatalogSnapshot("tenant:app:ns:svc", [], now);
        var endpointSnapshot = new ServiceEndpointCatalogSnapshot("tenant:app:ns:svc", [], now);
        var policySnapshot = new ServicePolicyCatalogSnapshot("tenant:app:ns:svc", [], now);
        var service = new ServiceGovernanceQueryApplicationService(
            new RecordingBindingQueryReader(bindingSnapshot),
            new RecordingEndpointCatalogQueryReader(endpointSnapshot),
            new RecordingPolicyQueryReader(policySnapshot));

        var bindings = await service.GetBindingsAsync(identity);
        var endpoints = await service.GetEndpointCatalogAsync(identity);
        var policies = await service.GetPoliciesAsync(identity);

        bindings.Should().BeSameAs(bindingSnapshot);
        endpoints.Should().BeSameAs(endpointSnapshot);
        policies.Should().BeSameAs(policySnapshot);
    }

    private sealed class RecordingBindingQueryReader : IServiceBindingQueryReader
    {
        private readonly ServiceBindingCatalogSnapshot _snapshot;

        public RecordingBindingQueryReader(ServiceBindingCatalogSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<ServiceBindingCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceBindingCatalogSnapshot?>(_snapshot);
    }

    private sealed class RecordingEndpointCatalogQueryReader : IServiceEndpointCatalogQueryReader
    {
        private readonly ServiceEndpointCatalogSnapshot _snapshot;

        public RecordingEndpointCatalogQueryReader(ServiceEndpointCatalogSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<ServiceEndpointCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceEndpointCatalogSnapshot?>(_snapshot);
    }

    private sealed class RecordingPolicyQueryReader : IServicePolicyQueryReader
    {
        private readonly ServicePolicyCatalogSnapshot _snapshot;

        public RecordingPolicyQueryReader(ServicePolicyCatalogSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<ServicePolicyCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServicePolicyCatalogSnapshot?>(_snapshot);
    }
}
