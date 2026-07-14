using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

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
        result.WorkflowId.Should().Be("wf-alpha");
        result.WorkflowRevision.Should().Be("rev-alpha");
        result.PublishedServiceId.Should().Be("svc-alpha");
    }

    [Fact]
    public async Task WorkflowPort_ShouldUseWorkflowIdentityAndCloneDependencies()
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            OwnerLlmRouteRequired = true,
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ConnectorCapabilityRefs.Add("connector-alpha");
        var reader = new RecordingReader<WorkflowCatalogCurrentStateDocument>(new WorkflowCatalogCurrentStateDocument
        {
            StateVersion = 5,
            AuthorizationDependencies = dependencies,
        });

        var result = await new ProjectionScheduledInvocationWorkflowQueryPort(reader).GetAsync(" wf-alpha ");

        reader.Key.Should().Be("wf-alpha");
        result.Should().NotBeNull();
        result!.StateVersion.Should().Be(5);
        result.Dependencies.Should().BeEquivalentTo(dependencies);
        result.Dependencies.Should().NotBeSameAs(dependencies);
    }

    [Fact]
    public async Task VersionPorts_ShouldUseTheirOwnedDocumentKeys()
    {
        var connectorReader = new RecordingReader<ConnectorCatalogCurrentStateDocument>(
            new ConnectorCatalogCurrentStateDocument { StateVersion = 7 });
        var ownerLlmReader = new RecordingReader<UserConfigCurrentStateDocument>(
            new UserConfigCurrentStateDocument { StateVersion = 11 });

        var connector = await new ProjectionScheduledInvocationConnectorQueryPort(connectorReader)
            .GetAsync(" scope-alpha ");
        var ownerLlm = await new ProjectionScheduledInvocationOwnerLLMQueryPort(ownerLlmReader)
            .GetAsync(" scope-alpha ");

        connectorReader.Key.Should().Be("connector-catalog-scope-alpha");
        ownerLlmReader.Key.Should().Be("user-config-scope-alpha");
        connector!.StateVersion.Should().Be(7);
        ownerLlm!.StateVersion.Should().Be(11);
    }

    [Fact]
    public async Task Ports_ShouldFailClosedForMissingDocumentsOrRequiredMemberFields()
    {
        var missingMember = new RecordingReader<StudioMemberCurrentStateDocument>(null);
        var incompleteMember = new RecordingReader<StudioMemberCurrentStateDocument>(new());
        var missingWorkflow = new RecordingReader<WorkflowCatalogCurrentStateDocument>(new());
        var missingConnector = new RecordingReader<ConnectorCatalogCurrentStateDocument>(null);
        var missingOwnerLlm = new RecordingReader<UserConfigCurrentStateDocument>(null);

        (await new ProjectionScheduledInvocationMemberQueryPort(missingMember).GetAsync("s", "m")).Should().BeNull();
        (await new ProjectionScheduledInvocationMemberQueryPort(incompleteMember).GetAsync("s", "m")).Should().BeNull();
        (await new ProjectionScheduledInvocationWorkflowQueryPort(missingWorkflow).GetAsync("wf")).Should().BeNull();
        (await new ProjectionScheduledInvocationConnectorQueryPort(missingConnector).GetAsync("s")).Should().BeNull();
        (await new ProjectionScheduledInvocationOwnerLLMQueryPort(missingOwnerLlm).GetAsync("s")).Should().BeNull();
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
