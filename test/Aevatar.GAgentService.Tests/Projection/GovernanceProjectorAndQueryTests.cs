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

public sealed class GovernanceProjectorAndQueryTests
{
    [Fact]
    public async Task BindingProjectorAndQueryReader_ShouldProjectLifecycleAndMapNullFields()
    {
        var store = new RecordingDocumentStore<ServiceBindingCatalogReadModel>(x => x.Id);
        var projector = new ServiceBindingProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var reader = new ServiceBindingQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceBindingProjectionContext
        {
            ProjectionId = "service-bindings:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        var created = CreateBindingSpec(identity, "binding-b", ServiceBindingKind.Service);
        created.PolicyIds.Add("policy-a");
        var updated = CreateBindingSpec(identity, "binding-a", ServiceBindingKind.Secret);
        updated.DisplayName = "Updated";
        updated.SecretRef = new BoundSecretRef { SecretName = "secret-a" };

        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingCreatedEvent { Spec = created.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingUpdatedEvent { Spec = updated.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingRetiredEvent
        {
            Identity = identity.Clone(),
            BindingId = "binding-b",
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
            x.BindingKind == ServiceBindingKind.Secret.ToString() &&
            x.SecretName == "secret-a" &&
            x.TargetServiceKey == null &&
            x.ConnectorType == null);
        snapshot.Bindings.Should().Contain(x =>
            x.BindingId == "binding-b" &&
            x.Retired &&
            x.TargetServiceKey == "tenant:app:default:dependency" &&
            x.TargetEndpointId == "run");
    }

    [Fact]
    public async Task BindingProjector_ShouldRespectCancellation_AndQueryReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServiceBindingCatalogReadModel>(x => x.Id);
        var projector = new ServiceBindingProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceBindingQueryReader(store);
        var context = new ServiceBindingProjectionContext
        {
            ProjectionId = "service-bindings:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var initialize = () => projector.InitializeAsync(context, cts.Token).AsTask();
        var complete = () => projector.CompleteAsync(context, [], cts.Token).AsTask();

        await initialize.Should().ThrowAsync<OperationCanceledException>();
        await complete.Should().ThrowAsync<OperationCanceledException>();
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    [Fact]
    public async Task EndpointCatalogProjectorAndQueryReader_ShouldProjectUpdatesAndFallbackOrdering()
    {
        var store = new RecordingDocumentStore<ServiceEndpointCatalogReadModel>(x => x.Id);
        var projector = new ServiceEndpointCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var reader = new ServiceEndpointCatalogQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceEndpointCatalogProjectionContext
        {
            ProjectionId = "service-endpoint-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        var created = new ServiceEndpointCatalogSpec
        {
            Identity = identity.Clone(),
            Endpoints =
            {
                CreateEndpointExposure("z-chat", ServiceEndpointKind.Chat, ServiceEndpointExposureKind.Public),
                CreateEndpointExposure("a-command", ServiceEndpointKind.Command, ServiceEndpointExposureKind.Internal),
            },
        };
        var updated = new ServiceEndpointCatalogSpec
        {
            Identity = identity.Clone(),
            Endpoints =
            {
                CreateEndpointExposure("b-disabled", ServiceEndpointKind.Command, ServiceEndpointExposureKind.Disabled),
            },
        };

        await projector.ProjectAsync(context, BuildEnvelope(new ServiceEndpointCatalogCreatedEvent { Spec = created.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceEndpointCatalogUpdatedEvent { Spec = updated.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new StringValue { Value = "noop" }));
        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.Endpoints.Should().ContainSingle();
        snapshot.Endpoints[0].EndpointId.Should().Be("b-disabled");
        snapshot.Endpoints[0].ExposureKind.Should().Be(ServiceEndpointExposureKind.Disabled.ToString());
    }

    [Fact]
    public async Task EndpointCatalogProjector_ShouldRespectCancellation_AndQueryReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServiceEndpointCatalogReadModel>(x => x.Id);
        var projector = new ServiceEndpointCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceEndpointCatalogQueryReader(store);
        var context = new ServiceEndpointCatalogProjectionContext
        {
            ProjectionId = "service-endpoint-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var initialize = () => projector.InitializeAsync(context, cts.Token).AsTask();
        var complete = () => projector.CompleteAsync(context, [], cts.Token).AsTask();

        await initialize.Should().ThrowAsync<OperationCanceledException>();
        await complete.Should().ThrowAsync<OperationCanceledException>();
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    [Fact]
    public async Task PolicyProjectorAndQueryReader_ShouldProjectLifecycleAndSortPolicies()
    {
        var store = new RecordingDocumentStore<ServicePolicyCatalogReadModel>(x => x.Id);
        var projector = new ServicePolicyProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var reader = new ServicePolicyQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServicePolicyProjectionContext
        {
            ProjectionId = "service-policies:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        var created = CreatePolicySpec(identity, "policy-b");
        created.InvokeAllowedCallerServiceKeys.Add("tenant/app/default/caller");
        var updated = CreatePolicySpec(identity, "policy-a");
        updated.DisplayName = "Updated Policy";
        updated.ActivationRequiredBindingIds.Add("binding-a");
        updated.InvokeRequiresActiveDeployment = true;

        await projector.ProjectAsync(context, BuildEnvelope(new ServicePolicyCreatedEvent { Spec = created.Clone() }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServicePolicyUpdatedEvent { Spec = updated.Clone() }));
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
        snapshot!.Policies.Select(x => x.PolicyId).Should().Equal("policy-a", "policy-b");
        var updatedSnapshot = snapshot.Policies.Single(x => x.PolicyId == "policy-a");
        updatedSnapshot.DisplayName.Should().Be("Updated Policy");
        updatedSnapshot.ActivationRequiredBindingIds.Should().Equal("binding-a");
        updatedSnapshot.InvokeRequiresActiveDeployment.Should().BeTrue();
        var retiredSnapshot = snapshot.Policies.Single(x => x.PolicyId == "policy-b");
        retiredSnapshot.Retired.Should().BeTrue();
        retiredSnapshot.InvokeAllowedCallerServiceKeys.Should().Equal("tenant/app/default/caller");
    }

    [Fact]
    public async Task PolicyProjector_ShouldRespectCancellation_AndQueryReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServicePolicyCatalogReadModel>(x => x.Id);
        var projector = new ServicePolicyProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServicePolicyQueryReader(store);
        var context = new ServicePolicyProjectionContext
        {
            ProjectionId = "service-policies:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var initialize = () => projector.InitializeAsync(context, cts.Token).AsTask();
        var complete = () => projector.CompleteAsync(context, [], cts.Token).AsTask();

        await initialize.Should().ThrowAsync<OperationCanceledException>();
        await complete.Should().ThrowAsync<OperationCanceledException>();
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
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
