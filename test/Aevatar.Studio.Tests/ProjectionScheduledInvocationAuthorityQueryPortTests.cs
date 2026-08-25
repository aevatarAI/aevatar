using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionScheduledInvocationAuthorityQueryPortTests
{
    [Fact]
    public async Task MemberPort_ShouldUseCanonicalKeyAndMapDistinctIdentities()
    {
        var reader = new RecordingReader<StudioMemberCurrentStateDocument>(new StudioMemberCurrentStateDocument
        {
            StateVersion = 420,
            AuthorizationRevision = 3,
            ImplementationWorkflowId = "wf-alpha",
            LastBoundRevisionId = "rev-alpha",
            PublishedServiceId = "svc-alpha",
        });

        var result = await new ProjectionScheduledInvocationMemberQueryPort(reader)
            .GetAsync(" scope-alpha ", " m-alpha ");

        reader.Key.Should().Be("studio-member:scope-alpha:m-alpha");
        result.Should().NotBeNull();
        result!.AuthorizationRevision.Should().Be(3);
        result.DraftWorkflowId.Should().Be("wf-alpha");
        result.WorkflowRevisionId.Should().Be("rev-alpha");
        result.PublishedServiceId.Should().Be("svc-alpha");
    }

    [Fact]
    public async Task MemberPort_ShouldMapLegacyZeroToStableBaseline_IgnoringAggregateVersion()
    {
        var reader = new RecordingReader<StudioMemberCurrentStateDocument>(new StudioMemberCurrentStateDocument
        {
            StateVersion = 427,
            AuthorizationRevision = 0,
            ImplementationWorkflowId = "wf-alpha",
            LastBoundRevisionId = "rev-alpha",
            PublishedServiceId = "svc-alpha",
        });

        var result = await new ProjectionScheduledInvocationMemberQueryPort(reader)
            .GetAsync("scope-alpha", "m-alpha");

        result.Should().NotBeNull();
        result!.AuthorizationRevision.Should().Be(1);
    }

    [Fact]
    public async Task MemberPort_ShouldFailClosed_WhenAuthorizationRevisionIsNegative()
    {
        var reader = new RecordingReader<StudioMemberCurrentStateDocument>(new StudioMemberCurrentStateDocument
        {
            StateVersion = 427,
            AuthorizationRevision = -1,
            ImplementationWorkflowId = "wf-alpha",
            LastBoundRevisionId = "rev-alpha",
            PublishedServiceId = "svc-alpha",
        });

        var result = await new ProjectionScheduledInvocationMemberQueryPort(reader)
            .GetAsync("scope-alpha", "m-alpha");

        result.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowPort_ShouldUseExactPublishedRevisionAndCloneAuthorizationEvidence()
    {
        var evidence = new WorkflowRevisionAuthorizationEvidence
        {
            OwnerLlmRouteRequired = true,
            ServiceGrantRequirement = AuthorizationGrantRequirement.Required,
        };
        evidence.ExternalCapabilities.Add(new ExternalWorkflowCapabilityRef
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-calendar-alpha",
                OperationId = "create_event",
                ContractDigest = "connector-digest-alpha",
            },
        });
        evidence.ExternalCapabilities.Add(new ExternalWorkflowCapabilityRef
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                EndpointId = "read_states",
                HttpMethod = "GET",
                PathTemplate = "/api/states",
                ContractDigest = "nyxid-digest-alpha",
                ExecutionPolicy = ReadOnlyPolicy(
                    ExternalCapabilityExecutionMode.Interactive,
                    ExternalCapabilityExecutionMode.Durable),
            },
        });
        var reader = new RecordingRevisionCatalogReader(CreateWorkflowRevisionCatalog(
            evidence,
            CreateAdmissionPlan(evidence.ExternalCapabilities)));

        var result = await new ProjectionScheduledInvocationWorkflowQueryPort(reader)
            .GetAsync(" scope-alpha ", " svc-alpha ", " rev-alpha ");

        reader.Identity.Should().NotBeNull();
        reader.Identity!.TenantId.Should().Be("scope-alpha");
        reader.Identity.AppId.Should().Be("default");
        reader.Identity.Namespace.Should().Be("default");
        reader.Identity.ServiceId.Should().Be("svc-alpha");
        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(5);
        result.OwnerLLMRouteRequired.Should().BeTrue();
        result.ExternalCapabilities.Should().Equal(evidence.ExternalCapabilities);
        result.ExternalCapabilities.Should()
            .OnlyContain(capability => evidence.ExternalCapabilities.All(source => !ReferenceEquals(source, capability)));
        result.ServiceGrantRequirement.Should().Be(AuthorizationGrantRequirement.Required);
    }

    [Fact]
    public async Task WorkflowPort_WhenDerivedEvidenceDoesNotMatchAdmissionPlan_ShouldFailClosed()
    {
        var evidence = new WorkflowRevisionAuthorizationEvidence
        {
            ServiceGrantRequirement = AuthorizationGrantRequirement.NotRequired,
        };
        evidence.ExternalCapabilities.Add(new ExternalWorkflowCapabilityRef
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-calendar-alpha",
                OperationId = "create_event",
                ContractDigest = "connector-digest-alpha",
            },
        });
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: workflow-alpha",
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Durable,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = "workflow-alpha/send-mail",
                Capability = new ExternalWorkflowCapabilityRef
                {
                    HostConnector = new HostConnectorCapabilityRef
                    {
                        ConnectorCapabilityRef = "connector-mail-alpha",
                        OperationId = "send_message",
                        ContractDigest = "connector-digest-beta",
                    },
                },
            }],
            []);
        var reader = new RecordingRevisionCatalogReader(
            CreateWorkflowRevisionCatalog(evidence, admissionPlan));

        var result = await new ProjectionScheduledInvocationWorkflowQueryPort(reader)
            .GetAsync("scope-alpha", "svc-alpha", "rev-alpha");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ConnectorPort_ShouldUseOwnedDocumentAndMapTypedEvidence()
    {
        var connectorState = new ConnectorCatalogState();
        connectorState.Connectors.Add(new ConnectorDefinitionEntry
        {
            Name = "calendar",
            Enabled = true,
        });
        connectorState.Connectors.Add(new ConnectorDefinitionEntry
        {
            Name = "disabled-mail",
            Enabled = false,
        });
        var connectorReader = new RecordingReader<ConnectorCatalogCurrentStateDocument>(
            new ConnectorCatalogCurrentStateDocument
            {
                StateVersion = 7,
                StateRoot = Any.Pack(connectorState),
            });
        var connector = await new ProjectionScheduledInvocationConnectorQueryPort(connectorReader)
            .GetAsync(" scope-alpha ");

        connectorReader.Key.Should().Be("connector-catalog-scope-alpha");
        connector!.StateVersion.Should().Be(7);
        connector.ConnectorCapabilityRefs.Should().Equal("calendar");
    }

    [Fact]
    public async Task OwnerLlmPort_WithTypedService_ShouldMapExactProjectedIdentity()
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 11,
            DefaultModel = "gpt-5.5",
            PreferredLlmRoute = "/api/v1/proxy/s/legacy-provider",
            LlmSelection = new LLMSelection
            {
                RouteKind = LLMRouteKind.NyxIdUserService,
                RouteValue = "/api/v1/proxy/s/chrono-llm-public",
                NyxIdUserServiceId = "us-chrono",
                ServiceSlugSnapshot = "chrono-llm-public",
                ModelSelection = ExplicitModel("gpt-5.5"),
            },
        });

        var ownerLlm = await new ProjectionScheduledInvocationOwnerLLMQueryPort(reader)
            .GetAsync(" scope-alpha ");

        reader.Key.Should().Be("user-config-scope-alpha");
        ownerLlm!.StateVersion.Should().Be(11);
        ownerLlm.Selection.RouteKind.Should().Be(LLMRouteKind.NyxIdUserService);
        ownerLlm.Selection.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm-public");
        ownerLlm.Selection.NyxIdUserServiceId.Should().Be("us-chrono");
        ownerLlm.Selection.ServiceSlugSnapshot.Should().Be("chrono-llm-public");
        ownerLlm.Selection.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task OwnerLlmPort_WithTypedGateway_ShouldMapExactRouteAndModel()
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 12,
            DefaultModel = "gpt-5.5",
            PreferredLlmRoute = "/api/v1/proxy/s/legacy-provider",
            LlmSelection = new LLMSelection
            {
                RouteKind = LLMRouteKind.Gateway,
                RouteValue = "/api/v1/llm/gateway/v1",
                ModelSelection = ExplicitModel("gpt-5.5"),
            },
        });

        var result = await new ProjectionScheduledInvocationOwnerLLMQueryPort(reader)
            .GetAsync("scope-alpha");

        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(12);
        result.Selection.RouteKind.Should().Be(LLMRouteKind.Gateway);
        result.Selection.RouteValue.Should().Be(ScheduledInvocationOwnerLLMSelectionPolicy.GatewayRoute);
        result.Selection.NyxIdUserServiceId.Should().BeEmpty();
        result.Selection.ServiceSlugSnapshot.Should().BeEmpty();
        result.Selection.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task OwnerLlmPort_WithLegacyProxyRoute_ShouldNotInferServiceIdentity()
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 13,
            DefaultModel = "gpt-5.5",
            PreferredLlmRoute = "/api/v1/proxy/s/provider-alpha",
        });

        var result = await new ProjectionScheduledInvocationOwnerLLMQueryPort(reader)
            .GetAsync("scope-alpha");

        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(13);
        result.Selection.RouteKind.Should().Be(LLMRouteKind.Unspecified);
        result.Selection.RouteValue.Should().BeEmpty();
        result.Selection.NyxIdUserServiceId.Should().BeEmpty();
        result.Selection.ServiceSlugSnapshot.Should().BeEmpty();
        result.Selection.Model.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("gateway")]
    [InlineData("/api/v1/llm/gateway/v1")]
    public async Task OwnerLlmPort_WithoutTypedSelection_ShouldFailClosed(string legacyRoute)
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 14,
            DefaultModel = "gpt-5.5",
            PreferredLlmRoute = legacyRoute,
        });

        var result = await new ProjectionScheduledInvocationOwnerLLMQueryPort(reader)
            .GetAsync("scope-alpha");

        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(14);
        result.Selection.RouteKind.Should().Be(LLMRouteKind.Unspecified);
        result.Selection.RouteValue.Should().BeEmpty();
        result.Selection.NyxIdUserServiceId.Should().BeEmpty();
        result.Selection.ServiceSlugSnapshot.Should().BeEmpty();
        result.Selection.Model.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LLMRouteKind.Unspecified, "/api/v1/llm/gateway/v1", "", "")]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/proxy/s/provider-alpha", "", "")]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/llm/gateway/v1", "us-alpha", "provider-alpha")]
    [InlineData(LLMRouteKind.Gateway, " /api/v1/llm/gateway/v1", "", "")]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/llm/gateway/v1 ", "", "")]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/llm/gateway/v1/", "", "")]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/llm/gateway/v1", "us-alpha", "")]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/llm/gateway/v1", "", "provider-alpha")]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/llm/gateway/v1", " ", "")]
    [InlineData(LLMRouteKind.Gateway, "/api/v1/llm/gateway/v1", "", " ")]
    [InlineData(LLMRouteKind.NyxIdUserService, "", "us-alpha", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, " ", "us-alpha", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", "", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", " ", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", "us-alpha", "")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", "us-alpha", " ")]
    [InlineData(LLMRouteKind.NyxIdUserService, " /api/v1/proxy/s/provider-alpha", "us-alpha", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha ", "us-alpha", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", " us-alpha", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", "us-alpha ", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", "us-alpha", " provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", "us-alpha", "provider-alpha ")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha/", "us-alpha", "provider-alpha")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha", "us-alpha", "provider-beta")]
    [InlineData(LLMRouteKind.NyxIdUserService, "/api/v1/proxy/s/provider-alpha/beta", "us-alpha", "provider-alpha/beta")]
    public async Task OwnerLlmPort_WithInvalidTypedSelection_ShouldFailClosed(
        LLMRouteKind routeKind,
        string routeValue,
        string serviceId,
        string serviceSlug)
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 15,
            DefaultModel = "gpt-5.5",
            PreferredLlmRoute = "/api/v1/proxy/s/legacy-provider",
            LlmSelection = new LLMSelection
            {
                RouteKind = routeKind,
                RouteValue = routeValue,
                NyxIdUserServiceId = serviceId,
                ServiceSlugSnapshot = serviceSlug,
            },
        });

        var result = await new ProjectionScheduledInvocationOwnerLLMQueryPort(reader)
            .GetAsync("scope-alpha");

        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(15);
        result.Selection.RouteKind.Should().Be(LLMRouteKind.Unspecified);
        result.Selection.RouteValue.Should().BeEmpty();
        result.Selection.NyxIdUserServiceId.Should().BeEmpty();
        result.Selection.ServiceSlugSnapshot.Should().BeEmpty();
        result.Selection.Model.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" gpt-5.5")]
    [InlineData("gpt-5.5 ")]
    public async Task OwnerLlmPort_WithInvalidModel_ShouldFailClosed(string model)
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 16,
            DefaultModel = model,
            LlmSelection = new LLMSelection
            {
                RouteKind = LLMRouteKind.Gateway,
                RouteValue = "/api/v1/llm/gateway/v1",
                ModelSelection = ExplicitModel(model),
            },
        });

        var result = await new ProjectionScheduledInvocationOwnerLLMQueryPort(reader)
            .GetAsync("scope-alpha");

        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(16);
        result.Selection.RouteKind.Should().Be(LLMRouteKind.Unspecified);
        result.Selection.RouteValue.Should().BeEmpty();
        result.Selection.NyxIdUserServiceId.Should().BeEmpty();
        result.Selection.ServiceSlugSnapshot.Should().BeEmpty();
        result.Selection.Model.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerLlmPort_ShouldUseOwnerScopeKeyWithoutCallerBindingContext()
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 17,
            DefaultModel = "gpt-5.5",
            LlmSelection = new LLMSelection
            {
                RouteKind = LLMRouteKind.NyxIdUserService,
                RouteValue = "/api/v1/proxy/s/provider-alpha",
                NyxIdUserServiceId = "us-alpha",
                ServiceSlugSnapshot = "provider-alpha",
                ModelSelection = ExplicitModel("gpt-5.5"),
            },
        });

        var result = await new ProjectionScheduledInvocationOwnerLLMQueryPort(reader)
            .GetAsync(" scope-binding-alpha ");

        reader.Key.Should().Be("user-config-scope-binding-alpha");
        result!.Selection.NyxIdUserServiceId.Should().Be("us-alpha");
    }

    [Fact]
    public async Task Ports_ShouldFailClosedForMissingDocumentsOrRequiredMemberFields()
    {
        var missingMember = new RecordingReader<StudioMemberCurrentStateDocument>(null);
        var incompleteMember = new RecordingReader<StudioMemberCurrentStateDocument>(new());
        var missingWorkflow = new RecordingRevisionCatalogReader(null);
        var missingConnector = new RecordingReader<ConnectorCatalogCurrentStateDocument>(null);

        (await new ProjectionScheduledInvocationMemberQueryPort(missingMember).GetAsync("s", "m")).Should().BeNull();
        (await new ProjectionScheduledInvocationMemberQueryPort(incompleteMember).GetAsync("s", "m")).Should().BeNull();
        (await new ProjectionScheduledInvocationWorkflowQueryPort(missingWorkflow)
            .GetAsync("s", "svc", "rev")).Should().BeNull();
        (await new ProjectionScheduledInvocationConnectorQueryPort(missingConnector).GetAsync("s")).Should().BeNull();
    }

    [Fact]
    public async Task OwnerLlmPort_WithMissingDocument_ShouldReturnNull()
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(null);

        var missingOwnerResult = await new ProjectionScheduledInvocationOwnerLLMQueryPort(reader)
            .GetAsync("s");

        missingOwnerResult.Should().BeNull();
    }

    private static ServiceRevisionCatalogSnapshot CreateWorkflowRevisionCatalog(
        WorkflowRevisionAuthorizationEvidence evidence,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan = null)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            RevisionId = "rev-alpha",
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = "workflow-alpha",
                    WorkflowYaml = "name: workflow-alpha",
                    AuthorizationEvidence = evidence,
                    CapabilityAdmissionPlan = capabilityAdmissionPlan,
                },
            },
        };
        return new ServiceRevisionCatalogSnapshot(
            "scope-alpha:aevatar:default:svc-alpha",
            [
                new ServiceRevisionSnapshot(
                    "rev-alpha",
                    ServiceImplementationKind.Workflow.ToString(),
                    ServiceRevisionStatus.Published.ToString(),
                    "artifact-hash",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null,
                    PreparedArtifact: artifact),
            ],
            DateTimeOffset.UtcNow,
            StateVersion: 5);
    }

    private static WorkflowCapabilityAdmissionPlan CreateAdmissionPlan(
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities) =>
        WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: workflow-alpha",
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Durable,
            capabilities.Select(static (capability, index) => new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = $"workflow-alpha/call-{index}",
                Capability = capability,
            }),
            []);

    private static NyxIdOperationExecutionPolicy ReadOnlyPolicy(
        params ExternalCapabilityExecutionMode[] executionModes)
    {
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = NyxIdOperationRisk.ReadOnly,
            Approval = NyxIdOperationApproval.None,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(executionModes);
        return policy;
    }

    private static LLMModelSelection ExplicitModel(string modelId) => new()
    {
        Kind = LLMModelSelectionKind.ExplicitModel,
        ModelId = modelId,
    };

    private sealed class RecordingRevisionCatalogReader(ServiceRevisionCatalogSnapshot? snapshot)
        : IServiceRevisionCatalogQueryReader
    {
        public ServiceIdentity? Identity { get; private set; }

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(
            ServiceIdentity identity,
            CancellationToken ct = default)
        {
            Identity = identity.Clone();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingReader<TDocument>(TDocument? document)
        : IProjectionDocumentReader<TDocument, string>
        where TDocument : class, IProjectionReadModel
    {
        public string? Key { get; private set; }

        public Task<TDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            Key = key;
            return Task.FromResult(document);
        }

        public Task<ProjectionDocumentQueryResult<TDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
