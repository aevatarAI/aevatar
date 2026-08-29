using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
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
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"));
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);

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
            new RecordingInvocationCatalogQueryReader
            {
                GetResult = CreateInvocationCatalogSnapshot(identity, Ready(identity, "chat", "r1", "dep-1", "actor-1")),
            },
            revisionCatalog,
            ServingSetReader(identity, "chat", "r1", "dep-1", "actor-1"));
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

    [Fact]
    public async Task InvokeAsync_ShouldGenerateIds_WhenBothCommandAndCorrelationAreMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"));
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);

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
                    [],
                    DateTimeOffset.UtcNow),
            },
            new RecordingInvocationCatalogQueryReader
            {
                GetResult = CreateInvocationCatalogSnapshot(identity, Ready(identity, "chat", "r1", "dep-1", "actor-1")),
            },
            revisionCatalog,
            ServingSetReader(identity, "chat", "r1", "dep-1", "actor-1"));
        var authorizer = new RecordingAuthorizer();
        var dispatcher = new RecordingDispatcher();
        var service = new ServiceInvocationApplicationService(resolutionService, authorizer, dispatcher);

        var receipt = await service.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        authorizer.Calls.Should().ContainSingle();
        authorizer.Calls[0].request.CommandId.Should().NotBeNullOrWhiteSpace();
        authorizer.Calls[0].request.CorrelationId.Should().Be(authorizer.Calls[0].request.CommandId);
        dispatcher.Calls.Should().ContainSingle();
        dispatcher.Calls[0].request.CommandId.Should().NotBeNullOrWhiteSpace();
        dispatcher.Calls[0].request.CorrelationId.Should().Be(dispatcher.Calls[0].request.CommandId);
        receipt.CommandId.Should().Be(dispatcher.Calls[0].request.CommandId);
        receipt.CorrelationId.Should().Be(dispatcher.Calls[0].request.CorrelationId);
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenServiceNotFound()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        var resolutionService = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader { GetResult = null },
            new RecordingInvocationCatalogQueryReader { GetResult = null },
            revisionCatalog,
            new RecordingServingSetQueryReader());
        var service = new ServiceInvocationApplicationService(
            resolutionService, new RecordingAuthorizer(), new RecordingDispatcher());

        var act = () => service.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found*");
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenNoInvocationCatalogReadModel()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
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
                    [],
                    DateTimeOffset.UtcNow),
            },
            new RecordingInvocationCatalogQueryReader { GetResult = null },
            revisionCatalog,
            new RecordingServingSetQueryReader());
        var service = new ServiceInvocationApplicationService(
            resolutionService, new RecordingAuthorizer(), new RecordingDispatcher());

        var act = () => service.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no invocation catalog readmodel*");
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenEndpointNotInInvocationCatalog()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
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
                    [],
                    DateTimeOffset.UtcNow),
            },
            new RecordingInvocationCatalogQueryReader
            {
                GetResult = CreateInvocationCatalogSnapshot(identity, Ready(identity, "other-endpoint", "r1", "dep-1", "actor-1")),
            },
            revisionCatalog,
            ServingSetReader(identity, "other-endpoint", "r1", "dep-1", "actor-1"));
        var service = new ServiceInvocationApplicationService(
            resolutionService, new RecordingAuthorizer(), new RecordingDispatcher());

        var act = () => service.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<ServiceInvokeReadinessException>()
            .WithMessage("*has no invocation readiness*");
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenEndpointIsUnavailable()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
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
                    [],
                    DateTimeOffset.UtcNow),
            },
            new RecordingInvocationCatalogQueryReader
            {
                GetResult = CreateInvocationCatalogSnapshot(
                    identity,
                    Unavailable(
                        identity,
                        "chat",
                        ServiceInvokeUnavailableReason.ServingTargetMissing)),
            },
            revisionCatalog,
            new RecordingServingSetQueryReader());
        var service = new ServiceInvocationApplicationService(
            resolutionService, new RecordingAuthorizer(), new RecordingDispatcher());

        var act = () => service.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        var ex = await act.Should().ThrowAsync<ServiceInvokeReadinessException>();
        ex.Which.Snapshot.UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.ServingTargetMissing);
    }

    private static ServiceInvocationCatalogSnapshot CreateInvocationCatalogSnapshot(
        ServiceIdentity identity,
        params ServiceInvokeReadinessSnapshot[] entries) =>
        new(
            ServiceKeys.Build(identity),
            entries,
            DateTimeOffset.Parse("2026-06-05T00:00:00+00:00"),
            7,
            $"{ServiceKeys.Build(identity)}:invocation-catalog:7",
            1,
            2,
            3);

    private static ServiceInvokeReadinessSnapshot Ready(
        ServiceIdentity identity,
        string endpointId,
        string revisionId,
        string deploymentId,
        string actorId) =>
        Snapshot(
            identity,
            endpointId,
            ServiceInvokeReadinessStatus.Ready,
            ServiceInvokeUnavailableReason.Unspecified,
            revisionId,
            deploymentId,
            actorId);

    private static ServiceInvokeReadinessSnapshot Unavailable(
        ServiceIdentity identity,
        string endpointId,
        ServiceInvokeUnavailableReason reason) =>
        Snapshot(
            identity,
            endpointId,
            ServiceInvokeReadinessStatus.Unavailable,
            reason,
            string.Empty,
            string.Empty,
            string.Empty);

    private static ServiceInvokeReadinessSnapshot Snapshot(
        ServiceIdentity identity,
        string endpointId,
        ServiceInvokeReadinessStatus status,
        ServiceInvokeUnavailableReason reason,
        string revisionId,
        string deploymentId,
        string actorId) =>
        new(
            ServiceKeys.Build(identity),
            endpointId,
            status,
            reason,
            revisionId,
            deploymentId,
            actorId,
            DateTimeOffset.Parse("2026-06-05T00:00:00+00:00"),
            7,
            $"{ServiceKeys.Build(identity)}:invocation-catalog:7",
            1,
            2,
            3);

    private static RecordingServingSetQueryReader ServingSetReader(
        ServiceIdentity identity,
        string endpointId,
        string revisionId,
        string deploymentId,
        string actorId) =>
        new()
        {
            GetResult = new ServiceServingSetSnapshot(
                ServiceKeys.Build(identity),
                1,
                "rollout-1",
                [
                    new ServiceServingTargetSnapshot(
                        deploymentId,
                        revisionId,
                        actorId,
                        100,
                        ServiceServingState.Active.ToString(),
                        [endpointId]),
                ],
                DateTimeOffset.Parse("2026-06-05T00:00:00+00:00")),
        };

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

    private sealed class RecordingInvocationCatalogQueryReader : IServiceInvocationCatalogQueryReader
    {
        public ServiceInvocationCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceInvocationCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }

    private sealed class RecordingServingSetQueryReader : IServiceServingSetQueryReader
    {
        public ServiceServingSetSnapshot? GetResult { get; init; }

        public Task<ServiceServingSetSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
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
