using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using ProtoWorkspaceDirectory = Aevatar.Studio.Workspace.StudioWorkspaceDirectory;
using ProtoWorkspaceSettings = Aevatar.Studio.Workspace.StudioWorkspaceSettings;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionStudioWorkspaceQueryPortTests
{
    private static readonly JsonFormatter StateRootFormatter = new(
        JsonFormatter.Settings.Default
            .WithPreserveProtoFieldNames(true)
            .WithFormatDefaultValues(true));

    [Fact]
    public async Task GetAsync_ShouldMapOpaqueWorkspaceStateJson()
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
        });
        var reader = new StubDocumentReader();
        reader.Set(actorId, new StudioWorkspaceCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 17,
            LastEventId = "evt-17",
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            StateRootJson = StateRootFormatter.Format(state),
        });
        var port = new ProjectionStudioWorkspaceQueryPort(reader, scopeResolver);

        var snapshot = await port.GetAsync();

        snapshot.WorkspaceId.Should().Be(actorId);
        snapshot.ScopeId.Should().Be("scope-1");
        snapshot.StateVersion.Should().Be(17);
        snapshot.UpdatedAtUtc.Should().Be(updatedAt);
        snapshot.Settings.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
        snapshot.Settings.AppearanceTheme.Should().Be("blue");
        snapshot.Settings.ColorMode.Should().Be("light");
        snapshot.Directories.Should().ContainSingle().Which.DirectoryId.Should().Be("dir-1");

        var draft = snapshot.Drafts.Should().ContainSingle().Subject;
        draft.WorkflowId.Should().Be("workflow-1");
        draft.FilePath.Should().Be(Path.Combine("Drafts", "workflow-one.yaml"));
        draft.Layout.Should().BeNull();
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
    public async Task GetAsync_WithExplicitScope_ShouldReadRequestedScopeInsteadOfAmbientScope()
    {
        var reader = new StubDocumentReader();
        var port = new ProjectionStudioWorkspaceQueryPort(
            reader,
            new StubScopeResolver { ScopeId = "ambient-scope" });

        var snapshot = await port.GetAsync(" requested-scope ");

        reader.ReadKeys.Should().ContainSingle().Which.Should().Be("studio-workspace:requested-scope");
        snapshot.WorkspaceId.Should().Be("studio-workspace:requested-scope");
        snapshot.ScopeId.Should().Be("requested-scope");
        snapshot.Drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WhenStateRootJsonIsInvalid_ShouldUseScopedEmptyStateButKeepDocumentWatermark()
    {
        var scopeResolver = new StubScopeResolver { ScopeId = "scope-2" };
        var actorId = StudioWorkspaceConventions.BuildActorId("scope-2");
        var updatedAt = DateTimeOffset.Parse("2026-05-19T10:30:00Z");
        var reader = new StubDocumentReader();
        reader.Set(actorId, new StudioWorkspaceCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 23,
            LastEventId = "evt-23",
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            StateRootJson = "{not-json",
        });
        var port = new ProjectionStudioWorkspaceQueryPort(reader, scopeResolver);

        var snapshot = await port.GetAsync();

        snapshot.WorkspaceId.Should().Be(actorId);
        snapshot.ScopeId.Should().Be("scope-2");
        snapshot.StateVersion.Should().Be(23);
        snapshot.UpdatedAtUtc.Should().Be(updatedAt);
        snapshot.Settings.RuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
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
            .WithMessage("HTTP request has no resolvable scope*");
    }

    [Fact]
    public async Task GetAsync_ShouldThrowBeforeDocumentRead_WhenUnauthenticatedRequestHasNoScope()
    {
        var reader = new StubDocumentReader();
        var port = new ProjectionStudioWorkspaceQueryPort(
            reader,
            new StubScopeResolver { HttpRequestContextPresent = true });

        var act = () => port.GetAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("HTTP request has no resolvable scope*");
        reader.ReadKeys.Should().BeEmpty();
    }

    private sealed class StubScopeResolver : IAppScopeResolver
    {
        public string? ScopeId { get; init; }
        public bool AuthenticatedWithoutScope { get; init; }
        public bool HttpRequestContextPresent { get; init; }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            string.IsNullOrWhiteSpace(ScopeId) ? null : new AppScopeContext(ScopeId, "test");

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) =>
            AuthenticatedWithoutScope;

        public bool HasHttpRequestContext(HttpContext? httpContext = null) =>
            HttpRequestContextPresent || AuthenticatedWithoutScope;
    }

    private sealed class StubDocumentReader
        : IProjectionDocumentReader<StudioWorkspaceCurrentStateDocument, string>
    {
        private readonly Dictionary<string, StudioWorkspaceCurrentStateDocument> _documents = new(StringComparer.Ordinal);

        public List<string> ReadKeys { get; } = [];

        public void Set(string key, StudioWorkspaceCurrentStateDocument document) =>
            _documents[key] = document;

        public Task<StudioWorkspaceCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ReadKeys.Add(key);
            return Task.FromResult(_documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<StudioWorkspaceCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<StudioWorkspaceCurrentStateDocument>
            {
                Items = _documents.Values.ToList(),
            });
    }
}
