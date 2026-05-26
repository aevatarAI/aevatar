using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceRevisionCatalogQueryReaderTests
{
    [Fact]
    public async Task GetAsync_ShouldSortRevisionsByCreatedAtDescending()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r1",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                },
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r2",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Endpoints =
                    {
                        new ServiceCatalogEndpointReadModel
                        {
                            EndpointId = "run",
                            DisplayName = "run",
                            Kind = "Command",
                        },
                    },
                },
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var reader = new ServiceRevisionCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.Revisions.Select(x => x.RevisionId).Should().Equal("r2", "r1");
        snapshot.Revisions[0].Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenCatalogDoesNotExist()
    {
        var reader = new ServiceRevisionCatalogQueryReader(new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id));

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().BeNull();
    }

    [Fact]
    public void Constructor_ShouldValidateDocumentStore()
    {
        Action act = () => new ServiceRevisionCatalogQueryReader(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetAsync_ShouldNormalizeEmptyEndpointFields()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r-empty",
                    Endpoints =
                    {
                        new ServiceCatalogEndpointReadModel(),
                    },
                },
            },
        });
        var reader = new ServiceRevisionCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.Revisions.Should().ContainSingle();
        snapshot.Revisions[0].Endpoints.Should().ContainSingle();
        snapshot.Revisions[0].Endpoints[0].EndpointId.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ShouldMapVersionWatermarkAndTypedImplementationGovernance()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            StateVersion = 42,
            LastEventId = "evt-42",
            UpdatedAt = DateTimeOffset.UtcNow,
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r-workflow",
                    WorkflowName = "approval",
                    WorkflowDefinitionActorId = "workflow-def-1",
                    WorkflowInlineWorkflowCount = 2,
                },
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r-static",
                    StaticActorTypeName = "Tests.StaticActor, Tests",
                    StaticPreferredActorId = "static-actor-1",
                },
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r-script",
                    ScriptingScriptId = "script-a",
                    ScriptingRevision = "7",
                    ScriptingDefinitionActorId = "script-def-1",
                    ScriptingSourceHash = "hash-a",
                },
            },
        });
        var reader = new ServiceRevisionCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(GAgentServiceTestKit.CreateIdentity());

        snapshot.Should().NotBeNull();
        snapshot!.StateVersion.Should().Be(42);
        snapshot.LastEventId.Should().Be("evt-42");
        snapshot.Revisions.Single(x => x.RevisionId == "r-workflow").Implementation!.Workflow.Should()
            .BeEquivalentTo(new ServiceRevisionWorkflowSnapshot("approval", "workflow-def-1", 2));
        snapshot.Revisions.Single(x => x.RevisionId == "r-static").Implementation!.Static.Should()
            .BeEquivalentTo(new ServiceRevisionStaticSnapshot("Tests.StaticActor, Tests", "static-actor-1"));
        snapshot.Revisions.Single(x => x.RevisionId == "r-script").Implementation!.Scripting.Should()
            .BeEquivalentTo(new ServiceRevisionScriptingSnapshot("script-a", "7", "script-def-1", "hash-a"));
    }

    [Fact]
    public async Task GetAsync_ShouldMapPreparedArtifactAndCloneIt()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var preparedArtifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            identity,
            "r1",
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat"));
        await store.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = ServiceKeys.Build(identity),
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "r1",
                    ImplementationKind = ServiceImplementationKind.Static.ToString(),
                    Status = ServiceRevisionStatus.Prepared.ToString(),
                    ArtifactHash = "hash-1",
                    PreparedArtifact = preparedArtifact.Clone(),
                    Endpoints =
                    {
                        new ServiceCatalogEndpointReadModel
                        {
                            EndpointId = "chat",
                            DisplayName = "Chat",
                            Kind = ServiceEndpointKind.Chat.ToString(),
                            RequestTypeUrl = "type.googleapis.com/test.chat.request",
                            ResponseTypeUrl = "type.googleapis.com/test.chat.response",
                            Description = "chat endpoint",
                        },
                    },
                },
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var reader = new ServiceRevisionCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        var revision = snapshot!.Revisions.Should().ContainSingle().Subject;
        revision.PreparedArtifact.Should().NotBeNull();
        revision.PreparedArtifact.Should().NotBeSameAs(preparedArtifact);
        revision.PreparedArtifact!.RevisionId.Should().Be("r1");
        revision.PreparedArtifact.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        revision.Endpoints.Should().ContainSingle(x =>
            x.EndpointId == "chat" &&
            x.DisplayName == "Chat" &&
            x.Kind == ServiceEndpointKind.Chat.ToString() &&
            x.RequestTypeUrl == "type.googleapis.com/test.chat.request" &&
            x.ResponseTypeUrl == "type.googleapis.com/test.chat.response" &&
            x.Description == "chat endpoint");

        revision.PreparedArtifact.RevisionId = "mutated";
        var reread = await reader.GetAsync(identity);
        reread!.Revisions.Single().PreparedArtifact!.RevisionId.Should().Be("r1");
    }
}
