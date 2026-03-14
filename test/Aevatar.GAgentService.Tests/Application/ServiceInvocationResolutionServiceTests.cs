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
    public async Task ResolveAsync_ShouldUseActiveDeploymentFromCatalogSnapshot()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
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
                GetResult = CreateCatalogSnapshot(
                    identity,
                    activeRevisionId: "r2",
                    deploymentId: "dep-2",
                    primaryActorId: "actor-2",
                    deploymentStatus: ServiceDeploymentStatus.Active.ToString(),
                    policyIds: ["policy-a"]),
            },
            artifactStore);

        var resolved = await service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        resolved.Service.ActiveRevisionId.Should().Be("r2");
        resolved.Service.DeploymentId.Should().Be("dep-2");
        resolved.Service.PrimaryActorId.Should().Be("actor-2");
        resolved.Service.PolicyIds.Should().ContainSingle("policy-a");
        resolved.Artifact.RevisionId.Should().Be("r2");
        resolved.Endpoint.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingEndpoint()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(
                    identity,
                    activeRevisionId: "r1",
                    deploymentId: "dep-1",
                    primaryActorId: "actor-1",
                    deploymentStatus: ServiceDeploymentStatus.Active.ToString()),
            },
            artifactStore);

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "missing",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Endpoint 'missing' was not found*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingIdentity()
    {
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(),
            new InMemoryServiceRevisionArtifactStore());

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
            new InMemoryServiceRevisionArtifactStore());

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
            new InMemoryServiceRevisionArtifactStore());

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
    public async Task ResolveAsync_ShouldRejectCatalogWithoutActiveDeployment()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(
                    identity,
                    activeRevisionId: string.Empty,
                    deploymentId: string.Empty,
                    primaryActorId: string.Empty,
                    deploymentStatus: ServiceDeploymentStatus.Inactive.ToString()),
            },
            new InMemoryServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no active deployment*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingPreparedArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(
                    identity,
                    activeRevisionId: "r1",
                    deploymentId: "dep-1",
                    primaryActorId: "actor-1",
                    deploymentStatus: ServiceDeploymentStatus.Active.ToString()),
            },
            new InMemoryServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Prepared artifact*was not found*");
    }

    private static ServiceCatalogSnapshot CreateCatalogSnapshot(
        ServiceIdentity identity,
        string activeRevisionId,
        string deploymentId,
        string primaryActorId,
        string deploymentStatus,
        IReadOnlyList<string>? policyIds = null) =>
        new(
            ServiceKeys.Build(identity),
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            "Service",
            activeRevisionId,
            activeRevisionId,
            deploymentId,
            primaryActorId,
            deploymentStatus,
            [],
            policyIds ?? [],
            DateTimeOffset.UtcNow);

    private sealed class RecordingCatalogQueryReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }
}
