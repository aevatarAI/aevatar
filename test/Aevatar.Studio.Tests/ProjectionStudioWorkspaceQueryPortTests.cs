using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using ProtoWorkspaceDirectory = Aevatar.Studio.Workspace.StudioWorkspaceDirectory;
using ProtoWorkspaceSettings = Aevatar.Studio.Workspace.StudioWorkspaceSettings;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionStudioWorkspaceQueryPortTests
{
    [Fact]
    public async Task GetAsync_ShouldMapPackedWorkspaceState()
    {
        var scopeResolver = new StubScopeResolver { ScopeId = "scope-1" };
        var actorId = StudioWorkspaceConventions.BuildActorId("scope-1");
        var updatedAt = DateTimeOffset.Parse("2026-05-19T09:00:00Z");
        var state = new StudioWorkspaceState
        {
            WorkspaceId = actorId,
            ScopeId = "scope-1",
            Settings = new ProtoWorkspaceSettings
            {
                RuntimeBaseUrl = "http://127.0.0.1:5100",
                AppearanceTheme = "teal",
                ColorMode = "dark",
            },
            LastAppliedEventVersion = 12,
        };
        state.Directories.Add(new ProtoWorkspaceDirectory
        {
            DirectoryId = "dir-1",
            Label = "Drafts",
            Path = "/tmp/drafts",
            IsBuiltIn = true,
        });
        state.Drafts.Add("workflow-1", new StudioWorkflowDraft
        {
            WorkflowId = "workflow-1",
            Name = "workflow-one",
            FileName = "workflow-one.yaml",
            DirectoryId = "dir-1",
            DirectoryLabel = "Drafts",
            Yaml = "name: workflow-one\nsteps: []\n",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt.AddHours(-1)),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt),
            Version = 3,
            Layout = new StudioWorkflowLayout
            {
                EntryWorkflow = "workflow-one",
                Viewport = new StudioWorkflowViewport { X = 1, Y = 2, Zoom = 0.75 },
                Nodes = { new StudioWorkflowNodeLayout { NodeId = "start", X = 10, Y = 20 } },
                Groups =
                {
                    new StudioWorkflowLayoutGroup
                    {
                        GroupId = "group-1",
                        NodeIds = { "start" },
                    },
                },
                Collapsed = { "group-1" },
            },
        });
        var reader = new StubDocumentReader();
        reader.Set(actorId, new StudioWorkspaceCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 17,
            LastEventId = "evt-17",
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            StateRoot = Any.Pack(state),
        });
        var port = new ProjectionStudioWorkspaceQueryPort(reader, scopeResolver);

        var snapshot = await port.GetAsync();

        snapshot.WorkspaceId.Should().Be(actorId);
        snapshot.ScopeId.Should().Be("scope-1");
        snapshot.StateVersion.Should().Be(17);
        snapshot.UpdatedAtUtc.Should().Be(updatedAt);
        snapshot.Settings.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
        snapshot.Settings.AppearanceTheme.Should().Be("teal");
        snapshot.Settings.ColorMode.Should().Be("dark");
        snapshot.Directories.Should().ContainSingle().Which.DirectoryId.Should().Be("dir-1");

        var draft = snapshot.Drafts.Should().ContainSingle().Subject;
        draft.WorkflowId.Should().Be("workflow-1");
        draft.FilePath.Should().Be(Path.Combine("Drafts", "workflow-one.yaml"));
        draft.Layout.Should().NotBeNull();
        draft.Layout!.EntryWorkflow.Should().Be("workflow-one");
        draft.Layout.NodePositions["start"].X.Should().Be(10);
        draft.Layout.Groups["group-1"].Should().Equal("start");
        draft.Layout.Collapsed.Should().Equal("group-1");
        draft.Layout.Viewport.Zoom.Should().Be(0.75);
        draft.Version.Should().Be(3);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnDefaultSnapshot_WhenDocumentMissing()
    {
        var port = new ProjectionStudioWorkspaceQueryPort(
            new StubDocumentReader(),
            new StubScopeResolver());

        var snapshot = await port.GetAsync();

        snapshot.WorkspaceId.Should().Be(StudioWorkspaceConventions.BuildActorId("default"));
        snapshot.ScopeId.Should().Be("default");
        snapshot.StateVersion.Should().Be(0);
        snapshot.UpdatedAtUtc.Should().Be(DateTimeOffset.MinValue);
        snapshot.Settings.RuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
        snapshot.Settings.AppearanceTheme.Should().Be("blue");
        snapshot.Settings.ColorMode.Should().Be("light");
        snapshot.Directories.Should().BeEmpty();
        snapshot.Drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ShouldThrow_WhenAuthenticatedCallerHasNoScope()
    {
        var port = new ProjectionStudioWorkspaceQueryPort(
            new StubDocumentReader(),
            new StubScopeResolver { AuthenticatedWithoutScope = true });

        var act = () => port.GetAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authenticated caller has no resolvable scope*");
    }

    private sealed class StubScopeResolver : IAppScopeResolver
    {
        public string? ScopeId { get; init; }
        public bool AuthenticatedWithoutScope { get; init; }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            string.IsNullOrWhiteSpace(ScopeId) ? null : new AppScopeContext(ScopeId, "test");

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) =>
            AuthenticatedWithoutScope;
    }

    private sealed class StubDocumentReader
        : IProjectionDocumentReader<StudioWorkspaceCurrentStateDocument, string>
    {
        private readonly Dictionary<string, StudioWorkspaceCurrentStateDocument> _documents = new(StringComparer.Ordinal);

        public void Set(string key, StudioWorkspaceCurrentStateDocument document) =>
            _documents[key] = document;

        public Task<StudioWorkspaceCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default) =>
            Task.FromResult(_documents.GetValueOrDefault(key));

        public Task<ProjectionDocumentQueryResult<StudioWorkspaceCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<StudioWorkspaceCurrentStateDocument>
            {
                Items = _documents.Values.ToList(),
            });
    }
}
