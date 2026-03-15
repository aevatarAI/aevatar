using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.Projectors;
using Aevatar.GAgentService.Governance.Projection.Queries;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceConfigurationProjectorAndQueryTests
{
    [Fact]
    public async Task ProjectorAndQueryReader_ShouldReplaceReadModelWhenLegacyConfigurationIsImported()
    {
        var store = new RecordingDocumentStore<ServiceConfigurationReadModel>(x => x.Id);
        var projector = new ServiceConfigurationProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceConfigurationQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceConfigurationProjectionContext
        {
            ProjectionId = "service-configuration:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        var importedState = new ServiceConfigurationState
        {
            Identity = identity.Clone(),
            Bindings =
            {
                ["binding-a"] = new ServiceBindingRecordState
                {
                    Spec = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Service),
                    Retired = true,
                },
            },
            EndpointCatalog = new ServiceEndpointCatalogSpec
            {
                Identity = identity.Clone(),
                Endpoints =
                {
                    CreateEndpointExposure("invoke", ServiceEndpointKind.Command, ServiceEndpointExposureKind.Public),
                },
            },
            Policies =
            {
                ["policy-a"] = new ServicePolicyRecordState
                {
                    Spec = CreatePolicySpec(identity, "policy-a"),
                    Retired = false,
                },
            },
        };

        await projector.ProjectAsync(context, BuildEnvelope(new LegacyServiceConfigurationImportedEvent
        {
            State = importedState.Clone(),
        }));

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.Bindings.Should().ContainSingle(x => x.BindingId == "binding-a" && x.Retired);
        snapshot.Endpoints.Should().ContainSingle(x => x.EndpointId == "invoke");
        snapshot.Policies.Should().ContainSingle(x => x.PolicyId == "policy-a");
    }

    [Fact]
    public async Task ProjectorAndQueryReader_ShouldProjectGovernanceLifecycleIntoSingleConfigurationSnapshot()
    {
        var store = new RecordingDocumentStore<ServiceConfigurationReadModel>(x => x.Id);
        var projector = new ServiceConfigurationProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var reader = new ServiceConfigurationQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceConfigurationProjectionContext
        {
            ProjectionId = "service-configuration:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        var createdBinding = CreateBindingSpec(identity, "binding-b", ServiceBindingKind.Service);
        createdBinding.PolicyIds.Add("policy-a");
        var updatedBinding = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Secret);
        updatedBinding.DisplayName = "Updated";
        updatedBinding.SecretRef = new BoundSecretRef { SecretName = "secret-a" };
        var createdEndpoints = new ServiceEndpointCatalogSpec
        {
            Identity = identity.Clone(),
            Endpoints =
            {
                CreateEndpointExposure("z-chat", ServiceEndpointKind.Chat, ServiceEndpointExposureKind.Public),
                CreateEndpointExposure("a-command", ServiceEndpointKind.Command, ServiceEndpointExposureKind.Internal),
            },
        };
        var updatedEndpoints = new ServiceEndpointCatalogSpec
        {
            Identity = identity.Clone(),
            Endpoints =
            {
                CreateEndpointExposure("b-disabled", ServiceEndpointKind.Command, ServiceEndpointExposureKind.Disabled),
            },
        };
        var createdPolicy = CreatePolicySpec(identity, "policy-b");
        createdPolicy.InvokeAllowedCallerServiceKeys.Add("tenant/app/default/caller");
        var updatedPolicy = CreatePolicySpec(identity, "policy-a");
        updatedPolicy.DisplayName = "Updated Policy";
        updatedPolicy.ActivationRequiredBindingIds.Add("binding-a");
        updatedPolicy.InvokeRequiresActiveDeployment = true;

        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingCreatedEvent { Spec = createdBinding.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingUpdatedEvent { Spec = updatedBinding.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingRetiredEvent
        {
            Identity = identity.Clone(),
            BindingId = "binding-b",
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceEndpointCatalogCreatedEvent { Spec = createdEndpoints.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceEndpointCatalogUpdatedEvent { Spec = updatedEndpoints.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServicePolicyCreatedEvent { Spec = createdPolicy.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServicePolicyUpdatedEvent { Spec = updatedPolicy.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServicePolicyRetiredEvent
        {
            Identity = identity.Clone(),
            PolicyId = "policy-b",
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new StringValue { Value = "noop" }));
        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.Bindings.Select(x => x.BindingId).Should().Equal("binding-a", "binding-b");
        snapshot.Bindings.Should().Contain(x =>
            x.BindingId == "binding-a" &&
            x.DisplayName == "Updated" &&
            x.BindingKind == ServiceBindingKind.Secret &&
            x.SecretRef != null &&
            x.SecretRef.SecretName == "secret-a" &&
            x.ServiceRef == null &&
            x.ConnectorRef == null);
        snapshot.Bindings.Should().Contain(x =>
            x.BindingId == "binding-b" &&
            x.Retired &&
            x.ServiceRef != null &&
            x.ServiceRef.Identity.ServiceId == "dependency" &&
            x.ServiceRef.EndpointId == "run");
        snapshot.Endpoints.Should().ContainSingle();
        snapshot.Endpoints[0].EndpointId.Should().Be("b-disabled");
        snapshot.Endpoints[0].ExposureKind.Should().Be(ServiceEndpointExposureKind.Disabled);
        snapshot.Policies.Select(x => x.PolicyId).Should().Equal("policy-a", "policy-b");
        snapshot.Policies.Single(x => x.PolicyId == "policy-a").InvokeRequiresActiveDeployment.Should().BeTrue();
        snapshot.Policies.Single(x => x.PolicyId == "policy-b").Retired.Should().BeTrue();
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
            case ServiceBindingKind.Secret:
                spec.SecretRef = new BoundSecretRef
                {
                    SecretName = "secret",
                };
                break;
            case ServiceBindingKind.Connector:
                spec.ConnectorRef = new BoundConnectorRef
                {
                    ConnectorType = "mcp",
                    ConnectorId = "connector-a",
                };
                break;
        }

        return spec;
    }

    private static ServiceEndpointExposureSpec CreateEndpointExposure(
        string endpointId,
        ServiceEndpointKind kind,
        ServiceEndpointExposureKind exposureKind)
    {
        return new ServiceEndpointExposureSpec
        {
            EndpointId = endpointId,
            DisplayName = endpointId,
            Kind = kind,
            RequestTypeUrl = $"type.googleapis.com/demo.{endpointId}",
            ResponseTypeUrl = string.Empty,
            Description = endpointId,
            ExposureKind = exposureKind,
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

    private static EventEnvelope BuildEnvelope<T>(T evt)
        where T : Google.Protobuf.IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
        };
}
