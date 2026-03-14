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
            new NoOpRevisionProjectionPort());
        Action nullProvisioner = () => new ServiceCommandApplicationService(
            new NoOpActorDispatchPort(),
            null!,
            new NoOpCatalogQueryReader(),
            new NoOpCatalogProjectionPort(),
            new NoOpRevisionProjectionPort());
        Action nullCatalogReader = () => new ServiceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpServiceCommandTargetProvisioner(),
            null!,
            new NoOpCatalogProjectionPort(),
            new NoOpRevisionProjectionPort());
        Action nullCatalogProjection = () => new ServiceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpServiceCommandTargetProvisioner(),
            new NoOpCatalogQueryReader(),
            null!,
            new NoOpRevisionProjectionPort());
        Action nullRevisionProjection = () => new ServiceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpServiceCommandTargetProvisioner(),
            new NoOpCatalogQueryReader(),
            new NoOpCatalogProjectionPort(),
            null!);

        nullDispatch.Should().Throw<ArgumentNullException>();
        nullProvisioner.Should().Throw<ArgumentNullException>();
        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullCatalogProjection.Should().Throw<ArgumentNullException>();
        nullRevisionProjection.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceInvocationApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullResolution = () => new ServiceInvocationApplicationService(
            null!,
            new NoOpInvokeAdmissionAuthorizer(),
            new NoOpInvocationDispatcher());
        Action nullAuthorizer = () => new ServiceInvocationApplicationService(
            new ServiceInvocationResolutionService(new NoOpCatalogQueryReader(), new InMemoryServiceRevisionArtifactStore()),
            null!,
            new NoOpInvocationDispatcher());
        Action nullDispatcher = () => new ServiceInvocationApplicationService(
            new ServiceInvocationResolutionService(new NoOpCatalogQueryReader(), new InMemoryServiceRevisionArtifactStore()),
            new NoOpInvokeAdmissionAuthorizer(),
            null!);

        nullResolution.Should().Throw<ArgumentNullException>();
        nullAuthorizer.Should().Throw<ArgumentNullException>();
        nullDispatcher.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceQueryApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullCatalogReader = () => new ServiceQueryApplicationService(
            null!,
            new NoOpRevisionCatalogQueryReader());
        Action nullRevisionReader = () => new ServiceQueryApplicationService(
            new NoOpCatalogQueryReader(),
            null!);

        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullRevisionReader.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceGovernanceCommandApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullDispatch = () => new ServiceGovernanceCommandApplicationService(
            null!,
            new NoOpCatalogQueryReader(),
            new NoOpGovernanceCommandTargetProvisioner(),
            new NoOpGovernanceProjectionPort(),
            new NoOpGovernanceProjectionPort(),
            new NoOpGovernanceProjectionPort());
        Action nullCatalogReader = () => new ServiceGovernanceCommandApplicationService(
            new NoOpActorDispatchPort(),
            null!,
            new NoOpGovernanceCommandTargetProvisioner(),
            new NoOpGovernanceProjectionPort(),
            new NoOpGovernanceProjectionPort(),
            new NoOpGovernanceProjectionPort());
        Action nullProvisioner = () => new ServiceGovernanceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpCatalogQueryReader(),
            null!,
            new NoOpGovernanceProjectionPort(),
            new NoOpGovernanceProjectionPort(),
            new NoOpGovernanceProjectionPort());
        Action nullBindingProjection = () => new ServiceGovernanceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpCatalogQueryReader(),
            new NoOpGovernanceCommandTargetProvisioner(),
            null!,
            new NoOpGovernanceProjectionPort(),
            new NoOpGovernanceProjectionPort());
        Action nullEndpointProjection = () => new ServiceGovernanceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpCatalogQueryReader(),
            new NoOpGovernanceCommandTargetProvisioner(),
            new NoOpGovernanceProjectionPort(),
            null!,
            new NoOpGovernanceProjectionPort());
        Action nullPolicyProjection = () => new ServiceGovernanceCommandApplicationService(
            new NoOpActorDispatchPort(),
            new NoOpCatalogQueryReader(),
            new NoOpGovernanceCommandTargetProvisioner(),
            new NoOpGovernanceProjectionPort(),
            new NoOpGovernanceProjectionPort(),
            null!);

        nullDispatch.Should().Throw<ArgumentNullException>();
        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullProvisioner.Should().Throw<ArgumentNullException>();
        nullBindingProjection.Should().Throw<ArgumentNullException>();
        nullEndpointProjection.Should().Throw<ArgumentNullException>();
        nullPolicyProjection.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceGovernanceQueryApplicationService_ShouldValidateConstructorArguments()
    {
        Action nullBindingReader = () => new ServiceGovernanceQueryApplicationService(
            null!,
            new NoOpEndpointCatalogQueryReader(),
            new NoOpPolicyQueryReader());
        Action nullEndpointReader = () => new ServiceGovernanceQueryApplicationService(
            new NoOpBindingQueryReader(),
            null!,
            new NoOpPolicyQueryReader());
        Action nullPolicyReader = () => new ServiceGovernanceQueryApplicationService(
            new NoOpBindingQueryReader(),
            new NoOpEndpointCatalogQueryReader(),
            null!);

        nullBindingReader.Should().Throw<ArgumentNullException>();
        nullEndpointReader.Should().Throw<ArgumentNullException>();
        nullPolicyReader.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ActivationCapabilityViewAssembler_ShouldValidateConstructorArguments()
    {
        Action nullCatalogReader = () => new ActivationCapabilityViewAssembler(
            null!,
            new NoOpBindingQueryReader(),
            new NoOpEndpointCatalogQueryReader(),
            new NoOpPolicyQueryReader(),
            new InMemoryServiceRevisionArtifactStore());
        Action nullBindingReader = () => new ActivationCapabilityViewAssembler(
            new NoOpCatalogQueryReader(),
            null!,
            new NoOpEndpointCatalogQueryReader(),
            new NoOpPolicyQueryReader(),
            new InMemoryServiceRevisionArtifactStore());
        Action nullEndpointReader = () => new ActivationCapabilityViewAssembler(
            new NoOpCatalogQueryReader(),
            new NoOpBindingQueryReader(),
            null!,
            new NoOpPolicyQueryReader(),
            new InMemoryServiceRevisionArtifactStore());
        Action nullPolicyReader = () => new ActivationCapabilityViewAssembler(
            new NoOpCatalogQueryReader(),
            new NoOpBindingQueryReader(),
            new NoOpEndpointCatalogQueryReader(),
            null!,
            new InMemoryServiceRevisionArtifactStore());
        Action nullArtifactStore = () => new ActivationCapabilityViewAssembler(
            new NoOpCatalogQueryReader(),
            new NoOpBindingQueryReader(),
            new NoOpEndpointCatalogQueryReader(),
            new NoOpPolicyQueryReader(),
            null!);

        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullBindingReader.Should().Throw<ArgumentNullException>();
        nullEndpointReader.Should().Throw<ArgumentNullException>();
        nullPolicyReader.Should().Throw<ArgumentNullException>();
        nullArtifactStore.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void InvokeAdmissionService_ShouldValidateConstructorArguments()
    {
        Action nullCatalogReader = () => new InvokeAdmissionService(
            null!,
            new NoOpEndpointCatalogQueryReader(),
            new NoOpPolicyQueryReader(),
            new NoOpInvokeAdmissionEvaluator());
        Action nullEndpointReader = () => new InvokeAdmissionService(
            new NoOpCatalogQueryReader(),
            null!,
            new NoOpPolicyQueryReader(),
            new NoOpInvokeAdmissionEvaluator());
        Action nullPolicyReader = () => new InvokeAdmissionService(
            new NoOpCatalogQueryReader(),
            new NoOpEndpointCatalogQueryReader(),
            null!,
            new NoOpInvokeAdmissionEvaluator());
        Action nullEvaluator = () => new InvokeAdmissionService(
            new NoOpCatalogQueryReader(),
            new NoOpEndpointCatalogQueryReader(),
            new NoOpPolicyQueryReader(),
            null!);

        nullCatalogReader.Should().Throw<ArgumentNullException>();
        nullEndpointReader.Should().Throw<ArgumentNullException>();
        nullPolicyReader.Should().Throw<ArgumentNullException>();
        nullEvaluator.Should().Throw<ArgumentNullException>();
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
    }

    private sealed class NoOpGovernanceCommandTargetProvisioner : IServiceGovernanceCommandTargetProvisioner
    {
        public Task<string> EnsureBindingCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("binding");
        public Task<string> EnsureEndpointCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("endpoint");
        public Task<string> EnsurePolicyCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult("policy");
    }

    private sealed class NoOpCatalogQueryReader : IServiceCatalogQueryReader
    {
        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceCatalogSnapshot?>(null);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class NoOpRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
    {
        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);
    }

    private sealed class NoOpCatalogProjectionPort : IServiceCatalogProjectionPort
    {
        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpRevisionProjectionPort : IServiceRevisionCatalogProjectionPort
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

    private sealed class NoOpBindingQueryReader : IServiceBindingQueryReader
    {
        public Task<ServiceBindingCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceBindingCatalogSnapshot?>(null);
    }

    private sealed class NoOpEndpointCatalogQueryReader : IServiceEndpointCatalogQueryReader
    {
        public Task<ServiceEndpointCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceEndpointCatalogSnapshot?>(null);
    }

    private sealed class NoOpPolicyQueryReader : IServicePolicyQueryReader
    {
        public Task<ServicePolicyCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServicePolicyCatalogSnapshot?>(null);
    }

    private sealed class NoOpGovernanceProjectionPort
        : IServiceBindingProjectionPort, IServiceEndpointCatalogProjectionPort, IServicePolicyProjectionPort
    {
        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpInvokeAdmissionEvaluator : IInvokeAdmissionEvaluator
    {
        public Task<InvokeAdmissionDecision> EvaluateAsync(InvokeAdmissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(new InvokeAdmissionDecision { Allowed = true });
    }
}
