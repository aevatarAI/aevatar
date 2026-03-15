using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.DependencyInjection;
using Aevatar.GAgentService.Governance.Projection.Metadata;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Projectors;
using Aevatar.GAgentService.Governance.Projection.Queries;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceConfigurationProjectionInfrastructureTests
{
    [Fact]
    public async Task ConfigurationProjectionPortService_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingConfigurationActivationService();
        var service = new ServiceConfigurationProjectionPortService(activationService);

        await service.EnsureProjectionAsync(string.Empty);
        await service.EnsureProjectionAsync("config-actor");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("config-actor", "service-configuration", string.Empty, "config-actor"));
    }

    [Fact]
    public async Task ActivationAndReleaseServices_ShouldCreateContextLeaseAndStopWhenIdle()
    {
        var lifecycle = new RecordingConfigurationLifecycle();
        var activation = new ServiceConfigurationProjectionActivationService(lifecycle);
        var release = new ServiceConfigurationProjectionReleaseService(lifecycle);
        var lease = await activation.EnsureAsync("config-actor", "service-configuration", string.Empty, "cmd-config");
        await release.ReleaseIfIdleAsync(lease);

        lease.ScopeId.Should().Be("config-actor");
        lease.SessionId.Should().Be("config-actor");
        lifecycle.StartedContexts.Should().ContainSingle();
        lifecycle.StartedContexts[0].ProjectionId.Should().Be("service-configuration:config-actor");
        lifecycle.StoppedContexts.Should().ContainSingle();
        ((IProjectionContext)lease.Context).ProjectionId.Should().Be("service-configuration:config-actor");
    }

    [Fact]
    public void MetadataProviders_ShouldExposeStableIndexNames()
    {
        var metadataProvider = new ServiceConfigurationReadModelMetadataProvider();

        metadataProvider.Metadata.IndexName.Should().Be("gagent-service-configuration");
        metadataProvider.Metadata.Mappings.Should().BeEmpty();
        metadataProvider.Metadata.Settings.Should().BeEmpty();
        metadataProvider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjection_ShouldRegisterGovernanceProjectionServices()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceGovernanceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceConfigurationReadModel>) &&
            x.ImplementationType == typeof(ServiceConfigurationReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceConfigurationProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceConfigurationQueryReader) &&
            x.ImplementationType == typeof(ServiceConfigurationQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionProjector<ServiceConfigurationProjectionContext, IReadOnlyList<string>>) &&
            x.ImplementationType == typeof(ServiceConfigurationProjector));
    }

    private sealed class RecordingConfigurationActivationService : IProjectionPortActivationService<ServiceConfigurationRuntimeLease>
    {
        public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

        public Task<ServiceConfigurationRuntimeLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add((rootEntityId, projectionName, input, commandId));
            return Task.FromResult(new ServiceConfigurationRuntimeLease(new ServiceConfigurationProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootEntityId}",
                RootActorId = rootEntityId,
            }));
        }
    }

    private sealed class RecordingConfigurationLifecycle : IProjectionLifecycleService<ServiceConfigurationProjectionContext, IReadOnlyList<string>>
    {
        public List<ServiceConfigurationProjectionContext> StartedContexts { get; } = [];

        public List<ServiceConfigurationProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(ServiceConfigurationProjectionContext context, CancellationToken ct = default)
        {
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(ServiceConfigurationProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(ServiceConfigurationProjectionContext context, CancellationToken ct = default)
        {
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ServiceConfigurationProjectionContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
