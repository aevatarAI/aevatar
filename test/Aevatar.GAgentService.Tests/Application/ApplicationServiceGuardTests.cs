using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Application.Services;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ApplicationServiceGuardTests
{
    [Fact]
    public void ServiceCommandApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullDispatch = () => new ServiceCommandApplicationService(
            null!,
            new NoOpServiceCommandTargetProvisioner(),
            new NoOpCatalogQueryReader(),
            new NoOpCatalogProjectionPort(),
            new NoOpRevisionProjectionPort(),
            new NoOpDeploymentCatalogQueryReader(),
            new NoOpServingSetQueryReader(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new InMemoryServiceRevisionArtifactStore());
        Action nullProvisioner = () => new ServiceCommandApplicationService(
            new NoOpActorDispatchPort(),
            null!,
            new NoOpCatalogQueryReader(),
            new NoOpCatalogProjectionPort(),
            new NoOpRevisionProjectionPort(),
            new NoOpDeploymentCatalogQueryReader(),
            new NoOpServingSetQueryReader(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new InMemoryServiceRevisionArtifactStore());
        Action nullCatalogReader = () => new ServiceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpServiceCommandTargetProvisioner(),
            null!,
            new NoOpCatalogProjectionPort(),
            new NoOpRevisionProjectionPort(),
            new NoOpDeploymentCatalogQueryReader(),
            new NoOpServingSetQueryReader(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new InMemoryServiceRevisionArtifactStore());
        Action nullArtifactStore = () => new ServiceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpServiceCommandTargetProvisioner(),
            new NoOpCatalogQueryReader(),
            new NoOpCatalogProjectionPort(),
            new NoOpRevisionProjectionPort(),
            new NoOpDeploymentCatalogQueryReader(),
            new NoOpServingSetQueryReader(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            new NoOpProjectionPort(),
            null!);

        nullDispatch.Should().Throw<ArgumentNullException>();
        nullProvisioner.Should().Throw<ArgumentNullException>();
        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullArtifactStore.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceInvocationApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullResolution = () => new ServiceInvocationApplicationService(
            null!,
            new NoOpInvokeAdmissionAuthorizer(),
            new NoOpInvocationDispatcher());
        Action nullAuthorizer = () => new ServiceInvocationApplicationService(
            new ServiceInvocationResolutionService(
                new NoOpCatalogQueryReader(),
                new NoOpTrafficViewQueryReader(),
                new InMemoryServiceRevisionArtifactStore()),
            null!,
            new NoOpInvocationDispatcher());
        Action nullDispatcher = () => new ServiceInvocationApplicationService(
            new ServiceInvocationResolutionService(
                new NoOpCatalogQueryReader(),
                new NoOpTrafficViewQueryReader(),
                new InMemoryServiceRevisionArtifactStore()),
            new NoOpInvokeAdmissionAuthorizer(),
            null!);

        nullResolution.Should().Throw<ArgumentNullException>();
        nullAuthorizer.Should().Throw<ArgumentNullException>();
        nullDispatcher.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceLifecycleQueryApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullCatalogReader = () => new ServiceLifecycleQueryApplicationService(
            null!,
            new NoOpRevisionCatalogQueryReader(),
            new NoOpDeploymentCatalogQueryReader());
        Action nullDeploymentReader = () => new ServiceLifecycleQueryApplicationService(
            new NoOpCatalogQueryReader(),
            new NoOpRevisionCatalogQueryReader(),
            null!);

        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullDeploymentReader.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceServingQueryApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullServingReader = () => new ServiceServingQueryApplicationService(
            null!,
            new NoOpRolloutQueryReader(),
            new NoOpTrafficViewQueryReader());
        Action nullTrafficReader = () => new ServiceServingQueryApplicationService(
            new NoOpServingSetQueryReader(),
            new NoOpRolloutQueryReader(),
            null!);

        nullServingReader.Should().Throw<ArgumentNullException>();
        nullTrafficReader.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceGovernanceCommandApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullDispatch = () => new ServiceGovernanceCommandApplicationService(
            null!,
            new NoOpCatalogQueryReader(),
            new NoOpGovernanceCommandTargetProvisioner(),
            new NoOpGovernanceProjectionPort());
        Action nullCatalogReader = () => new ServiceGovernanceCommandApplicationService(
            new NoOpActorDispatchPort(),
            null!,
            new NoOpGovernanceCommandTargetProvisioner(),
            new NoOpGovernanceProjectionPort());
        Action nullProvisioner = () => new ServiceGovernanceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpCatalogQueryReader(),
            null!,
            new NoOpGovernanceProjectionPort());
        Action nullProjectionPort = () => new ServiceGovernanceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpCatalogQueryReader(),
            new NoOpGovernanceCommandTargetProvisioner(),
            null!);

        nullDispatch.Should().Throw<ArgumentNullException>();
        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullProvisioner.Should().Throw<ArgumentNullException>();
        nullProjectionPort.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceGovernanceQueryApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullConfigurationReader = () => new ServiceGovernanceQueryApplicationService(null!);

        nullConfigurationReader.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ActivationCapabilityViewAssembler_ShouldValidateConstructorArguments()
    {
        Action nullCatalogReader = () => new ActivationCapabilityViewAssembler(
            null!,
            new NoOpConfigurationQueryReader(),
            new InMemoryServiceRevisionArtifactStore());
        Action nullConfigurationReader = () => new ActivationCapabilityViewAssembler(
            new NoOpCatalogQueryReader(),
            null!,
            new InMemoryServiceRevisionArtifactStore());
        Action nullArtifactStore = () => new ActivationCapabilityViewAssembler(
            new NoOpCatalogQueryReader(),
            new NoOpConfigurationQueryReader(),
            null!);

        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullConfigurationReader.Should().Throw<ArgumentNullException>();
        nullArtifactStore.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void InvokeAdmissionService_ShouldValidateConstructorArguments()
    {
        Action nullCatalogReader = () => new InvokeAdmissionService(
            null!,
            new NoOpConfigurationQueryReader(),
            new NoOpInvokeAdmissionEvaluator());
        Action nullConfigurationReader = () => new InvokeAdmissionService(
            new NoOpCatalogQueryReader(),
            null!,
            new NoOpInvokeAdmissionEvaluator());
        Action nullEvaluator = () => new InvokeAdmissionService(
            new NoOpCatalogQueryReader(),
            new NoOpConfigurationQueryReader(),
            null!);

        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullConfigurationReader.Should().Throw<ArgumentNullException>();
        nullEvaluator.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CommandEnvelopeFactories_ShouldValidateArguments_AndPopulateRoutes()
    {
        var serviceFactory = typeof(ServiceCommandApplicationService).Assembly
            .GetType("Aevatar.GAgentService.Application.Internal.ServiceCommandEnvelopeFactory", throwOnError: true)!;
        var governanceFactory = typeof(ServiceGovernanceCommandApplicationService).Assembly
            .GetType("Aevatar.GAgentService.Governance.Application.Internal.ServiceCommandEnvelopeFactory", throwOnError: true)!;
        var createMethod = serviceFactory.GetMethod("Create", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;
        var createGovernanceMethod = governanceFactory.GetMethod("Create", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;
        Action invalidServiceTarget = () => createMethod.Invoke(null, [" ", new StringValue { Value = "payload" }, "corr"]);
        Action nullServicePayload = () => createMethod.Invoke(null, ["actor-1", null!, "corr"]);
        Action invalidGovernanceTarget = () => createGovernanceMethod.Invoke(null, [" ", new StringValue { Value = "payload" }, "corr"]);
        Action nullGovernancePayload = () => createGovernanceMethod.Invoke(null, ["actor-2", null!, "corr"]);

        invalidServiceTarget.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<ArgumentException>();
        nullServicePayload.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<ArgumentNullException>();
        invalidGovernanceTarget.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<ArgumentException>();
        nullGovernancePayload.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<ArgumentNullException>();

        var serviceEnvelope = (EventEnvelope)createMethod.Invoke(null, ["actor-1", new StringValue { Value = "payload" }, null!])!;
        var governanceEnvelope = (EventEnvelope)createGovernanceMethod.Invoke(null, ["actor-2", new StringValue { Value = "payload" }, null!])!;

        serviceEnvelope.Route.GetTargetActorId().Should().Be("actor-1");
        serviceEnvelope.Route.PublisherActorId.Should().Be("gagent-service.application");
        serviceEnvelope.Propagation.CorrelationId.Should().BeEmpty();
        governanceEnvelope.Route.GetTargetActorId().Should().Be("actor-2");
        governanceEnvelope.Route.PublisherActorId.Should().Be("gagent-service.governance.application");
        governanceEnvelope.Propagation.CorrelationId.Should().BeEmpty();
    }

    private sealed class NoOpActorDispatchPort : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpServiceCommandTargetProvisioner : IServiceCommandTargetProvisioner
    {
        public Task<string> EnsureDefinitionTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("definition");
        public Task<string> EnsureRevisionCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("revision");
        public Task<string> EnsureDeploymentTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("deployment");
        public Task<string> EnsureServingSetTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("serving");
        public Task<string> EnsureRolloutTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("rollout");
    }

    private sealed class NoOpGovernanceCommandTargetProvisioner : IServiceGovernanceCommandTargetProvisioner
    {
        public Task<string> EnsureConfigurationTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("configuration");
    }

    private sealed class NoOpCatalogQueryReader : IServiceCatalogQueryReader
    {
        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceCatalogSnapshot?>(null);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class NoOpRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
    {
        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);
    }

    private sealed class NoOpDeploymentCatalogQueryReader : IServiceDeploymentCatalogQueryReader
    {
        public Task<ServiceDeploymentCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class NoOpServingSetQueryReader : IServiceServingSetQueryReader
    {
        public Task<ServiceServingSetSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceServingSetSnapshot?>(null);
    }

    private sealed class NoOpRolloutQueryReader : IServiceRolloutQueryReader
    {
        public Task<ServiceRolloutSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutSnapshot?>(null);
    }

    private sealed class NoOpTrafficViewQueryReader : IServiceTrafficViewQueryReader
    {
        public Task<ServiceTrafficViewSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceTrafficViewSnapshot?>(null);
    }

    private sealed class NoOpCatalogProjectionPort : IServiceCatalogProjectionPort
    {
        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpRevisionProjectionPort : IServiceRevisionCatalogProjectionPort
    {
        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpProjectionPort
        : IServiceDeploymentCatalogProjectionPort,
          IServiceServingSetProjectionPort,
          IServiceRolloutProjectionPort,
          IServiceTrafficViewProjectionPort
    {
        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpInvocationDispatcher : IServiceInvocationDispatcher
    {
        public Task<ServiceInvocationAcceptedReceipt> DispatchAsync(ServiceInvocationResolvedTarget target, ServiceInvocationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ServiceInvocationAcceptedReceipt());
    }

    private sealed class NoOpInvokeAdmissionAuthorizer : IInvokeAdmissionAuthorizer
    {
        public Task AuthorizeAsync(string serviceKey, string deploymentId, PreparedServiceRevisionArtifact artifact, ServiceEndpointDescriptor endpoint, ServiceInvocationRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpConfigurationQueryReader : IServiceConfigurationQueryReader
    {
        public Task<ServiceConfigurationSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceConfigurationSnapshot?>(null);
    }

    private sealed class NoOpGovernanceProjectionPort : IServiceConfigurationProjectionPort
    {
        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpInvokeAdmissionEvaluator : IInvokeAdmissionEvaluator
    {
        public Task<InvokeAdmissionDecision> EvaluateAsync(InvokeAdmissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(new InvokeAdmissionDecision { Allowed = true });
    }
}
