using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.DependencyInjection;
using Aevatar.GAgentService.Governance.Projection.Metadata;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Queries;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class GovernanceProjectionInfrastructureTests
{
    [Fact]
    public async Task BindingProjectionPortService_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingBindingActivationService();
        var service = new ServiceBindingProjectionPortService(activationService);

        await service.EnsureProjectionAsync(string.Empty);
        await service.EnsureProjectionAsync("binding-actor");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("binding-actor", "service-bindings", string.Empty, "binding-actor"));
    }

    [Fact]
    public async Task EndpointAndPolicyProjectionPortServices_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var endpointActivation = new RecordingEndpointActivationService();
        var endpointService = new ServiceEndpointCatalogProjectionPortService(endpointActivation);
        var policyActivation = new RecordingPolicyActivationService();
        var policyService = new ServicePolicyProjectionPortService(policyActivation);

        await endpointService.EnsureProjectionAsync(" ");
        await endpointService.EnsureProjectionAsync("endpoint-actor");
        await policyService.EnsureProjectionAsync(string.Empty);
        await policyService.EnsureProjectionAsync("policy-actor");

        endpointActivation.Calls.Should().ContainSingle();
        endpointActivation.Calls[0].Should().Be(("endpoint-actor", "service-endpoint-catalog", string.Empty, "endpoint-actor"));
        policyActivation.Calls.Should().ContainSingle();
        policyActivation.Calls[0].Should().Be(("policy-actor", "service-policies", string.Empty, "policy-actor"));
    }

    [Fact]
    public async Task ActivationAndReleaseServices_ShouldCreateContextLeaseAndStopWhenIdle()
    {
        var bindingLifecycle = new RecordingBindingLifecycle();
        var bindingActivation = new ServiceBindingProjectionActivationService(bindingLifecycle);
        var bindingRelease = new ServiceBindingProjectionReleaseService(bindingLifecycle);
        var bindingLease = await bindingActivation.EnsureAsync("binding-actor", "service-bindings", string.Empty, "cmd-binding");
        await bindingRelease.ReleaseIfIdleAsync(bindingLease);

        bindingLease.ScopeId.Should().Be("binding-actor");
        bindingLease.SessionId.Should().Be("binding-actor");
        bindingLifecycle.StartedContexts.Should().ContainSingle();
        bindingLifecycle.StartedContexts[0].ProjectionId.Should().Be("service-bindings:binding-actor");
        bindingLifecycle.StoppedContexts.Should().ContainSingle();
        ((IProjectionContext)bindingLease.Context).ProjectionId.Should().Be("service-bindings:binding-actor");

        var endpointLifecycle = new RecordingEndpointLifecycle();
        var endpointActivation = new ServiceEndpointCatalogProjectionActivationService(endpointLifecycle);
        var endpointRelease = new ServiceEndpointCatalogProjectionReleaseService(endpointLifecycle);
        var endpointLease = await endpointActivation.EnsureAsync("endpoint-actor", "service-endpoint-catalog", string.Empty, "cmd-endpoint");
        await endpointRelease.ReleaseIfIdleAsync(endpointLease);

        endpointLease.ScopeId.Should().Be("endpoint-actor");
        endpointLease.SessionId.Should().Be("endpoint-actor");
        endpointLifecycle.StartedContexts.Should().ContainSingle();
        endpointLifecycle.StartedContexts[0].ProjectionId.Should().Be("service-endpoint-catalog:endpoint-actor");
        endpointLifecycle.StoppedContexts.Should().ContainSingle();
        ((IProjectionContext)endpointLease.Context).ProjectionId.Should().Be("service-endpoint-catalog:endpoint-actor");

        var policyLifecycle = new RecordingPolicyLifecycle();
        var policyActivation = new ServicePolicyProjectionActivationService(policyLifecycle);
        var policyRelease = new ServicePolicyProjectionReleaseService(policyLifecycle);
        var policyLease = await policyActivation.EnsureAsync("policy-actor", "service-policies", string.Empty, "cmd-policy");
        await policyRelease.ReleaseIfIdleAsync(policyLease);

        policyLease.ScopeId.Should().Be("policy-actor");
        policyLease.SessionId.Should().Be("policy-actor");
        policyLifecycle.StartedContexts.Should().ContainSingle();
        policyLifecycle.StartedContexts[0].ProjectionId.Should().Be("service-policies:policy-actor");
        policyLifecycle.StoppedContexts.Should().ContainSingle();
        ((IProjectionContext)policyLease.Context).ProjectionId.Should().Be("service-policies:policy-actor");
    }

    [Fact]
    public void MetadataProviders_ShouldExposeStableIndexNames()
    {
        var binding = new ServiceBindingCatalogReadModelMetadataProvider();
        var endpointCatalog = new ServiceEndpointCatalogReadModelMetadataProvider();
        var policy = new ServicePolicyCatalogReadModelMetadataProvider();

        binding.Metadata.IndexName.Should().Be("gagent-service-bindings");
        endpointCatalog.Metadata.IndexName.Should().Be("gagent-service-endpoint-catalog");
        policy.Metadata.IndexName.Should().Be("gagent-service-policies");
        binding.Metadata.Mappings.Should().BeEmpty();
        endpointCatalog.Metadata.Settings.Should().BeEmpty();
        policy.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjection_ShouldRegisterGovernanceProjectionServices()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceGovernanceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceBindingCatalogReadModel>) &&
            x.ImplementationType == typeof(ServiceBindingCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceEndpointCatalogReadModel>) &&
            x.ImplementationType == typeof(ServiceEndpointCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServicePolicyCatalogReadModel>) &&
            x.ImplementationType == typeof(ServicePolicyCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceBindingQueryReader) &&
            x.ImplementationType == typeof(ServiceBindingQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceEndpointCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceEndpointCatalogQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServicePolicyQueryReader) &&
            x.ImplementationType == typeof(ServicePolicyQueryReader));
    }

    private sealed class RecordingBindingActivationService : IProjectionPortActivationService<ServiceBindingRuntimeLease>
    {
        public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

        public Task<ServiceBindingRuntimeLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add((rootEntityId, projectionName, input, commandId));
            return Task.FromResult(new ServiceBindingRuntimeLease(new ServiceBindingProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootEntityId}",
                RootActorId = rootEntityId,
            }));
        }
    }

    private sealed class RecordingEndpointActivationService : IProjectionPortActivationService<ServiceEndpointCatalogRuntimeLease>
    {
        public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

        public Task<ServiceEndpointCatalogRuntimeLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add((rootEntityId, projectionName, input, commandId));
            return Task.FromResult(new ServiceEndpointCatalogRuntimeLease(new ServiceEndpointCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootEntityId}",
                RootActorId = rootEntityId,
            }));
        }
    }

    private sealed class RecordingPolicyActivationService : IProjectionPortActivationService<ServicePolicyRuntimeLease>
    {
        public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

        public Task<ServicePolicyRuntimeLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add((rootEntityId, projectionName, input, commandId));
            return Task.FromResult(new ServicePolicyRuntimeLease(new ServicePolicyProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootEntityId}",
                RootActorId = rootEntityId,
            }));
        }
    }

    private sealed class RecordingBindingLifecycle : IProjectionLifecycleService<ServiceBindingProjectionContext, IReadOnlyList<string>>
    {
        public List<ServiceBindingProjectionContext> StartedContexts { get; } = [];

        public List<ServiceBindingProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(ServiceBindingProjectionContext context, CancellationToken ct = default)
        {
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(ServiceBindingProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(ServiceBindingProjectionContext context, CancellationToken ct = default)
        {
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ServiceBindingProjectionContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingEndpointLifecycle : IProjectionLifecycleService<ServiceEndpointCatalogProjectionContext, IReadOnlyList<string>>
    {
        public List<ServiceEndpointCatalogProjectionContext> StartedContexts { get; } = [];

        public List<ServiceEndpointCatalogProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(ServiceEndpointCatalogProjectionContext context, CancellationToken ct = default)
        {
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(ServiceEndpointCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(ServiceEndpointCatalogProjectionContext context, CancellationToken ct = default)
        {
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ServiceEndpointCatalogProjectionContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingPolicyLifecycle : IProjectionLifecycleService<ServicePolicyProjectionContext, IReadOnlyList<string>>
    {
        public List<ServicePolicyProjectionContext> StartedContexts { get; } = [];

        public List<ServicePolicyProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(ServicePolicyProjectionContext context, CancellationToken ct = default)
        {
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(ServicePolicyProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(ServicePolicyProjectionContext context, CancellationToken ct = default)
        {
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ServicePolicyProjectionContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
