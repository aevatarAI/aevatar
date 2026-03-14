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
    public async Task ServiceBindingManagerGAgent_ShouldPersistUpdateRetireAndReplay()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.BindingCatalog(identity);
        var eventStore = new InMemoryEventStore();
        var binding = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service);
        var updated = binding.Clone();
        updated.DisplayName = "Updated";
        updated.PolicyIds.Add("policy-a");
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceBindingManagerGAgent, ServiceBindingCatalogState>(
            eventStore,
            actorId,
            () => new ServiceBindingManagerGAgent());
        await agent.ActivateAsync();

        await agent.HandleCreateAsync(new CreateServiceBindingCommand { Spec = binding.Clone() });
        await agent.HandleUpdateAsync(new UpdateServiceBindingCommand { Spec = updated.Clone() });
        await agent.HandleRetireAsync(new RetireServiceBindingCommand { Identity = identity.Clone(), BindingId = "binding-a" });

        agent.State.Bindings["binding-a"].Spec.DisplayName.Should().Be("Updated");
        agent.State.Bindings["binding-a"].Retired.Should().BeTrue();
        agent.State.LastAppliedEventVersion.Should().Be(3);

        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceBindingManagerGAgent, ServiceBindingCatalogState>(
            eventStore,
            actorId,
            () => new ServiceBindingManagerGAgent());
        await replayed.ActivateAsync();
        replayed.State.Bindings["binding-a"].Retired.Should().BeTrue();
    }

    [Fact]
    public async Task ServiceBindingManagerGAgent_ShouldRejectInvalidOperations()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceBindingManagerGAgent, ServiceBindingCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.BindingCatalog(identity),
            () => new ServiceBindingManagerGAgent());
        await agent.ActivateAsync();
        var binding = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service);

        await agent.HandleCreateAsync(new CreateServiceBindingCommand { Spec = binding.Clone() });

        var duplicateCreate = () => agent.HandleCreateAsync(new CreateServiceBindingCommand { Spec = binding.Clone() });
        var missingUpdate = () => agent.HandleUpdateAsync(new UpdateServiceBindingCommand
        {
            Spec = CreateBindingSpec(identity, "missing", ServiceBindingKind.Service),
        });
        var missingRetire = () => agent.HandleRetireAsync(new RetireServiceBindingCommand
        {
            Identity = identity.Clone(),
            BindingId = "missing",
        });
        var wrongTarget = () => agent.HandleCreateAsync(new CreateServiceBindingCommand
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

        await duplicateCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        await missingUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was not found*");
        await missingRetire.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was not found*");
        await wrongTarget.Should().ThrowAsync<InvalidOperationException>().WithMessage("*connector binding requires connector_ref target*");
    }

    [Fact]
    public async Task ServiceBindingManagerGAgent_ShouldRejectBlankIds_UnspecifiedKinds_AndMismatchedCatalogIdentity()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var otherIdentity = GAgentServiceTestKit.CreateIdentity(serviceId: "other");
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceBindingManagerGAgent, ServiceBindingCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.BindingCatalog(identity),
            () => new ServiceBindingManagerGAgent());
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service),
        });

        var blankBindingId = () => agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = new ServiceBindingSpec
            {
                Identity = identity.Clone(),
                BindingId = " ",
                BindingKind = ServiceBindingKind.Service,
                ServiceRef = new BoundServiceRef
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                    EndpointId = "run",
                },
            },
        });
        var unspecifiedKind = () => agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = new ServiceBindingSpec
            {
                Identity = identity.Clone(),
                BindingId = "binding-b",
            },
        });
        var wrongSecretTarget = () => agent.HandleCreateAsync(new CreateServiceBindingCommand
        {
            Spec = new ServiceBindingSpec
            {
                Identity = identity.Clone(),
                BindingId = "binding-c",
                BindingKind = ServiceBindingKind.Secret,
                ServiceRef = new BoundServiceRef
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                    EndpointId = "run",
                },
            },
        });
        var mismatchedUpdate = () => agent.HandleUpdateAsync(new UpdateServiceBindingCommand
        {
            Spec = CreateBindingSpec(otherIdentity, "binding-a", ServiceBindingKind.Service),
        });
        var blankRetire = () => agent.HandleRetireAsync(new RetireServiceBindingCommand
        {
            Identity = identity.Clone(),
            BindingId = " ",
        });

        await blankBindingId.Should().ThrowAsync<InvalidOperationException>().WithMessage("*binding_id is required*");
        await unspecifiedKind.Should().ThrowAsync<InvalidOperationException>().WithMessage("*binding_kind is required*");
        await wrongSecretTarget.Should().ThrowAsync<InvalidOperationException>().WithMessage("*secret binding requires secret_ref target*");
        await mismatchedUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is bound to*");
        await blankRetire.Should().ThrowAsync<InvalidOperationException>().WithMessage("*binding_id is required*");
    }

    [Fact]
    public async Task ServiceEndpointCatalogGAgent_ShouldPersistUpdateAndReplay()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.EndpointCatalog(identity);
        var eventStore = new InMemoryEventStore();
        var created = CreateEndpointCatalogSpec(identity, "invoke");
        var updated = CreateEndpointCatalogSpec(identity, "chat", ServiceEndpointKind.Chat);
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceEndpointCatalogGAgent, ServiceEndpointCatalogState>(
            eventStore,
            actorId,
            () => new ServiceEndpointCatalogGAgent());
        await agent.ActivateAsync();

        await agent.HandleCreateAsync(new CreateServiceEndpointCatalogCommand { Spec = created.Clone() });
        await agent.HandleUpdateAsync(new UpdateServiceEndpointCatalogCommand { Spec = updated.Clone() });

        agent.State.Spec!.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat" && x.Kind == ServiceEndpointKind.Chat);
        agent.State.LastAppliedEventVersion.Should().Be(2);

        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceEndpointCatalogGAgent, ServiceEndpointCatalogState>(
            eventStore,
            actorId,
            () => new ServiceEndpointCatalogGAgent());
        await replayed.ActivateAsync();
        replayed.State.Spec!.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
    }

    [Fact]
    public async Task ServiceEndpointCatalogGAgent_ShouldRejectInvalidOperations()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceEndpointCatalogGAgent, ServiceEndpointCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.EndpointCatalog(identity),
            () => new ServiceEndpointCatalogGAgent());
        await agent.ActivateAsync();

        var emptyCreate = () => agent.HandleCreateAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = new ServiceEndpointCatalogSpec
            {
                Identity = identity.Clone(),
            },
        });

        await emptyCreate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one endpoint*");

        await agent.HandleCreateAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(identity, "invoke"),
        });

        var duplicateCreate = () => agent.HandleCreateAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(identity, "invoke"),
        });
        var mismatchedIdentity = () => agent.HandleUpdateAsync(new UpdateServiceEndpointCatalogCommand
        {
            Spec = CreateEndpointCatalogSpec(GAgentServiceTestKit.CreateIdentity(serviceId: "other"), "invoke"),
        });

        await duplicateCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        await mismatchedIdentity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is bound to*");
    }

    [Fact]
    public async Task ServicePolicyGAgent_ShouldPersistUpdateRetireAndReplay()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.PolicyCatalog(identity);
        var eventStore = new InMemoryEventStore();
        var created = CreatePolicySpec(identity, "policy-a");
        var updated = created.Clone();
        updated.InvokeRequiresActiveDeployment = true;
        updated.ActivationRequiredBindingIds.Add("binding-a");
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServicePolicyGAgent, ServicePolicyCatalogState>(
            eventStore,
            actorId,
            () => new ServicePolicyGAgent());
        await agent.ActivateAsync();

        await agent.HandleCreateAsync(new CreateServicePolicyCommand { Spec = created.Clone() });
        await agent.HandleUpdateAsync(new UpdateServicePolicyCommand { Spec = updated.Clone() });
        await agent.HandleRetireAsync(new RetireServicePolicyCommand { Identity = identity.Clone(), PolicyId = "policy-a" });

        agent.State.Policies["policy-a"].Spec.InvokeRequiresActiveDeployment.Should().BeTrue();
        agent.State.Policies["policy-a"].Retired.Should().BeTrue();

        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServicePolicyGAgent, ServicePolicyCatalogState>(
            eventStore,
            actorId,
            () => new ServicePolicyGAgent());
        await replayed.ActivateAsync();
        replayed.State.Policies["policy-a"].Retired.Should().BeTrue();
    }

    [Fact]
    public async Task ServicePolicyGAgent_ShouldRejectInvalidOperations()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServicePolicyGAgent, ServicePolicyCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.PolicyCatalog(identity),
            () => new ServicePolicyGAgent());
        await agent.ActivateAsync();

        var blankPolicy = () => agent.HandleCreateAsync(new CreateServicePolicyCommand
        {
            Spec = new ServicePolicySpec
            {
                Identity = identity.Clone(),
                PolicyId = " ",
            },
        });
        await blankPolicy.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*policy_id is required*");

        await agent.HandleCreateAsync(new CreateServicePolicyCommand
        {
            Spec = CreatePolicySpec(identity, "policy-a"),
        });

        var duplicateCreate = () => agent.HandleCreateAsync(new CreateServicePolicyCommand
        {
            Spec = CreatePolicySpec(identity, "policy-a"),
        });
        var missingUpdate = () => agent.HandleUpdateAsync(new UpdateServicePolicyCommand
        {
            Spec = CreatePolicySpec(identity, "missing"),
        });
        var missingRetire = () => agent.HandleRetireAsync(new RetireServicePolicyCommand
        {
            Identity = identity.Clone(),
            PolicyId = "missing",
        });

        await duplicateCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
        await missingUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was not found*");
        await missingRetire.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was not found*");
    }

    [Fact]
    public async Task ServicePolicyGAgent_ShouldRejectMismatchedIdentity_AndBlankRetireId()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var otherIdentity = GAgentServiceTestKit.CreateIdentity(serviceId: "other");
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServicePolicyGAgent, ServicePolicyCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.PolicyCatalog(identity),
            () => new ServicePolicyGAgent());
        await agent.ActivateAsync();
        await agent.HandleCreateAsync(new CreateServicePolicyCommand
        {
            Spec = CreatePolicySpec(identity, "policy-a"),
        });

        var mismatchedUpdate = () => agent.HandleUpdateAsync(new UpdateServicePolicyCommand
        {
            Spec = CreatePolicySpec(otherIdentity, "policy-a"),
        });
        var blankRetire = () => agent.HandleRetireAsync(new RetireServicePolicyCommand
        {
            Identity = identity.Clone(),
            PolicyId = " ",
        });

        await mismatchedUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is bound to*");
        await blankRetire.Should().ThrowAsync<InvalidOperationException>().WithMessage("*policy_id is required*");
    }

    private static ServiceBindingSpec CreateBindingSpec(ServiceIdentity identity, string bindingId, ServiceBindingKind kind)
    {
        return new ServiceBindingSpec
        {
            Identity = identity.Clone(),
            BindingId = bindingId,
            DisplayName = bindingId,
            BindingKind = kind,
            ServiceRef = new BoundServiceRef
            {
                Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                EndpointId = "run",
            },
        };
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
