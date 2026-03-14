using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Core;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceInvocationApplicationServiceTests
{
    [Fact]
    public async Task InvokeAsync_ShouldResolveTarget_AndDispatch()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"));
        await artifactStore.SaveAsync("tenant:app:default:svc", "r1", artifact);

        var resolutionService = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(
                new ServiceCatalogSnapshot(
                    "tenant:app:default:svc",
                    "tenant",
                    "app",
                    "default",
                    "svc",
                    "Service",
                    "r1",
                    string.Empty,
                    "dep-1",
                    "actor-1",
                    "Active",
                    [new ServiceEndpointSnapshot("chat", "chat", "Command", "type.googleapis.com/test.command", string.Empty, string.Empty)],
                    DateTimeOffset.UtcNow)),
            artifactStore);
        var dispatcher = new RecordingDispatcher();
        var service = new ServiceInvocationApplicationService(resolutionService, dispatcher);

        var receipt = await service.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
        });

        dispatcher.Calls.Should().ContainSingle();
        dispatcher.Calls[0].target.Endpoint.EndpointId.Should().Be("chat");
        dispatcher.Calls[0].request.CommandId.Should().Be("cmd-1");
        receipt.TargetActorId.Should().Be("actor-1");
        receipt.EndpointId.Should().Be("chat");
    }

    private sealed class RecordingCatalogQueryReader : IServiceCatalogQueryReader
    {
        private readonly ServiceCatalogSnapshot? _snapshot;

        public RecordingCatalogQueryReader(ServiceCatalogSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_snapshot);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(_snapshot == null ? [] : [_snapshot]);
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
