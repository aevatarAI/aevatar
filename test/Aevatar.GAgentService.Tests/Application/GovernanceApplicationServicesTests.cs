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
    public async Task ActivationCapabilityViewAssembler_ShouldComposeConfigurationAndArtifactFallback()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
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
            new RecordingConfigurationQueryReader
            {
                GetResult = CreateConfigurationSnapshot(
                    identity,
                    bindings:
                    [
                        new ServiceBindingSnapshot(
                            "binding-a",
                            "Binding A",
                            ServiceBindingKind.Service,
                            ["policy-binding"],
                            false,
                            new BoundServiceReferenceSnapshot(
                                GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                                "run"),
                            null,
                            null),
                        new ServiceBindingSnapshot(
                            "binding-retired",
                            "Retired",
                            ServiceBindingKind.Secret,
                            [],
                            true,
                            null,
                            null,
                            new BoundSecretReferenceSnapshot("secret")),
                    ],
                    endpoints:
                    [
                        new ServiceEndpointExposureSnapshot(
                            "published",
                            "Published",
                            ServiceEndpointKind.Command,
                            "type.googleapis.com/demo.Published",
                            string.Empty,
                            "published",
                            ServiceEndpointExposureKind.Public,
                            ["policy-endpoint"]),
                    ],
                    policies:
                    [
                        CreatePolicySnapshot("policy-definition"),
                        CreatePolicySnapshot("policy-binding"),
                        CreatePolicySnapshot("policy-endpoint"),
                    ]),
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
            new RecordingConfigurationQueryReader(),
            new ConfiguredServiceRevisionArtifactStore());

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
            new RecordingConfigurationQueryReader
            {
                GetResult = CreateConfigurationSnapshot(identity),
            },
            new ConfiguredServiceRevisionArtifactStore());

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
            new RecordingConfigurationQueryReader
            {
                GetResult = CreateConfigurationSnapshot(
                    identity,
                    endpoints:
                    [
                        new ServiceEndpointExposureSnapshot(
                            "invoke",
                            "Invoke",
                            ServiceEndpointKind.Command,
                            "type.googleapis.com/demo.Invoke",
                            string.Empty,
                            "invoke",
                            ServiceEndpointExposureKind.Public,
                            ["policy-endpoint"]),
                    ],
                    policies:
                    [
                        CreatePolicySnapshot("policy-definition"),
                        CreatePolicySnapshot("policy-endpoint"),
                    ]),
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
    public async Task InvokeAdmissionService_ShouldRejectMissingDefinitionMissingConfigurationAndRejectedDecision()
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
            new RecordingConfigurationQueryReader(),
            evaluator);
        var missingDefinition = () => missingDefinitionService.AuthorizeAsync(
            ServiceKeys.Build(identity),
            "dep-1",
            artifact,
            artifact.Endpoints[0],
            request);
        await missingDefinition.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service definition*was not found*");

        var missingConfigurationService = new InvokeAdmissionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingConfigurationQueryReader(),
            evaluator);
        var missingConfiguration = () => missingConfigurationService.AuthorizeAsync(
            ServiceKeys.Build(identity),
            "dep-1",
            artifact,
            artifact.Endpoints[0],
            request);
        await missingConfiguration.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Endpoint catalog*was not found*");

        var rejectedService = new InvokeAdmissionService(
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingConfigurationQueryReader
            {
                GetResult = CreateConfigurationSnapshot(
                    identity,
                    endpoints:
                    [
                        new ServiceEndpointExposureSnapshot(
                            "invoke",
                            "Invoke",
                            ServiceEndpointKind.Command,
                            "type.googleapis.com/demo.Invoke",
                            string.Empty,
                            "invoke",
                            ServiceEndpointExposureKind.Public,
                            []),
                    ]),
            },
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
    public async Task ServiceGovernanceCommandApplicationService_ShouldEnsureTargetProjectionAndDispatchCommands()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var targetProvisioner = new RecordingGovernanceCommandTargetProvisioner();
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingGovernanceProjectionPort();
        var service = new ServiceGovernanceCommandApplicationService(
            dispatchPort,
            targetProvisioner,
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

        targetProvisioner.ConfigurationRequests.Should().HaveCount(8);
        projectionPort.ActorIds.Should().HaveCount(8);
        dispatchPort.Calls.Should().HaveCount(8);
        dispatchPort.Calls.Select(x => x.actorId).Should().OnlyContain(x => x == ServiceActorIds.Configuration(identity));
        dispatchPort.Calls.Select(x => x.envelope.Propagation.CorrelationId).Should().Contain($"{ServiceKeys.Build(identity)}:binding:binding-a");
        dispatchPort.Calls.Select(x => x.envelope.Propagation.CorrelationId).Should().Contain($"{ServiceKeys.Build(identity)}:policy:policy-a");
        dispatchPort.Calls.Select(x => x.envelope.Propagation.CorrelationId).Should().Contain(ServiceKeys.Build(identity));
    }

    [Fact]
    public async Task ServiceGovernanceCommandApplicationService_ShouldSurfaceProvisionerFailures()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var targetProvisioner = new RecordingGovernanceCommandTargetProvisioner
        {
            ConfigurationException = new InvalidOperationException("configuration target failed"),
        };
        var failingService = new ServiceGovernanceCommandApplicationService(
            new RecordingDispatchPort(),
            targetProvisioner,
            new RecordingGovernanceProjectionPort());

        var failingProvision = () => failingService.CreateEndpointCatalogAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(identity, "invoke"),
        });
        await failingProvision.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("configuration target failed");
    }

    [Fact]
    public async Task ServiceGovernanceQueryApplicationService_ShouldProjectBindingsEndpointsAndPoliciesFromConfiguration()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var configuration = CreateConfigurationSnapshot(
            identity,
            bindings:
            [
                new ServiceBindingSnapshot(
                    "binding-a",
                    "Binding A",
                    ServiceBindingKind.Service,
                    [],
                    false,
                    new BoundServiceReferenceSnapshot(GAgentServiceTestKit.CreateIdentity(serviceId: "dep"), "run"),
                    null,
                    null),
            ],
            endpoints:
            [
                new ServiceEndpointExposureSnapshot(
                    "invoke",
                    "Invoke",
                    ServiceEndpointKind.Command,
                    "type.googleapis.com/demo.Invoke",
                    string.Empty,
                    "invoke",
                    ServiceEndpointExposureKind.Public,
                    []),
            ],
            policies:
            [
                CreatePolicySnapshot("policy-a"),
            ]);
        var service = new ServiceGovernanceQueryApplicationService(new RecordingConfigurationQueryReader
        {
            GetResult = configuration,
        });

        var bindings = await service.GetBindingsAsync(identity);
        var endpoints = await service.GetEndpointCatalogAsync(identity);
        var policies = await service.GetPoliciesAsync(identity);

        bindings.Should().NotBeNull();
        bindings!.Bindings.Should().ContainSingle(x => x.BindingId == "binding-a");
        endpoints.Should().NotBeNull();
        endpoints!.Endpoints.Should().ContainSingle(x => x.EndpointId == "invoke");
        policies.Should().NotBeNull();
        policies!.Policies.Should().ContainSingle(x => x.PolicyId == "policy-a");
    }

    [Fact]
    public async Task ServiceGovernanceQueryApplicationService_ShouldReturnNullSnapshots_WhenConfigurationMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = new ServiceGovernanceQueryApplicationService(new RecordingConfigurationQueryReader());

        (await service.GetBindingsAsync(identity)).Should().BeNull();
        (await service.GetEndpointCatalogAsync(identity)).Should().BeNull();
        (await service.GetPoliciesAsync(identity)).Should().BeNull();
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

    private static ServiceConfigurationSnapshot CreateConfigurationSnapshot(
        ServiceIdentity identity,
        IReadOnlyList<ServiceBindingSnapshot>? bindings = null,
        IReadOnlyList<ServiceEndpointExposureSnapshot>? endpoints = null,
        IReadOnlyList<ServicePolicySnapshot>? policies = null) =>
        new(
            ServiceKeys.Build(identity),
            identity.Clone(),
            bindings ?? [],
            endpoints ?? [],
            policies ?? [],
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

    private sealed class RecordingConfigurationQueryReader : IServiceConfigurationQueryReader
    {
        public ServiceConfigurationSnapshot? GetResult { get; init; }

        public Task<ServiceConfigurationSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
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
        public List<ServiceIdentity> ConfigurationRequests { get; } = [];

        public Exception? ConfigurationException { get; init; }

        public Task<string> EnsureConfigurationTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            ConfigurationRequests.Add(identity.Clone());
            if (ConfigurationException != null)
                throw ConfigurationException;

            return Task.FromResult(ServiceActorIds.Configuration(identity));
        }
    }

    private sealed class RecordingGovernanceProjectionPort : IServiceConfigurationProjectionPort
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
