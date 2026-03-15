using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Application.Services;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceGovernanceQueryApplicationServiceTests
{
    [Fact]
    public async Task QueryService_ShouldProjectConfigurationIntoGovernanceViews()
    {
        var identity = new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "svc",
        };
        var now = DateTimeOffset.UtcNow;
        var configuration = new ServiceConfigurationSnapshot(
            "tenant:app:ns:svc",
            identity.Clone(),
            [
                new ServiceBindingSnapshot(
                    "binding-a",
                    "Binding A",
                    ServiceBindingKind.Service,
                    ["policy-a"],
                    false,
                    new BoundServiceReferenceSnapshot(
                        new ServiceIdentity
                        {
                            TenantId = "tenant",
                            AppId = "app",
                            Namespace = "ns",
                            ServiceId = "dep",
                        },
                        "run"),
                    null,
                    null),
            ],
            [
                new ServiceEndpointExposureSnapshot(
                    "invoke",
                    "Invoke",
                    ServiceEndpointKind.Command,
                    "type.googleapis.com/demo.Invoke",
                    string.Empty,
                    "invoke",
                    ServiceEndpointExposureKind.Public,
                    ["policy-a"]),
            ],
            [
                new ServicePolicySnapshot(
                    "policy-a",
                    "Policy A",
                    ["binding-a"],
                    ["tenant/app/ns/caller"],
                    true,
                    false),
            ],
            now);
        var service = new ServiceGovernanceQueryApplicationService(new RecordingConfigurationQueryReader(configuration));

        var bindings = await service.GetBindingsAsync(identity);
        var endpoints = await service.GetEndpointCatalogAsync(identity);
        var policies = await service.GetPoliciesAsync(identity);

        bindings.Should().NotBeNull();
        bindings!.Bindings.Should().ContainSingle(x => x.BindingId == "binding-a" && x.ServiceRef != null);
        endpoints.Should().NotBeNull();
        endpoints!.Endpoints.Should().ContainSingle(x => x.EndpointId == "invoke" && x.ExposureKind == ServiceEndpointExposureKind.Public);
        policies.Should().NotBeNull();
        policies!.Policies.Should().ContainSingle(x => x.PolicyId == "policy-a" && x.InvokeRequiresActiveDeployment);
    }

    private sealed class RecordingConfigurationQueryReader : IServiceConfigurationQueryReader
    {
        private readonly ServiceConfigurationSnapshot _snapshot;

        public RecordingConfigurationQueryReader(ServiceConfigurationSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<ServiceConfigurationSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceConfigurationSnapshot?>(_snapshot);
    }
}
