using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class GovernanceGAgentTests
{
    [Fact]
    public async Task ServiceConfigurationGAgent_ShouldPersistCombinedGovernanceState_AndReplay()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Configuration(identity);
        var eventStore = new InMemoryEventStore();
        var binding = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service);
        var updatedBinding = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Connector);
        updatedBinding.DisplayName = "Updated Binding";
        updatedBinding.ConnectorRef = new BoundConnectorRef
        {
            ConnectorType = "mcp",
            ConnectorId = "connector-1",
        };
        var createdCatalog = CreateEndpointCatalogSpec(identity, "invoke", ServiceEndpointKind.Command);
        var updatedCatalog = CreateEndpointCatalogSpec(identity, "chat", ServiceEndpointKind.Chat);
        var policy = CreatePolicySpec(identity, "policy-a");
        var updatedPolicy = CreatePolicySpec(identity, "policy-a");
        updatedPolicy.ActivationRequiredBindingIds.Add("binding-a");
        updatedPolicy.InvokeRequiresActiveDeployment = true;

        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceConfigurationGAgent, ServiceConfigurationState>(
            eventStore,
            actorId,
            () => new ServiceConfigurationGAgent());
        await agent.ActivateAsync();

        await agent.HandleCreateAsync(new CreateServiceBindingCommand { Spec = binding.Clone() });
        await agent.HandleUpdateAsync(new UpdateServiceBindingCommand { Spec = updatedBinding.Clone() });
        await agent.HandleCreateAsync(new CreateServiceEndpointCatalogCommand { Spec = createdCatalog.Clone() });
        await agent.HandleUpdateAsync(new UpdateServiceEndpointCatalogCommand { Spec = updatedCatalog.Clone() });
        await agent.HandleCreateAsync(new CreateServicePolicyCommand { Spec = policy.Clone() });
        await agent.HandleUpdateAsync(new UpdateServicePolicyCommand { Spec = updatedPolicy.Clone() });
        await agent.HandleRetireAsync(new RetireServiceBindingCommand { Identity = identity.Clone(), BindingId = "binding-a" });
        await agent.HandleRetireAsync(new RetireServicePolicyCommand { Identity = identity.Clone(), PolicyId = "policy-a" });

        agent.State.Identity.ServiceId.Should().Be(identity.ServiceId);
        agent.State.Bindings["binding-a"].Spec.DisplayName.Should().Be("Updated Binding");
        agent.State.Bindings["binding-a"].Spec.BindingKind.Should().Be(ServiceBindingKind.Connector);
        agent.State.Bindings["binding-a"].Retired.Should().BeTrue();
        agent.State.EndpointCatalog.Should().NotBeNull();
        agent.State.EndpointCatalog!.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat" && x.Kind == ServiceEndpointKind.Chat);
        agent.State.Policies["policy-a"].Spec.InvokeRequiresActiveDeployment.Should().BeTrue();
        agent.State.Policies["policy-a"].Retired.Should().BeTrue();
        agent.State.LastAppliedEventVersion.Should().Be(8);

        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceConfigurationGAgent, ServiceConfigurationState>(
            eventStore,
            actorId,
            () => new ServiceConfigurationGAgent());
        await replayed.ActivateAsync();

        replayed.State.Bindings["binding-a"].Retired.Should().BeTrue();
        replayed.State.EndpointCatalog!.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        replayed.State.Policies["policy-a"].Retired.Should().BeTrue();
    }

    [Fact]
    public async Task ServiceConfigurationGAgent_ShouldRejectInvalidBindingOperations()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceConfigurationGAgent, ServiceConfigurationState>(
            new InMemoryEventStore(),
            ServiceActorIds.Configuration(identity),
            () => new ServiceConfigurationGAgent());
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service),
        });

        Func<Task> duplicateCreate = async () => await agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service),
        });
        Func<Task> missingUpdate = async () => await agent.HandleUpdateAsync(new UpdateServiceBindingCommand
        {
            Spec = CreateBindingSpec(identity, "missing", ServiceBindingKind.Service),
        });
        Func<Task> missingRetire = async () => await agent.HandleRetireAsync(new RetireServiceBindingCommand
        {
            Identity = identity.Clone(),
            BindingId = "missing",
        });
        Func<Task> wrongTarget = async () => await agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = new ServiceBindingSpec
            {
                Identity = identity.Clone(),
                BindingId = "binding-b",
                BindingKind = ServiceBindingKind.Connector,
                ServiceRef = new BoundServiceRef
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                    EndpointId = "run",
                },
            },
        });
        Func<Task> mismatchedIdentity = async () => await agent.HandleUpdateAsync(new UpdateServiceBindingCommand
        {
            Spec = CreateBindingSpec(GAgentServiceTestKit.CreateIdentity(serviceId: "other"), "binding-a", ServiceBindingKind.Service),
        });

        await duplicateCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        await missingUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was not found*");
        await missingRetire.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was not found*");
        await wrongTarget.Should().ThrowAsync<InvalidOperationException>().WithMessage("*connector binding requires connector_ref target*");
        await mismatchedIdentity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is bound to*");
    }

    [Fact]
    public async Task ServiceConfigurationGAgent_ShouldRejectInvalidEndpointAndPolicyOperations()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceConfigurationGAgent, ServiceConfigurationState>(
            new InMemoryEventStore(),
            ServiceActorIds.Configuration(identity),
            () => new ServiceConfigurationGAgent());
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(identity, "invoke"),
        });
        await agent.HandleCreateAsync(new CreateServicePolicyCommand
        {
            Spec = CreatePolicySpec(identity, "policy-a"),
        });

        Func<Task> emptyCatalog = async () => await agent.HandleCreateAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = new ServiceEndpointCatalogSpec
            {
                Identity = identity.Clone(),
            },
        });
        Func<Task> duplicateCatalog = async () => await agent.HandleCreateAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(identity, "invoke"),
        });
        Func<Task> mismatchedCatalog = async () => await agent.HandleUpdateAsync(new UpdateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(GAgentServiceTestKit.CreateIdentity(serviceId: "other"), "invoke"),
        });
        Func<Task> blankPolicy = async () => await agent.HandleCreateAsync(new CreateServicePolicyCommand
        {
            Spec = new ServicePolicySpec
            {
                Identity = identity.Clone(),
                PolicyId = " ",
            },
        });
        Func<Task> missingPolicyUpdate = async () => await agent.HandleUpdateAsync(new UpdateServicePolicyCommand
        {
            Spec = CreatePolicySpec(identity, "missing"),
        });
        Func<Task> blankPolicyRetire = async () => await agent.HandleRetireAsync(new RetireServicePolicyCommand
        {
            Identity = identity.Clone(),
            PolicyId = " ",
        });

        await emptyCatalog.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least one endpoint*");
        await duplicateCatalog.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        await mismatchedCatalog.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is bound to*");
        await blankPolicy.Should().ThrowAsync<InvalidOperationException>().WithMessage("*policy_id is required*");
        await missingPolicyUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was not found*");
        await blankPolicyRetire.Should().ThrowAsync<InvalidOperationException>().WithMessage("*policy_id is required*");
    }

    [Fact]
    public async Task ServiceConfigurationGAgent_ShouldRejectEndpointCatalogUpdateBeforeCatalogExists()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceConfigurationGAgent, ServiceConfigurationState>(
            new InMemoryEventStore(),
            ServiceActorIds.Configuration(identity),
            () => new ServiceConfigurationGAgent());
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service),
        });

        Func<Task> updateMissingCatalog = async () => await agent.HandleUpdateAsync(new UpdateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(identity, "invoke"),
        });

        await updateMissingCatalog.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not exist*");
    }

    [Fact]
    public async Task ServiceConfigurationGAgent_ShouldImportLegacyConfigurationStateAndRemainIdempotent()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Configuration(identity);
        var eventStore = new InMemoryEventStore();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceConfigurationGAgent, ServiceConfigurationState>(
            eventStore,
            actorId,
            () => new ServiceConfigurationGAgent());
        await agent.ActivateAsync();

        var importState = new ServiceConfigurationState
        {
            Identity = identity.Clone(),
            EndpointCatalog = CreateEndpointCatalogSpec(identity, "invoke"),
            Policies =
            {
                ["policy-a"] = new ServicePolicyRecordState
                {
                    Spec = CreatePolicySpec(identity, "policy-a"),
                    Retired = false,
                },
            },
            Bindings =
            {
                ["binding-a"] = new ServiceBindingRecordState
                {
                    Spec = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service),
                    Retired = false,
                },
            },
        };

        await agent.HandleImportAsync(new ImportLegacyServiceConfigurationCommand
        {
            State = importState.Clone(),
        });
        await agent.HandleImportAsync(new ImportLegacyServiceConfigurationCommand
        {
            State = importState.Clone(),
        });

        agent.State.Identity.Should().BeEquivalentTo(identity);
        agent.State.Bindings.Should().ContainKey("binding-a");
        agent.State.EndpointCatalog!.Endpoints.Should().ContainSingle(x => x.EndpointId == "invoke");
        agent.State.Policies.Should().ContainKey("policy-a");
        agent.State.LastAppliedEventVersion.Should().Be(1);

        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceConfigurationGAgent, ServiceConfigurationState>(
            eventStore,
            actorId,
            () => new ServiceConfigurationGAgent());
        await replayed.ActivateAsync();

        replayed.State.Bindings.Should().ContainKey("binding-a");
        replayed.State.EndpointCatalog!.Endpoints.Should().ContainSingle(x => x.EndpointId == "invoke");
        replayed.State.Policies.Should().ContainKey("policy-a");
        (await eventStore.GetVersionAsync(actorId)).Should().Be(1);
    }

    [Fact]
    public async Task ServiceConfigurationGAgent_ShouldRejectAdditionalBindingTargetMismatches_AndInvalidImportedCatalog()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceConfigurationGAgent, ServiceConfigurationState>(
            new InMemoryEventStore(),
            ServiceActorIds.Configuration(identity),
            () => new ServiceConfigurationGAgent());
        await agent.ActivateAsync();

        Func<Task> wrongServiceTarget = async () => await agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = new ServiceBindingSpec
            {
                Identity = identity.Clone(),
                BindingId = "binding-service",
                BindingKind = ServiceBindingKind.Service,
                SecretRef = new BoundSecretRef
                {
                    SecretName = "secret",
                },
            },
        });

        Func<Task> wrongSecretTarget = async () => await agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = new ServiceBindingSpec
            {
                Identity = identity.Clone(),
                BindingId = "binding-secret",
                BindingKind = ServiceBindingKind.Secret,
                ConnectorRef = new BoundConnectorRef
                {
                    ConnectorType = "mcp",
                    ConnectorId = "connector-1",
                },
            },
        });

        Func<Task> invalidImportedCatalog = async () => await agent.HandleImportAsync(new ImportLegacyServiceConfigurationCommand
        {
            State = new ServiceConfigurationState
            {
                Identity = identity.Clone(),
                EndpointCatalog = new ServiceEndpointCatalogSpec
                {
                    Identity = identity.Clone(),
                },
            },
        });

        await wrongServiceTarget.Should().ThrowAsync<InvalidOperationException>().WithMessage("*service binding requires service_ref target*");
        await wrongSecretTarget.Should().ThrowAsync<InvalidOperationException>().WithMessage("*secret binding requires secret_ref target*");
        await invalidImportedCatalog.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least one endpoint*");
    }

    private static ServiceBindingSpec CreateBindingSpec(ServiceIdentity identity, string bindingId, ServiceBindingKind kind)
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

    private static ServiceEndpointCatalogSpec CreateEndpointCatalogSpec(
        ServiceIdentity identity,
        string endpointId,
        ServiceEndpointKind kind = ServiceEndpointKind.Command)
    {
        return new ServiceEndpointCatalogSpec
        {
            Identity = identity.Clone(),
            Endpoints =
            {
                new ServiceEndpointExposureSpec
                {
                    EndpointId = endpointId,
                    DisplayName = endpointId,
                    Kind = kind,
                    RequestTypeUrl = "type.googleapis.com/demo.Invoke",
                    ExposureKind = ServiceEndpointExposureKind.Public,
                },
            },
        };
    }

    private static ServicePolicySpec CreatePolicySpec(ServiceIdentity identity, string policyId)
    {
        return new ServicePolicySpec
        {
            Identity = identity.Clone(),
            PolicyId = policyId,
            DisplayName = policyId,
        };
    }
}
