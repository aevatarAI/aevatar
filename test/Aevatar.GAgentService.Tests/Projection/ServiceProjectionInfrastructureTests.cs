using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceProjectionInfrastructureTests
{
    [Fact]
    public async Task CatalogProjectionPortService_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingCatalogActivationService();
        var service = new ServiceCatalogProjectionPortService(activationService);

        await service.EnsureProjectionAsync(string.Empty);
        await service.EnsureProjectionAsync("actor-1");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("actor-1", "service-catalog", string.Empty, "actor-1"));
    }

    [Fact]
    public async Task RevisionProjectionPortService_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingRevisionActivationService();
        var service = new ServiceRevisionCatalogProjectionPortService(activationService);

        await service.EnsureProjectionAsync(" ");
        await service.EnsureProjectionAsync("actor-2");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("actor-2", "service-revisions", string.Empty, "actor-2"));
    }

    [Fact]
    public async Task ActivationAndReleaseServices_ShouldCreateContext_AndStopWhenIdle()
    {
        var catalogLifecycle = new RecordingCatalogLifecycle();
        var activation = new ServiceCatalogProjectionActivationService(catalogLifecycle);
        var release = new ServiceCatalogProjectionReleaseService(catalogLifecycle);

        var lease = await activation.EnsureAsync("actor-1", "service-catalog", string.Empty, "cmd-1");
        await release.ReleaseIfIdleAsync(lease);

        lease.ScopeId.Should().Be("actor-1");
        lease.SessionId.Should().Be("actor-1");
        catalogLifecycle.StartedContexts.Should().ContainSingle();
        catalogLifecycle.StartedContexts[0].ProjectionId.Should().Be("service-catalog:actor-1");
        catalogLifecycle.StoppedContexts.Should().ContainSingle();
        catalogLifecycle.StoppedContexts[0].RootActorId.Should().Be("actor-1");

        var revisionLifecycle = new RecordingRevisionLifecycle();
        var revisionActivation = new ServiceRevisionCatalogProjectionActivationService(revisionLifecycle);
        var revisionRelease = new ServiceRevisionCatalogProjectionReleaseService(revisionLifecycle);
        var revisionLease = await revisionActivation.EnsureAsync("actor-2", "service-revisions", string.Empty, "cmd-2");
        await revisionRelease.ReleaseIfIdleAsync(revisionLease);

        revisionLifecycle.StartedContexts.Should().ContainSingle();
        revisionLifecycle.StartedContexts[0].ProjectionId.Should().Be("service-revisions:actor-2");
        revisionLifecycle.StoppedContexts.Should().ContainSingle();
        revisionLease.ScopeId.Should().Be("actor-2");
    }

    [Fact]
    public void MetadataProviders_ShouldExposeStableIndexNames()
    {
        var catalog = new ServiceCatalogReadModelMetadataProvider();
        var revisions = new ServiceRevisionCatalogReadModelMetadataProvider();

        catalog.Metadata.IndexName.Should().Be("gagent-service-catalog");
        revisions.Metadata.IndexName.Should().Be("gagent-service-revisions");
        catalog.Metadata.Mappings.Should().BeEmpty();
        revisions.Metadata.Settings.Should().BeEmpty();
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterProjectionServices()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>) &&
            x.ImplementationType == typeof(ServiceCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>) &&
            x.ImplementationType == typeof(ServiceRevisionCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceCatalogQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRevisionCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceRevisionCatalogQueryReader));
    }

    private sealed class RecordingCatalogActivationService : IProjectionPortActivationService<ServiceCatalogRuntimeLease>
    {
        public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

        public Task<ServiceCatalogRuntimeLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add((rootEntityId, projectionName, input, commandId));
            return Task.FromResult(new ServiceCatalogRuntimeLease(new ServiceCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootEntityId}",
                RootActorId = rootEntityId,
            }));
        }
    }

    private sealed class RecordingRevisionActivationService : IProjectionPortActivationService<ServiceRevisionCatalogRuntimeLease>
    {
        public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

        public Task<ServiceRevisionCatalogRuntimeLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add((rootEntityId, projectionName, input, commandId));
            return Task.FromResult(new ServiceRevisionCatalogRuntimeLease(new ServiceRevisionCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootEntityId}",
                RootActorId = rootEntityId,
            }));
        }
    }

    private sealed class RecordingCatalogLifecycle : IProjectionLifecycleService<ServiceCatalogProjectionContext, IReadOnlyList<string>>
    {
        public List<ServiceCatalogProjectionContext> StartedContexts { get; } = [];
        public List<ServiceCatalogProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(ServiceCatalogProjectionContext context, CancellationToken ct = default)
        {
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(ServiceCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(ServiceCatalogProjectionContext context, CancellationToken ct = default)
        {
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ServiceCatalogProjectionContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingRevisionLifecycle : IProjectionLifecycleService<ServiceRevisionCatalogProjectionContext, IReadOnlyList<string>>
    {
        public List<ServiceRevisionCatalogProjectionContext> StartedContexts { get; } = [];
        public List<ServiceRevisionCatalogProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(ServiceRevisionCatalogProjectionContext context, CancellationToken ct = default)
        {
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(ServiceRevisionCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(ServiceRevisionCatalogProjectionContext context, CancellationToken ct = default)
        {
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ServiceRevisionCatalogProjectionContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
