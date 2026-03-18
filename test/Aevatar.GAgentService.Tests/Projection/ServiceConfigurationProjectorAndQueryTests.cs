using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Projection.Configuration;
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
        var projector = new ServiceConfigurationProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceConfigurationQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceConfigurationProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-configuration",
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
        }, importedState));

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
        var projector = new ServiceConfigurationProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var reader = new ServiceConfigurationQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceConfigurationProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-configuration",
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
        var stateAfterBindingCreate = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(createdBinding, retired: false),
            ]);
        var stateAfterBindingUpdate = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(updatedBinding, retired: false),
                CreateBindingRecord(createdBinding, retired: false),
            ]);
        var stateAfterBindingRetire = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(updatedBinding, retired: false),
                CreateBindingRecord(createdBinding, retired: true),
            ]);
        var stateAfterEndpointCreate = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(updatedBinding, retired: false),
                CreateBindingRecord(createdBinding, retired: true),
            ],
            endpointCatalog: createdEndpoints);
        var stateAfterEndpointUpdate = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(updatedBinding, retired: false),
                CreateBindingRecord(createdBinding, retired: true),
            ],
            endpointCatalog: updatedEndpoints);
        var stateAfterPolicyCreate = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(updatedBinding, retired: false),
                CreateBindingRecord(createdBinding, retired: true),
            ],
            endpointCatalog: updatedEndpoints,
            policies:
            [
                CreatePolicyRecord(createdPolicy, retired: false),
            ]);
        var stateAfterPolicyUpdate = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(updatedBinding, retired: false),
                CreateBindingRecord(createdBinding, retired: true),
            ],
            endpointCatalog: updatedEndpoints,
            policies:
            [
                CreatePolicyRecord(updatedPolicy, retired: false),
                CreatePolicyRecord(createdPolicy, retired: false),
            ]);
        var stateAfterPolicyRetire = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(updatedBinding, retired: false),
                CreateBindingRecord(createdBinding, retired: true),
            ],
            endpointCatalog: updatedEndpoints,
            policies:
            [
                CreatePolicyRecord(updatedPolicy, retired: false),
                CreatePolicyRecord(createdPolicy, retired: true),
            ]);

        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingCreatedEvent { Spec = createdBinding.Clone() }, stateAfterBindingCreate));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingUpdatedEvent { Spec = updatedBinding.Clone() }, stateAfterBindingUpdate));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceBindingRetiredEvent
        {
            Identity = identity.Clone(),
            BindingId = "binding-b",
        }, stateAfterBindingRetire));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceEndpointCatalogCreatedEvent { Spec = createdEndpoints.Clone() }, stateAfterEndpointCreate));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceEndpointCatalogUpdatedEvent { Spec = updatedEndpoints.Clone() }, stateAfterEndpointUpdate));
        await projector.ProjectAsync(context, BuildEnvelope(new ServicePolicyCreatedEvent { Spec = createdPolicy.Clone() }, stateAfterPolicyCreate));
        await projector.ProjectAsync(context, BuildEnvelope(new ServicePolicyUpdatedEvent { Spec = updatedPolicy.Clone() }, stateAfterPolicyUpdate));
        await projector.ProjectAsync(context, BuildEnvelope(new ServicePolicyRetiredEvent
        {
            Identity = identity.Clone(),
            PolicyId = "policy-b",
        }, stateAfterPolicyRetire));
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

    [Fact]
    public async Task QueryReader_ShouldReturnNull_WhenConfigurationDoesNotExist()
    {
        var reader = new ServiceConfigurationQueryReader(new RecordingDocumentStore<ServiceConfigurationReadModel>(x => x.Id));

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task QueryReader_ShouldReturnNull_WhenProjectionDisabled()
    {
        var store = new RecordingDocumentStore<ServiceConfigurationReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceConfigurationReadModel
        {
            Id = "tenant:app:default:svc",
            Identity = new ServiceIdentityReadModel
            {
                TenantId = "tenant",
                AppId = "app",
                Namespace = "default",
                ServiceId = "svc",
            },
        });
        var reader = new ServiceConfigurationQueryReader(
            store,
            new ServiceGovernanceProjectionOptions
            {
                Enabled = false,
            });

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task QueryReader_ShouldNormalizeNullStringsAndOptionalRefs()
    {
        var store = new RecordingDocumentStore<ServiceConfigurationReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceConfigurationReadModel
        {
            Id = "tenant:app:default:svc",
            Identity = new ServiceIdentityReadModel(),
            Bindings =
            {
                new ServiceBindingReadModel
                {
                    BindingId = "binding-service",
                    BindingKind = ServiceBindingKind.Service,
                    ServiceRef = new BoundServiceReferenceReadModel
                    {
                        Identity = new ServiceIdentityReadModel(),
                    },
                },
                new ServiceBindingReadModel
                {
                    BindingId = "binding-connector",
                    BindingKind = ServiceBindingKind.Connector,
                    ConnectorRef = new BoundConnectorReferenceReadModel(),
                },
                new ServiceBindingReadModel
                {
                    BindingId = "binding-secret",
                    BindingKind = ServiceBindingKind.Secret,
                    SecretRef = new BoundSecretReferenceReadModel(),
                },
                new ServiceBindingReadModel
                {
                    BindingId = "binding-empty",
                    BindingKind = ServiceBindingKind.Unspecified,
                },
            },
            Endpoints =
            {
                new ServiceEndpointExposureReadModel
                {
                    EndpointId = "invoke",
                },
            },
            Policies =
            {
                new ServicePolicyReadModel
                {
                    PolicyId = "policy-a",
                },
            },
        });
        var reader = new ServiceConfigurationQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.Identity.TenantId.Should().BeEmpty();
        snapshot.Bindings.Should().ContainSingle(x =>
            x.BindingId == "binding-service" &&
            x.ServiceRef != null &&
            x.ServiceRef.EndpointId == string.Empty &&
            x.ServiceRef.Identity.ServiceId == string.Empty);
        snapshot.Bindings.Should().ContainSingle(x =>
            x.BindingId == "binding-connector" &&
            x.ConnectorRef != null &&
            x.ConnectorRef.ConnectorType == string.Empty &&
            x.ConnectorRef.ConnectorId == string.Empty);
        snapshot.Bindings.Should().ContainSingle(x =>
            x.BindingId == "binding-secret" &&
            x.SecretRef != null &&
            x.SecretRef.SecretName == string.Empty);
        snapshot.Bindings.Should().ContainSingle(x =>
            x.BindingId == "binding-empty" &&
            x.ServiceRef == null &&
            x.ConnectorRef == null &&
            x.SecretRef == null);
        snapshot.Endpoints.Should().ContainSingle(x => x.EndpointId == "invoke" && x.DisplayName == string.Empty);
        snapshot.Policies.Should().ContainSingle(x => x.PolicyId == "policy-a" && x.DisplayName == string.Empty);
    }

    [Fact]
    public async Task Projector_ShouldIgnoreLegacyImport_WhenImportedStateHasNoIdentity()
    {
        var store = new RecordingDocumentStore<ServiceConfigurationReadModel>(x => x.Id);
        var projector = new ServiceConfigurationProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var context = new ServiceConfigurationProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-configuration",
        };

        await projector.ProjectAsync(context, BuildEnvelope(new LegacyServiceConfigurationImportedEvent
        {
            State = new ServiceConfigurationState(),
        }, new ServiceConfigurationState()));

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_ShouldIgnoreCommittedEnvelope_WhenEventDataIsMissing()
    {
        var store = new RecordingDocumentStore<ServiceConfigurationReadModel>(x => x.Id);
        var projector = new ServiceConfigurationProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var context = new ServiceConfigurationProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-configuration",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "outer-missing-data",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T10:05:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-missing-data",
                        Version = 9,
                    },
                }),
            });

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectorAndQueryReader_ShouldProjectConnectorBinding_FromCommittedEnvelope_AndStampReadModel()
    {
        var observedAt = DateTimeOffset.Parse("2026-03-15T10:00:00+00:00");
        var store = new RecordingDocumentStore<ServiceConfigurationReadModel>(x => x.Id);
        var projector = new ServiceConfigurationProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceConfigurationQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var spec = CreateBindingSpec(identity, "binding-c", ServiceBindingKind.Connector);
        var context = new ServiceConfigurationProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-configuration",
        };
        var state = CreateConfigurationState(
            identity,
            bindings:
            [
                CreateBindingRecord(spec, retired: false),
            ]);

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceBindingCreatedEvent
                {
                    Spec = spec,
                },
                eventId: "evt-binding-c",
                stateVersion: 7,
                observedAt: observedAt,
                state: state));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        var snapshot = await reader.GetAsync(identity);

        readModel.Should().NotBeNull();
        readModel!.ActorId.Should().Be("tenant:app:default:svc");
        readModel.StateVersion.Should().Be(7);
        readModel.LastEventId.Should().Be("evt-binding-c");
        readModel.UpdatedAt.Should().Be(observedAt);
        snapshot.Should().NotBeNull();
        snapshot!.Bindings.Should().ContainSingle();
        snapshot.Bindings[0].BindingKind.Should().Be(ServiceBindingKind.Connector);
        snapshot.Bindings[0].ConnectorRef.Should().NotBeNull();
        var connectorRef = snapshot.Bindings[0].ConnectorRef!;
        connectorRef.ConnectorType.Should().Be("mcp");
        connectorRef.ConnectorId.Should().Be("connector-a");
        snapshot.Bindings[0].ServiceRef.Should().BeNull();
        snapshot.Bindings[0].SecretRef.Should().BeNull();
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

    private static EventEnvelope BuildEnvelope<T>(T evt, ServiceConfigurationState? state = null)
        where T : Google.Protobuf.IMessage =>
        BuildCommittedEnvelope(
            evt,
            Guid.NewGuid().ToString("N"),
            1,
            DateTimeOffset.UtcNow,
            state);

    private static EventEnvelope BuildCommittedEnvelope<T>(
        T evt,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt,
        ServiceConfigurationState? state = null)
        where T : Google.Protobuf.IMessage =>
        new()
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(5)),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = stateVersion,
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                    EventData = Any.Pack(evt),
                },
                StateRoot = state == null ? null : Any.Pack(state),
            }),
        };

    private static ServiceConfigurationState CreateConfigurationState(
        ServiceIdentity identity,
        IReadOnlyList<ServiceBindingRecordState>? bindings = null,
        ServiceEndpointCatalogSpec? endpointCatalog = null,
        IReadOnlyList<ServicePolicyRecordState>? policies = null)
    {
        var state = new ServiceConfigurationState
        {
            Identity = identity.Clone(),
        };
        if (bindings != null)
        {
            foreach (var binding in bindings)
                state.Bindings[binding.Spec?.BindingId ?? string.Empty] = binding.Clone();
        }

        if (endpointCatalog != null)
            state.EndpointCatalog = endpointCatalog.Clone();

        if (policies != null)
        {
            foreach (var policy in policies)
                state.Policies[policy.Spec?.PolicyId ?? string.Empty] = policy.Clone();
        }

        return state;
    }

    private static ServiceBindingRecordState CreateBindingRecord(ServiceBindingSpec spec, bool retired)
    {
        return new ServiceBindingRecordState
        {
            Spec = spec.Clone(),
            Retired = retired,
        };
    }

    private static ServicePolicyRecordState CreatePolicyRecord(ServicePolicySpec spec, bool retired)
    {
        return new ServicePolicyRecordState
        {
            Spec = spec.Clone(),
            Retired = retired,
        };
    }
}
