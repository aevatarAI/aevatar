using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceInvocationResolutionServiceTests
{
    [Fact]
    public async Task ResolveAsync_ShouldPreferActiveServingRevision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            "tenant:app:default:svc",
            "r2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r2", GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var reader = new RecordingCatalogQueryReader(
            new ServiceCatalogSnapshot(
                "tenant:app:default:svc",
                "tenant",
                "app",
                "default",
                "svc",
                "Service",
                "r1",
                "r2",
                "dep",
                "actor",
                "Active",
                [new ServiceEndpointSnapshot("chat", "chat", "Command", "type.googleapis.com/test.command", string.Empty, string.Empty)],
                DateTimeOffset.UtcNow));
        var service = new ServiceInvocationResolutionService(reader, artifactStore);

        var resolved = await service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        resolved.Service.ActiveServingRevisionId.Should().Be("r2");
        resolved.Artifact.RevisionId.Should().Be("r2");
        resolved.Endpoint.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingEndpoint()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            "tenant:app:default:svc",
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"));
        var service = new ServiceInvocationResolutionService(
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
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow)),
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
    public async Task ResolveAsync_ShouldFallbackToDefaultServingRevision_WhenActiveRevisionIsMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            "tenant:app:default:svc",
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1", GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var service = new ServiceInvocationResolutionService(
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
                    "dep",
                    "actor",
                    "Active",
                    [new ServiceEndpointSnapshot("chat", "chat", "Command", "type.googleapis.com/test.command", string.Empty, string.Empty)],
                    DateTimeOffset.UtcNow)),
            artifactStore);

        var resolved = await service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        resolved.Artifact.RevisionId.Should().Be("r1");
        resolved.Endpoint.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingIdentity()
    {
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(null),
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
            new RecordingCatalogQueryReader(null),
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
    public async Task ResolveAsync_ShouldRejectMissingServiceSnapshot()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(null),
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
    public async Task ResolveAsync_ShouldRejectServiceWithoutServingRevision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(
                new ServiceCatalogSnapshot(
                    "tenant:app:default:svc",
                    "tenant",
                    "app",
                    "default",
                    "svc",
                    "Service",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow)),
            new InMemoryServiceRevisionArtifactStore());

        var act = () => service.ResolveAsync(new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no active or default serving revision*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMissingPreparedArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceInvocationResolutionService(
            new RecordingCatalogQueryReader(
                new ServiceCatalogSnapshot(
                    "tenant:app:default:svc",
                    "tenant",
                    "app",
                    "default",
                    "svc",
                    "Service",
                    "r1",
                    "r1",
                    "dep",
                    "actor",
                    "Active",
                    [new ServiceEndpointSnapshot("chat", "chat", "Command", "type.googleapis.com/test.command", string.Empty, string.Empty)],
                    DateTimeOffset.UtcNow)),
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
}
