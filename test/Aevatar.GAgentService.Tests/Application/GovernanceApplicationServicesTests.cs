using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Application.Services;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GovernanceApplicationServicesTests
{
    [Fact]
    public async Task ActivationCapabilityViewAssembler_ShouldComposeBindingsEndpointsPoliciesAndArtifactFallback()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "r1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "published"),
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "internal-only")));
        var assembler = new ActivationCapabilityViewAssembler(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity, policyIds: ["policy-definition", "policy-missing"]),
            },
            new RecordingBindingQueryReader
            {
                GetResult = new ServiceBindingCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        new ServiceBindingSnapshot(
                            "binding-a",
                            "Binding A",
                            ServiceBindingKind.Service.ToString(),
                            ["policy-binding"],
                            false,
                            ServiceKeys.Build(GAgentServiceTestKit.CreateIdentity(serviceId: "dependency")),
                            "run",
                            null,
                            null,
                            null),
                        new ServiceBindingSnapshot(
                            "binding-retired",
                            "Retired",
                            ServiceBindingKind.Secret.ToString(),
                            [],
                            true,
                            null,
                            null,
                            null,
                            null,
                            "secret"),
                    ],
                    DateTimeOffset.UtcNow),
            },
            new RecordingEndpointCatalogQueryReader
            {
                GetResult = new ServiceEndpointCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        new ServiceEndpointExposureSnapshot(
                            "published",
                            "Published",
                            ServiceEndpointKind.Command.ToString(),
                            "type.googleapis.com/demo.Published",
                            string.Empty,
                            "published",
                            ServiceEndpointExposureKind.Public.ToString(),
                            ["policy-endpoint"]),
                    ],
                    DateTimeOffset.UtcNow),
            },
            new RecordingPolicyQueryReader
            {
                GetResult = new ServicePolicyCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        CreatePolicySnapshot("policy-definition"),
                        CreatePolicySnapshot("policy-binding"),
                        CreatePolicySnapshot("policy-endpoint"),
                    ],
                    DateTimeOffset.UtcNow),
            },
            artifactStore);

        var view = await assembler.GetAsync(identity, "r1");

        view.RevisionId.Should().Be("r1");
        view.Bindings.Should().ContainSingle(x => x.BindingId == "binding-a");
        view.Endpoints.Should().Contain(x => x.EndpointId == "published" && x.ExposureKind == ServiceEndpointExposureKind.Public);
        view.Endpoints.Should().Contain(x => x.EndpointId == "internal-only" && x.ExposureKind == ServiceEndpointExposureKind.Internal);
        view.Policies.Select(x => x.PolicyId).Should().BeEquivalentTo(["policy-definition", "policy-binding", "policy-endpoint"]);
        view.MissingPolicyIds.Should().ContainSingle("policy-missing");
    }

    [Fact]
    public async Task ActivationCapabilityViewAssembler_ShouldRejectBlankRevisionMissingDefinitionAndMissingArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var assembler = new ActivationCapabilityViewAssembler(
            new RecordingCatalogQueryReader(),
            new RecordingBindingQueryReader(),
            new RecordingEndpointCatalogQueryReader(),
            new RecordingPolicyQueryReader(),
            new InMemoryServiceRevisionArtifactStore());

        var blankRevision = () => assembler.GetAsync(identity, " ");
        var missingDefinition = () => assembler.GetAsync(identity, "r1");

        await blankRevision.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision_id is required*");
        await missingDefinition.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service definition*was not found*");

        var missingArtifactAssembler = new ActivationCapabilityViewAssembler(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingBindingQueryReader(),
            new RecordingEndpointCatalogQueryReader(),
            new RecordingPolicyQueryReader(),
            new InMemoryServiceRevisionArtifactStore());

        var missingArtifact = () => missingArtifactAssembler.GetAsync(identity, "r1");

        await missingArtifact.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Prepared artifact*was not found*");
    }

    [Fact]
    public async Task InvokeAdmissionService_ShouldPopulateRequestAndAllowWhenEvaluatorApproves()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var evaluator = new RecordingInvokeAdmissionEvaluator
        {
            Decision = new InvokeAdmissionDecision
            {
                Allowed = true,
            },
        };
        var service = new InvokeAdmissionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity, policyIds: ["policy-definition", "policy-missing"]),
            },
            new RecordingEndpointCatalogQueryReader
            {
                GetResult = new ServiceEndpointCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        new ServiceEndpointExposureSnapshot(
                            "invoke",
                            "Invoke",
                            ServiceEndpointKind.Command.ToString(),
                            "type.googleapis.com/demo.Invoke",
                            string.Empty,
                            "invoke",
                            ServiceEndpointExposureKind.Public.ToString(),
                            ["policy-endpoint"]),
                    ],
                    DateTimeOffset.UtcNow),
            },
            new RecordingPolicyQueryReader
            {
                GetResult = new ServicePolicyCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        CreatePolicySnapshot("policy-definition"),
                        CreatePolicySnapshot("policy-endpoint"),
                    ],
                    DateTimeOffset.UtcNow),
            },
            evaluator);
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "invoke"));
        var request = new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "invoke",
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = "tenant/app/default/caller",
            },
        };

        await service.AuthorizeAsync(ServiceKeys.Build(identity), "dep-1", artifact, artifact.Endpoints[0], request);

        evaluator.LastRequest.Should().NotBeNull();
        evaluator.LastRequest!.Policies.Select(x => x.PolicyId).Should().BeEquivalentTo(["policy-definition", "policy-endpoint"]);
        evaluator.LastRequest.MissingPolicyIds.Should().ContainSingle("policy-missing");
        evaluator.LastRequest.HasActiveDeployment.Should().BeTrue();
        evaluator.LastRequest.Caller.ServiceKey.Should().Be("tenant/app/default/caller");
    }

    [Fact]
    public async Task InvokeAdmissionService_ShouldRejectMissingDefinitionMissingCatalogAndRejectedDecision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var evaluator = new RecordingInvokeAdmissionEvaluator
        {
            Decision = new InvokeAdmissionDecision
            {
                Allowed = false,
                Violations =
                {
                    new AdmissionViolation
                    {
                        Code = "forbidden",
                        SubjectId = "policy-a",
                        Message = "not allowed",
                    },
                },
            },
        };
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "invoke"));
        var request = new ServiceInvocationRequest
        {
            Identity = identity.Clone(),
            EndpointId = "invoke",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        var missingDefinitionService = new InvokeAdmissionService(
            new RecordingCatalogQueryReader(),
            new RecordingEndpointCatalogQueryReader(),
            new RecordingPolicyQueryReader(),
            evaluator);
        var missingDefinition = () => missingDefinitionService.AuthorizeAsync(
            ServiceKeys.Build(identity),
            "dep-1",
            artifact,
            artifact.Endpoints[0],
            request);
        await missingDefinition.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service definition*was not found*");

        var missingCatalogService = new InvokeAdmissionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingEndpointCatalogQueryReader(),
            new RecordingPolicyQueryReader(),
            evaluator);
        var missingCatalog = () => missingCatalogService.AuthorizeAsync(
            ServiceKeys.Build(identity),
            "dep-1",
            artifact,
            artifact.Endpoints[0],
            request);
        await missingCatalog.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Endpoint catalog*was not found*");

        var rejectedService = new InvokeAdmissionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingEndpointCatalogQueryReader
            {
                GetResult = new ServiceEndpointCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        new ServiceEndpointExposureSnapshot(
                            "invoke",
                            "Invoke",
                            ServiceEndpointKind.Command.ToString(),
                            "type.googleapis.com/demo.Invoke",
                            string.Empty,
                            "invoke",
                            ServiceEndpointExposureKind.Public.ToString(),
                            []),
                    ],
                    DateTimeOffset.UtcNow),
            },
            new RecordingPolicyQueryReader(),
            evaluator);
        var rejected = () => rejectedService.AuthorizeAsync(
            ServiceKeys.Build(identity),
            "dep-1",
            artifact,
            artifact.Endpoints[0],
            request);
        await rejected.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*forbidden:policy-a:not allowed*");
    }

    [Fact]
    public async Task ServiceGovernanceCommandApplicationService_ShouldEnsureTargetsProjectionsAndDispatchCommands()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var targetProvisioner = new RecordingGovernanceCommandTargetProvisioner();
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingGovernanceProjectionPort();
        var service = new ServiceGovernanceCommandApplicationService(
            dispatchPort,
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            targetProvisioner,
            projectionPort,
            projectionPort,
            projectionPort);

        var bindingSpec = CreateServiceBindingSpec(identity, "binding-a", ServiceBindingKind.Service);
        var endpointSpec = CreateEndpointCatalogSpec(identity, "invoke");
        var policySpec = CreatePolicySpec(identity, "policy-a");

        await service.CreateBindingAsync(new CreateServiceBindingCommand { Spec = bindingSpec.Clone() });
        await service.UpdateBindingAsync(new UpdateServiceBindingCommand { Spec = bindingSpec.Clone() });
        await service.RetireBindingAsync(new RetireServiceBindingCommand { Identity = identity.Clone(), BindingId = "binding-a" });
        await service.CreateEndpointCatalogAsync(new CreateServiceEndpointCatalogCommand { Spec = endpointSpec.Clone() });
        await service.UpdateEndpointCatalogAsync(new UpdateServiceEndpointCatalogCommand { Spec = endpointSpec.Clone() });
        await service.CreatePolicyAsync(new CreateServicePolicyCommand { Spec = policySpec.Clone() });
        await service.UpdatePolicyAsync(new UpdateServicePolicyCommand { Spec = policySpec.Clone() });
        await service.RetirePolicyAsync(new RetireServicePolicyCommand { Identity = identity.Clone(), PolicyId = "policy-a" });

        targetProvisioner.BindingRequests.Should().HaveCount(3);
        targetProvisioner.EndpointRequests.Should().HaveCount(2);
        targetProvisioner.PolicyRequests.Should().HaveCount(3);
        projectionPort.ActorIds.Should().HaveCount(8);
        dispatchPort.Calls.Should().HaveCount(8);
        dispatchPort.Calls.Select(x => x.actorId).Should().Contain(ServiceActorIds.BindingCatalog(identity));
        dispatchPort.Calls.Select(x => x.actorId).Should().Contain(ServiceActorIds.EndpointCatalog(identity));
        dispatchPort.Calls.Select(x => x.actorId).Should().Contain(ServiceActorIds.PolicyCatalog(identity));
        dispatchPort.Calls.Select(x => x.envelope.Propagation.CorrelationId).Should().Contain($"{ServiceKeys.Build(identity)}:binding:binding-a");
        dispatchPort.Calls.Select(x => x.envelope.Propagation.CorrelationId).Should().Contain($"{ServiceKeys.Build(identity)}:policy:policy-a");
        dispatchPort.Calls.Select(x => x.envelope.Propagation.CorrelationId).Should().Contain(ServiceKeys.Build(identity));
    }

    [Fact]
    public async Task ServiceGovernanceCommandApplicationService_ShouldRejectMissingDefinitionAndProvisionerFailures()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var bindingSpec = CreateServiceBindingSpec(identity, "binding-a", ServiceBindingKind.Service);

        var missingDefinitionService = new ServiceGovernanceCommandApplicationService(
            new RecordingDispatchPort(),
            new RecordingCatalogQueryReader(),
            new RecordingGovernanceCommandTargetProvisioner(),
            new RecordingGovernanceProjectionPort(),
            new RecordingGovernanceProjectionPort(),
            new RecordingGovernanceProjectionPort());

        var missingDefinition = () => missingDefinitionService.CreateBindingAsync(new CreateServiceBindingCommand
        {
            Spec = bindingSpec.Clone(),
        });
        await missingDefinition.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service definition*was not found*");

        var targetProvisioner = new RecordingGovernanceCommandTargetProvisioner
        {
            EndpointException = new InvalidOperationException("endpoint target failed"),
        };
        var failingService = new ServiceGovernanceCommandApplicationService(
            new RecordingDispatchPort(),
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            targetProvisioner,
            new RecordingGovernanceProjectionPort(),
            new RecordingGovernanceProjectionPort(),
            new RecordingGovernanceProjectionPort());

        var failingProvision = () => failingService.CreateEndpointCatalogAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(identity, "invoke"),
        });
        await failingProvision.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("endpoint target failed");
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

    private static ServicePolicySnapshot CreatePolicySnapshot(string policyId) =>
        new(
            policyId,
            policyId,
            [],
            [],
            false,
            false);

    private static ServiceBindingSpec CreateServiceBindingSpec(
        ServiceIdentity identity,
        string bindingId,
        ServiceBindingKind kind)
    {
        var spec = new ServiceBindingSpec
        {
            Identity = identity.Clone(),
            BindingId = bindingId,
            DisplayName = bindingId,
            BindingKind = kind,
        };

        switch (kind)
        {
            case ServiceBindingKind.Service:
                spec.ServiceRef = new BoundServiceRef
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                    EndpointId = "run",
                };
                break;
            case ServiceBindingKind.Connector:
                spec.ConnectorRef = new BoundConnectorRef
                {
                    ConnectorType = "mcp",
                    ConnectorId = "connector-1",
                };
                break;
            case ServiceBindingKind.Secret:
                spec.SecretRef = new BoundSecretRef
                {
                    SecretName = "secret-1",
                };
                break;
        }

        return spec;
    }

    private static ServiceEndpointCatalogSpec CreateEndpointCatalogSpec(ServiceIdentity identity, string endpointId) =>
        new()
        {
            Identity = identity.Clone(),
            Endpoints =
            {
                new ServiceEndpointExposureSpec
                {
                    EndpointId = endpointId,
                    DisplayName = endpointId,
                    Kind = ServiceEndpointKind.Command,
                    RequestTypeUrl = "type.googleapis.com/demo.Invoke",
                    ExposureKind = ServiceEndpointExposureKind.Public,
                },
            },
        };

    private static ServicePolicySpec CreatePolicySpec(ServiceIdentity identity, string policyId) =>
        new()
        {
            Identity = identity.Clone(),
            PolicyId = policyId,
            DisplayName = policyId,
        };

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

    private sealed class RecordingBindingQueryReader : IServiceBindingQueryReader
    {
        public ServiceBindingCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceBindingCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }

    private sealed class RecordingEndpointCatalogQueryReader : IServiceEndpointCatalogQueryReader
    {
        public ServiceEndpointCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceEndpointCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }

    private sealed class RecordingPolicyQueryReader : IServicePolicyQueryReader
    {
        public ServicePolicyCatalogSnapshot? GetResult { get; init; }

        public Task<ServicePolicyCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }

    private sealed class RecordingInvokeAdmissionEvaluator : IInvokeAdmissionEvaluator
    {
        public InvokeAdmissionDecision Decision { get; init; } = new() { Allowed = true };

        public InvokeAdmissionRequest? LastRequest { get; private set; }

        public Task<InvokeAdmissionDecision> EvaluateAsync(InvokeAdmissionRequest request, CancellationToken ct = default)
        {
            LastRequest = request.Clone();
            return Task.FromResult(Decision.Clone());
        }
    }

    private sealed class RecordingGovernanceCommandTargetProvisioner : IServiceGovernanceCommandTargetProvisioner
    {
        public List<ServiceIdentity> BindingRequests { get; } = [];

        public List<ServiceIdentity> EndpointRequests { get; } = [];

        public List<ServiceIdentity> PolicyRequests { get; } = [];

        public Exception? EndpointException { get; init; }

        public Task<string> EnsureBindingCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            BindingRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.BindingCatalog(identity));
        }

        public Task<string> EnsureEndpointCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            EndpointRequests.Add(identity.Clone());
            if (EndpointException != null)
                throw EndpointException;

            return Task.FromResult(ServiceActorIds.EndpointCatalog(identity));
        }

        public Task<string> EnsurePolicyCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            PolicyRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.PolicyCatalog(identity));
        }
    }

    private sealed class RecordingGovernanceProjectionPort
        : IServiceBindingProjectionPort, IServiceEndpointCatalogProjectionPort, IServicePolicyProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }
}
