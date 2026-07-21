using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionScheduledInvocationAuthorityQueryPortTests
{
    [Fact]
    public async Task MemberPort_ShouldUseCanonicalKeyAndMapDistinctIdentities()
    {
        var reader = new RecordingReader<StudioMemberCurrentStateDocument>(new StudioMemberCurrentStateDocument
        {
            StateVersion = 3,
            ImplementationWorkflowId = "wf-alpha",
            LastBoundRevisionId = "rev-alpha",
            PublishedServiceId = "svc-alpha",
        });

        var result = await new ProjectionScheduledInvocationMemberQueryPort(reader)
            .GetAsync(" scope-alpha ", " m-alpha ");

        reader.Key.Should().Be("studio-member:scope-alpha:m-alpha");
        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(3);
        result.DraftWorkflowId.Should().Be("wf-alpha");
        result.WorkflowRevisionId.Should().Be("rev-alpha");
        result.PublishedServiceId.Should().Be("svc-alpha");
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
                OperationId = "read_states",
                HttpMethod = "GET",
                PathTemplate = "/api/states",
                ContractDigest = "nyxid-digest-alpha",
            },
        });
        var reader = new RecordingRevisionCatalogReader(CreateWorkflowRevisionCatalog(evidence));

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
    public async Task SourcePorts_ShouldUseOwnedDocumentsAndMapTypedEvidence()
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
        var ownerLlmReader = new RecordingReader<UserConfigCurrentStateDocument>(
            new UserConfigCurrentStateDocument
            {
                StateVersion = 11,
                PreferredLlmRoute = "/api/v1/proxy/s/provider-alpha",
            });

        var connector = await new ProjectionScheduledInvocationConnectorQueryPort(connectorReader)
            .GetAsync(" scope-alpha ");
        var ownerLlm = await new ProjectionScheduledInvocationOwnerLLMQueryPort(ownerLlmReader)
            .GetAsync(" scope-alpha ");

        connectorReader.Key.Should().Be("connector-catalog-scope-alpha");
        ownerLlmReader.Key.Should().Be("user-config-scope-alpha");
        connector!.StateVersion.Should().Be(7);
        connector.ConnectorCapabilityRefs.Should().Equal("calendar");
        ownerLlm!.StateVersion.Should().Be(11);
        ownerLlm.ServiceGrantRequirement.Should().Be(AuthorizationGrantRequirement.Required);
        ownerLlm.NyxIdServiceSlug.Should().Be("provider-alpha");
        ownerLlm.NyxIdServiceId.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerLlmPort_ShouldResolveEmptyPreferenceToEffectiveHostDefaultRoute()
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 12,
            PreferredLlmRoute = string.Empty,
        });
        var port = new ProjectionScheduledInvocationOwnerLLMQueryPort(
            reader,
            Options.Create(new ScheduledInvocationOwnerLLMRouteOptions
            {
                DefaultRoutePreference = "chrono-llm-public",
            }));

        var result = await port.GetAsync("scope-alpha");

        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(12);
        result.NyxIdServiceSlug.Should().Be("chrono-llm-public");
        result.ServiceGrantRequirement.Should().Be(AuthorizationGrantRequirement.Required);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("auto")]
    [InlineData("/api/v1/llm/gateway/v1")]
    public async Task OwnerLlmPort_ShouldRequireNoUserServiceGrantForBareGateway(string defaultRoute)
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 13,
        });
        var port = new ProjectionScheduledInvocationOwnerLLMQueryPort(
            reader,
            Options.Create(new ScheduledInvocationOwnerLLMRouteOptions
            {
                DefaultRoutePreference = defaultRoute,
            }));

        var result = await port.GetAsync("scope-alpha");

        result!.NyxIdServiceSlug.Should().BeEmpty();
        result.ServiceGrantRequirement.Should().Be(AuthorizationGrantRequirement.NotRequired);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("auto")]
    [InlineData("")]
    public async Task OwnerLlmPort_ShouldApplyServiceDefaultWhenUserPreferenceSelectsDefault(string preference)
    {
        var reader = new RecordingReader<UserConfigCurrentStateDocument>(new UserConfigCurrentStateDocument
        {
            StateVersion = 14,
            PreferredLlmRoute = preference,
        });
        var port = new ProjectionScheduledInvocationOwnerLLMQueryPort(
            reader,
            Options.Create(new ScheduledInvocationOwnerLLMRouteOptions
            {
                DefaultRoutePreference = "chrono-llm-public",
            }));

        var result = await port.GetAsync("scope-alpha");

        result!.NyxIdServiceSlug.Should().Be("chrono-llm-public");
        result.ServiceGrantRequirement.Should().Be(AuthorizationGrantRequirement.Required);
    }

    [Fact]
    public async Task Ports_ShouldFailClosedForMissingDocumentsOrRequiredMemberFields()
    {
        var missingMember = new RecordingReader<StudioMemberCurrentStateDocument>(null);
        var incompleteMember = new RecordingReader<StudioMemberCurrentStateDocument>(new());
        var missingWorkflow = new RecordingRevisionCatalogReader(null);
        var missingConnector = new RecordingReader<ConnectorCatalogCurrentStateDocument>(null);
        var missingOwnerLlm = new RecordingReader<UserConfigCurrentStateDocument>(null);

        (await new ProjectionScheduledInvocationMemberQueryPort(missingMember).GetAsync("s", "m")).Should().BeNull();
        (await new ProjectionScheduledInvocationMemberQueryPort(incompleteMember).GetAsync("s", "m")).Should().BeNull();
        (await new ProjectionScheduledInvocationWorkflowQueryPort(missingWorkflow)
            .GetAsync("s", "svc", "rev")).Should().BeNull();
        (await new ProjectionScheduledInvocationConnectorQueryPort(missingConnector).GetAsync("s")).Should().BeNull();
        var missingOwnerResult = await new ProjectionScheduledInvocationOwnerLLMQueryPort(
            missingOwnerLlm,
            Options.Create(new ScheduledInvocationOwnerLLMRouteOptions
            {
                DefaultRoutePreference = "chrono-llm-public",
            })).GetAsync("s");
        missingOwnerResult.Should().BeEquivalentTo(new ScheduledInvocationOwnerLLMEvidence(
            0,
            string.Empty,
            "chrono-llm-public",
            AuthorizationGrantRequirement.Required));
    }

    private static ServiceRevisionCatalogSnapshot CreateWorkflowRevisionCatalog(
        WorkflowRevisionAuthorizationEvidence evidence)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            RevisionId = "rev-alpha",
            ImplementationKind = ServiceImplementationKind.Workflow,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    AuthorizationEvidence = evidence,
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
