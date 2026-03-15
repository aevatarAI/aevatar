using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceInvocationApplicationServiceTests
{
    [Fact]
    public async Task InvokeAsync_ShouldResolveAuthorizeAndDispatch()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"));
        await artifactStore.SaveAsync(ServiceKeys.Build(identity), "r1", artifact);

        var resolutionService = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = new ServiceCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    identity.TenantId,
                    identity.AppId,
                    identity.Namespace,
                    identity.ServiceId,
                    "Orders",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    ServiceDeploymentStatus.Unspecified.ToString(),
                    [],
                    ["service-policy"],
                    DateTimeOffset.UtcNow),
            },
            new RecordingTrafficViewQueryReader
            {
                GetResult = new ServiceTrafficViewSnapshot(
                    ServiceKeys.Build(identity),
                    1,
                    string.Empty,
                    [
                        new ServiceTrafficEndpointSnapshot(
                            "chat",
                            [
                                new ServiceTrafficTargetSnapshot(
                                    "dep-1",
                                    "r1",
                                    "actor-1",
                                    100,
                                    ServiceServingState.Active.ToString()),
                            ]),
                    ],
                    DateTimeOffset.UtcNow),
            },
            artifactStore);
        var authorizer = new RecordingAuthorizer();
        var dispatcher = new RecordingDispatcher();
        var service = new ServiceInvocationApplicationService(resolutionService, authorizer, dispatcher);

        var receipt = await service.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
        });

        authorizer.Calls.Should().ContainSingle();
        authorizer.Calls[0].serviceKey.Should().Be(ServiceKeys.Build(identity));
        authorizer.Calls[0].deploymentId.Should().Be("dep-1");
        dispatcher.Calls.Should().ContainSingle();
        dispatcher.Calls[0].target.Endpoint.EndpointId.Should().Be("chat");
        dispatcher.Calls[0].request.CommandId.Should().Be("cmd-1");
        receipt.TargetActorId.Should().Be("actor-1");
        receipt.EndpointId.Should().Be("chat");
    }

    private sealed class RecordingCatalogQueryReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class RecordingTrafficViewQueryReader : IServiceTrafficViewQueryReader
    {
        public ServiceTrafficViewSnapshot? GetResult { get; init; }

        public Task<ServiceTrafficViewSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }

    private sealed class RecordingAuthorizer : IInvokeAdmissionAuthorizer
    {
        public List<(string serviceKey, string deploymentId, PreparedServiceRevisionArtifact artifact, ServiceEndpointDescriptor endpoint, ServiceInvocationRequest request)> Calls { get; } = [];

        public Task AuthorizeAsync(
            string serviceKey,
            string deploymentId,
            PreparedServiceRevisionArtifact artifact,
            ServiceEndpointDescriptor endpoint,
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            Calls.Add((serviceKey, deploymentId, artifact.Clone(), endpoint.Clone(), request.Clone()));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDispatcher : IServiceInvocationDispatcher
    {
        public List<(ServiceInvocationResolvedTarget target, ServiceInvocationRequest request)> Calls { get; } = [];

        public Task<ServiceInvocationAcceptedReceipt> DispatchAsync(
            ServiceInvocationResolvedTarget target,
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            Calls.Add((target, request.Clone()));
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                RequestId = "req-1",
                ServiceKey = target.Service.ServiceKey,
                DeploymentId = target.Service.DeploymentId,
                TargetActorId = target.Service.PrimaryActorId,
                EndpointId = target.Endpoint.EndpointId,
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
            });
        }
    }
}
