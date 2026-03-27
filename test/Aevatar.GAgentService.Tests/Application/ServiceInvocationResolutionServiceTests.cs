using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceInvocationResolutionServiceTests
{
    [Fact]
    public async Task ResolveAsync_ShouldUseTrafficViewTargetAndArtifactEndpoint()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity, policyIds: ["policy-a"]),
            },
            new RecordingTrafficViewQueryReader
            {
                GetResult = new ServiceTrafficViewSnapshot(
                    ServiceKeys.Build(identity),
                    2,
                    "rollout-1",
                    [
                        new ServiceTrafficEndpointSnapshot(
                            "chat",
                            [
                                new ServiceTrafficTargetSnapshot(
                                    "dep-2",
                                    "r2",
                                    "actor-2",
                                    100,
                                    ServiceServingState.Active.ToString()),
                            ]),
                    ],
                    DateTimeOffset.UtcNow),
            },
            artifactStore);

        var resolved = await service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        resolved.Service.RevisionId.Should().Be("r2");
        resolved.Service.DeploymentId.Should().Be("dep-2");
        resolved.Service.PrimaryActorId.Should().Be("actor-2");
        resolved.Service.PolicyIds.Should().ContainSingle("policy-a");
        resolved.Artifact.RevisionId.Should().Be("r2");
        resolved.Endpoint.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingServingTargetForEndpoint()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingTrafficViewQueryReader
            {
                GetResult = new ServiceTrafficViewSnapshot(
                    ServiceKeys.Build(identity),
                    1,
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow),
            },
            new ConfiguredServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "missing",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no serving target*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingIdentity()
    {
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(),
            new RecordingTrafficViewQueryReader(),
            new ConfiguredServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("service identity is required.");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectBlankEndpointId()
    {
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(),
            new RecordingTrafficViewQueryReader(),
            new ConfiguredServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = " ",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("endpoint_id is required.");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingCatalogSnapshot()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(),
            new RecordingTrafficViewQueryReader(),
            new ConfiguredServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingTrafficView()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingTrafficViewQueryReader(),
            new ConfiguredServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no serving traffic view*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallbackToServingSet_WhenTrafficViewIsMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity, policyIds: ["policy-a"]),
            },
            new RecordingTrafficViewQueryReader(),
            new RecordingServingSetQueryReader
            {
                GetResult = new ServiceServingSetSnapshot(
                    ServiceKeys.Build(identity),
                    2,
                    "rollout-1",
                    [
                        new ServiceServingTargetSnapshot(
                            "dep-2",
                            "r2",
                            "actor-2",
                            100,
                            ServiceServingState.Active.ToString(),
                            []),
                    ],
                    DateTimeOffset.UtcNow),
            },
            artifactStore);

        var resolved = await service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        resolved.Service.RevisionId.Should().Be("r2");
        resolved.Service.DeploymentId.Should().Be("dep-2");
        resolved.Service.PrimaryActorId.Should().Be("actor-2");
        resolved.Service.PolicyIds.Should().ContainSingle("policy-a");
        resolved.Artifact.RevisionId.Should().Be("r2");
        resolved.Endpoint.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingPreparedArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingTrafficViewQueryReader
            {
                GetResult = new ServiceTrafficViewSnapshot(
                    ServiceKeys.Build(identity),
                    2,
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
            new ConfiguredServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Prepared artifact*was not found*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldHonorExplicitRevisionSelection()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingTrafficViewQueryReader
            {
                GetResult = new ServiceTrafficViewSnapshot(
                    ServiceKeys.Build(identity),
                    2,
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
                                new ServiceTrafficTargetSnapshot(
                                    "dep-2",
                                    "r2",
                                    "actor-2",
                                    100,
                                    ServiceServingState.Active.ToString()),
                            ]),
                    ],
                    DateTimeOffset.UtcNow),
            },
            artifactStore);

        var resolved = await service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            RevisionId = "r1",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        resolved.Service.RevisionId.Should().Be("r1");
        resolved.Service.DeploymentId.Should().Be("dep-1");
        resolved.Artifact.RevisionId.Should().Be("r1");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectExplicitRevisionWhenTargetIsNotActive()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingTrafficViewQueryReader
            {
                GetResult = new ServiceTrafficViewSnapshot(
                    ServiceKeys.Build(identity),
                    2,
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
                                    ServiceServingState.Paused.ToString()),
                            ]),
                    ],
                    DateTimeOffset.UtcNow),
            },
            new ConfiguredServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            RevisionId = "r1",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*Revision 'r1' is not active on service '{ServiceKeys.Build(identity)}'.*");
    }

    private static ServiceCatalogSnapshot CreateCatalogSnapshot(
        ServiceIdentity identity,
        IReadOnlyList<string>? policyIds = null) =>
        new(
            ServiceKeys.Build(identity),
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            "Service",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ServiceDeploymentStatus.Unspecified.ToString(),
            [],
            policyIds ?? [],
            DateTimeOffset.UtcNow);

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

    private sealed class RecordingServingSetQueryReader : IServiceServingSetQueryReader
    {
        public ServiceServingSetSnapshot? GetResult { get; init; }

        public Task<ServiceServingSetSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }
}
