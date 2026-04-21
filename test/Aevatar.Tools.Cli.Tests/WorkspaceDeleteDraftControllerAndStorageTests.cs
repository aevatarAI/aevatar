using System.Net;
using System.Text.RegularExpressions;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class WorkspaceDeleteDraftControllerAndStorageTests
{
    [Fact]
    public async Task GetSettings_WhenScopeIsResolved_ReturnsScopedDirectoryOnly()
    {
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.GetSettings(null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<WorkspaceSettingsResponse>().Subject;
        payload.Directories.Should().ContainSingle();
        payload.Directories[0].DirectoryId.Should().Be("scope:scope-1");
        payload.Directories[0].Label.Should().Be("scope-1");
        payload.Directories[0].Path.Should().Be("scope://scope-1");
    }

    [Fact]
    public async Task GetSettings_WhenRequestedScopeMismatchesAmbientScope_ReturnsForbidden()
    {
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.GetSettings("scope-2", CancellationToken.None);

        var forbidden = result.Result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        forbidden.Value.Should().BeEquivalentTo(new
        {
            message = "Requested scope does not match the authenticated Studio scope.",
        });
    }

    [Fact]
    public async Task UpdateSettings_ReturnsNormalizedRuntimeBaseUrl()
    {
        var store = new RecordingWorkspaceStore(Path.GetTempPath());
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.UpdateSettings(
            new UpdateWorkspaceSettingsRequest("http://127.0.0.1:5100/"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<WorkspaceSettingsResponse>().Subject;
        payload.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
    }

    [Fact]
    public async Task DeleteDraft_WhenScopeIsNotResolved_DeletesWorkspaceDraftAndReturnsNoContent()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"workspace-delete-{Guid.NewGuid():N}");
        var store = new RecordingWorkspaceStore(workspaceRoot);
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());
        var workflowPath = Path.Combine(workspaceRoot, "drafts", "hello.yaml");
        var workflowId = WorkspaceService.CreateStableId(workflowPath);

        var result = await controller.DeleteDraft(workflowId, null, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        store.DeletedWorkflowIds.Should().ContainSingle().Which.Should().Be(workflowId);
    }

    [Fact]
    public async Task DeleteDraft_WhenScopeIsResolved_DeletesScopedDraftAndReturnsNoContent()
    {
        var storagePort = new RecordingWorkflowDraftStore();
        var workflowId = $"workflow-{Guid.NewGuid():N}";
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.DeleteDraft(workflowId, null, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        storagePort.DeletedWorkflows.Should().ContainSingle();
        storagePort.DeletedWorkflows[0].ScopeId.Should().Be("scope-1");
        storagePort.DeletedWorkflows[0].WorkflowId.Should().Be(workflowId);
    }

    [Fact]
    public async Task DeleteDraft_WhenQueryFallbackIsEnabled_UsesRequestedScopeId()
    {
        var storagePort = new RecordingWorkflowDraftStore();
        var workflowId = $"workflow-{Guid.NewGuid():N}";
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = true,
            });

        var result = await controller.DeleteDraft(workflowId, "scope-1", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        storagePort.DeletedWorkflows.Should().ContainSingle();
        storagePort.DeletedWorkflows[0].ScopeId.Should().Be("scope-1");
        storagePort.DeletedWorkflows[0].WorkflowId.Should().Be(workflowId);
    }

    [Fact]
    public async Task DeleteDraft_WhenQueryFallbackIsDisabled_ReturnsUnauthorized()
    {
        var storagePort = new RecordingWorkflowDraftStore();
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = false,
            });

        var result = await controller.DeleteDraft("workflow-1", "scope-1", CancellationToken.None);

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        unauthorized.Value.Should().BeEquivalentTo(new
        {
            message = "Studio authentication is required before accessing a scoped workflow workspace.",
        });
        storagePort.DeletedWorkflows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraft_WhenScopedDeleteThrowsAppApiException_ReturnsStatusCodePayload()
    {
        var exception = new AppApiException(
            StatusCodes.Status502BadGateway,
            AppApiErrors.BackendInvalidResponseCode,
            "delete failed");
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new ThrowingWorkflowStoragePort(exception)),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.DeleteDraft("workflow-1", null, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        objectResult.Value.Should().BeEquivalentTo(new AppApiErrorResponse(
            AppApiErrors.BackendInvalidResponseCode,
            "delete failed"));
    }

    [Fact]
    public async Task DeleteDraft_WhenDeleteThrowsInvalidOperationException_ReturnsBadRequest()
    {
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.DeleteDraft(string.Empty, null, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequest.Value.Should().BeEquivalentTo(new { message = "workflowId is required." });
    }

    [Fact]
    public async Task CreateDraft_WhenDirectoryIdIsUnknown_ReturnsBadRequest()
    {
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.CreateDraft(
            new SaveWorkflowDraftRequest(
                DirectoryId: "missing-directory",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"),
            null,
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequest.Value.Should().BeEquivalentTo(new
        {
            message = "Workflow directory 'missing-directory' was not found.",
        });
    }

    [Fact]
    public async Task AddDirectory_WhenScopeIsResolved_ReturnsBadRequest()
    {
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.AddDirectory(
            new AddWorkflowDirectoryRequest(Path.Combine(Path.GetTempPath(), $"scoped-dir-{Guid.NewGuid():N}"), "Scoped"),
            null,
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequest.Value.Should().BeEquivalentTo(new
        {
            message = "Workflow directories are unavailable when workflows are scoped to the current login.",
        });
    }

    [Fact]
    public async Task RemoveDirectory_WhenScopeIsResolved_ReturnsBadRequest()
    {
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.RemoveDirectory("dir-1", null, CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequest.Value.Should().BeEquivalentTo(new
        {
            message = "Workflow directories are unavailable when workflows are scoped to the current login.",
        });
    }

    [Fact]
    public async Task RemoveDirectory_WhenScopeIsNotResolved_RemovesDirectory()
    {
        var store = new RecordingWorkspaceStore(Path.Combine(Path.GetTempPath(), $"workspace-remove-directory-{Guid.NewGuid():N}"));
        await store.SaveSettingsAsync(new StudioWorkspaceSettings(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories:
            [
                new StudioWorkspaceDirectory("dir-1", "Drafts", store.RootDirectory),
                new StudioWorkspaceDirectory("dir-2", "Extra", Path.Combine(store.RootDirectory, "extra"), IsBuiltIn: false),
            ],
            AppearanceTheme: "default",
            ColorMode: "system"));
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.RemoveDirectory("dir-2", null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<WorkspaceSettingsResponse>().Subject;
        payload.Directories.Should().ContainSingle(directory => directory.DirectoryId == "dir-1");
    }

    [Fact]
    public async Task LegacySaveWorkflowRoute_ReturnsWorkflowFileResponse()
    {
        var store = new RecordingWorkspaceStore(Path.Combine(Path.GetTempPath(), $"workspace-legacy-{Guid.NewGuid():N}"));
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.SaveWorkflow(
            new SaveWorkflowFileRequest(
                WorkflowId: null,
                DirectoryId: "dir-1",
                WorkflowName: "legacy-save",
                FileName: null,
                Yaml: "name: legacy-save\nsteps: []\n"),
            null,
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<WorkflowFileResponse>().Subject;
        payload.WorkflowId.Should().Be("legacy-save");
        payload.Name.Should().Be("legacy-save");
        payload.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacySaveWorkflowRoute_WhenWorkflowIdIsProvided_ReturnsWorkflowFileResponse()
    {
        var store = new RecordingWorkspaceStore(Path.Combine(Path.GetTempPath(), $"workspace-legacy-update-{Guid.NewGuid():N}"));
        store.SetWorkflowFiles([
            new StoredWorkflowFile(
                WorkflowId: "workflow-1",
                Name: "legacy-save",
                FileName: "legacy-save.yaml",
                FilePath: Path.Combine(store.RootDirectory, "legacy-save.yaml"),
                DirectoryId: "dir-1",
                DirectoryLabel: "Drafts",
                Yaml: "name: legacy-save\nsteps: []\n",
                Layout: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
        ]);
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.SaveWorkflow(
            new SaveWorkflowFileRequest(
                WorkflowId: "workflow-1",
                DirectoryId: "dir-1",
                WorkflowName: "legacy-save",
                FileName: "legacy-save-renamed.yaml",
                Yaml: "name: legacy-save\nsteps: []\n"),
            null,
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<WorkflowFileResponse>().Subject;
        payload.WorkflowId.Should().Be("workflow-1");
        payload.FileName.Should().Be("legacy-save-renamed.yaml");
        payload.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyGetWorkflowRoute_ReturnsWorkflowFileResponse()
    {
        var store = new RecordingWorkspaceStore(Path.Combine(Path.GetTempPath(), $"workspace-legacy-{Guid.NewGuid():N}"));
        store.SavedWorkflowFile = new StoredWorkflowFile(
            WorkflowId: "workflow-1",
            Name: "legacy-get",
            FileName: "legacy-get.yaml",
            FilePath: Path.Combine(store.RootDirectory, "legacy-get.yaml"),
            DirectoryId: "dir-1",
            DirectoryLabel: "Drafts",
            Yaml: "name: legacy-get\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.GetWorkflow("workflow-1", null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<WorkflowFileResponse>().Subject;
        payload.WorkflowId.Should().Be("workflow-1");
        payload.Name.Should().Be("legacy-get");
        payload.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDraft_WhenWorkspaceDraftIsMissing_ReturnsNotFound()
    {
        var store = new RecordingWorkspaceStore(Path.GetTempPath())
        {
            ReturnFallbackWorkflowFile = false,
        };
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.UpdateDraft(
            "workflow-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "dir-1",
                WorkflowName: "missing-workflow",
                FileName: null,
                Yaml: "name: missing-workflow\nsteps: []\n"),
            null,
            CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateDraft_WhenTargetPathConflicts_ReturnsConflict()
    {
        var store = new RecordingWorkspaceStore(Path.Combine(Path.GetTempPath(), $"workspace-update-conflict-{Guid.NewGuid():N}"));
        store.SetWorkflowFiles(
            new StoredWorkflowFile(
                WorkflowId: "workflow-1",
                Name: "first-workflow",
                FileName: "first.yaml",
                FilePath: Path.Combine(store.RootDirectory, "first.yaml"),
                DirectoryId: "dir-1",
                DirectoryLabel: "Drafts",
                Yaml: "name: first-workflow\nsteps: []\n",
                Layout: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            new StoredWorkflowFile(
                WorkflowId: "workflow-2",
                Name: "second-workflow",
                FileName: "second.yaml",
                FilePath: Path.Combine(store.RootDirectory, "second.yaml"),
                DirectoryId: "dir-1",
                DirectoryLabel: "Drafts",
                Yaml: "name: second-workflow\nsteps: []\n",
                Layout: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.UpdateDraft(
            "workflow-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "dir-1",
                WorkflowName: "first-workflow",
                FileName: "second.yaml",
                Yaml: "name: first-workflow\nsteps: []\n"),
            null,
            CancellationToken.None);

        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        conflict.Value.Should().BeEquivalentTo(new
        {
            code = "WORKFLOW_DRAFT_PATH_CONFLICT",
            message = "Draft 'workflow-1' cannot move to 'Drafts/second.yaml' because that path is already used by draft 'workflow-2'.",
        });
    }

    [Fact]
    public async Task LegacyListWorkflowsRoute_ReturnsWorkflowSummaries()
    {
        var store = new RecordingWorkspaceStore(Path.Combine(Path.GetTempPath(), $"workspace-legacy-{Guid.NewGuid():N}"));
        store.SavedWorkflowFile = new StoredWorkflowFile(
            WorkflowId: "workflow-1",
            Name: "legacy-list",
            FileName: "legacy-list.yaml",
            FilePath: Path.Combine(store.RootDirectory, "legacy-list.yaml"),
            DirectoryId: "dir-1",
            DirectoryLabel: "Drafts",
            Yaml: "name: legacy-list\nsteps: []\n",
            Layout: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowDraftStore()),
            new StubScopeResolver());

        var result = await controller.ListWorkflows(null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeAssignableTo<IReadOnlyList<WorkflowSummary>>().Subject;
        payload.Should().ContainSingle();
        payload[0].WorkflowId.Should().Be("workflow-1");
        payload[0].Name.Should().Be("legacy-list");
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenWorkflowIdIsBlank_DoesNotSendRequest()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.NoContent);
        var port = CreateWorkflowStoragePort(handler, enabled: true);

        await port.DeleteDraftAsync("scope-1", "   ", CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenWorkflowIdIsNull_DoesNotSendRequest()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.NoContent);
        var port = CreateWorkflowStoragePort(handler, enabled: true);

        await port.DeleteDraftAsync("scope-1", null!, CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenChronoStorageIsDisabled_DoesNotSendRequest()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.NoContent);
        var port = CreateWorkflowStoragePort(handler, enabled: false);

        await port.DeleteDraftAsync("scope-1", "workflow-1", CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenWorkflowIdIsValid_SendsDeleteToScopedObjectKey()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.NoContent);
        var port = CreateWorkflowStoragePort(handler, enabled: true, ambientScopeId: "ambient-scope");

        await port.DeleteDraftAsync("scope-1", " workflow-1 ", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Delete);
        request.RequestUri.Should().Be("https://chrono.example.com/api/buckets/test-bucket/objects?key=scope-1%2Fworkflows%2Fworkflow-1.yaml");
    }

    [Fact]
    public async Task SaveDraftAsync_WhenExplicitScopeDiffersFromAmbient_UsesRequestedScopeObjectKey()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK);
        var port = CreateWorkflowStoragePort(handler, enabled: true, ambientScopeId: "ambient-scope");

        await port.SaveDraftAsync(
            "requested-scope",
            " workflow-1 ",
            "workflow-1",
            "name: workflow-1\nsteps: []\n",
            CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Contain("key=requested-scope%2Fworkflows%2Fworkflow-1.yaml");
    }

    [Fact]
    public async Task SaveDraftAsync_WhenChronoStorageIsDisabled_ShouldThrow()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK);
        var port = CreateWorkflowStoragePort(handler, enabled: false, ambientScopeId: "ambient-scope");

        var act = () => port.SaveDraftAsync(
            "scope-1",
            "workflow-1",
            "workflow-1",
            "name: workflow-1\nsteps: []\n",
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Scoped workflow draft storage is not enabled.");
        handler.Requests.Should().BeEmpty();
    }

    private static WorkspaceController CreateController(
        WorkspaceService workspaceService,
        AppScopedWorkflowService scopeWorkflowService,
        IAppScopeResolver scopeResolver,
        StudioHostingOptions? hostingOptions = null)
    {
        var controller = new WorkspaceController(
            workspaceService,
            scopeWorkflowService,
            scopeResolver,
            Options.Create(hostingOptions ?? new StudioHostingOptions()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        return controller;
    }

    private static AppScopedWorkflowService CreateScopeWorkflowService(IWorkflowDraftStore? workflowDraftStore) =>
        new(
            new StubHttpClientFactory(new HttpClient(new ThrowingHttpMessageHandler())),
            new StubWorkflowYamlDocumentService(),
            workflowDraftStore: workflowDraftStore);

    private static ChronoStorageWorkflowDraftStore CreateWorkflowStoragePort(
        HttpMessageHandler handler,
        bool enabled,
        string ambientScopeId = "scope-1")
    {
        var blobClient = new ChronoStorageCatalogBlobClient(
            new StubScopeResolver { ScopeIdToReturn = ambientScopeId },
            new StubHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ConnectorCatalogStorageOptions
            {
                Enabled = enabled,
                UseNyxProxy = false,
                BaseUrl = "https://chrono.example.com",
                Bucket = "test-bucket",
            }));
        return new ChronoStorageWorkflowDraftStore(blobClient);
    }

    private sealed class StubScopeResolver : IAppScopeResolver
    {
        public string? ScopeIdToReturn { get; set; }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            ScopeIdToReturn is null ? null : new AppScopeContext(ScopeIdToReturn, "test");
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        private static readonly Regex NameRegex = new(@"(?m)^name:\s*(.+?)\s*$", RegexOptions.Compiled);

        public WorkflowParseResult Parse(string yaml) =>
            new(new WorkflowDocument
            {
                Name = NameRegex.Match(yaml ?? string.Empty) is var match && match.Success
                    ? match.Groups[1].Value.Trim()
                    : "workflow",
            }, []);

        public string Serialize(WorkflowDocument document) =>
            $"name: {document.Name}\nsteps: []\n";
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP client should not be used in this test.");
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public RecordingHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri?.ToString()));
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string? RequestUri);

    private sealed class RecordingWorkflowDraftStore : IWorkflowDraftStore
    {
        public List<ScopedWorkflowDelete> DeletedWorkflows { get; } = [];

        public Task SaveDraftAsync(string scopeId, string workflowId, string workflowName, string yaml, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<WorkflowDraft>> ListDraftsAsync(string scopeId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkflowDraft>>([]);

        public Task<WorkflowDraft?> GetDraftAsync(string scopeId, string workflowId, CancellationToken ct) =>
            Task.FromResult<WorkflowDraft?>(string.IsNullOrWhiteSpace(workflowId)
                ? null
                : new WorkflowDraft(
                    workflowId,
                    workflowId,
                    $"name: {workflowId}\nsteps: []\n",
                    DateTimeOffset.UtcNow));

        public Task DeleteDraftAsync(string scopeId, string workflowId, CancellationToken ct)
        {
            DeletedWorkflows.Add(new ScopedWorkflowDelete(scopeId, workflowId));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingWorkflowStoragePort : IWorkflowDraftStore
    {
        private readonly Exception _exception;

        public ThrowingWorkflowStoragePort(Exception exception)
        {
            _exception = exception;
        }

        public Task SaveDraftAsync(string scopeId, string workflowId, string workflowName, string yaml, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<WorkflowDraft>> ListDraftsAsync(string scopeId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkflowDraft>>([]);

        public Task<WorkflowDraft?> GetDraftAsync(string scopeId, string workflowId, CancellationToken ct) =>
            Task.FromResult<WorkflowDraft?>(string.IsNullOrWhiteSpace(workflowId)
                ? null
                : new WorkflowDraft(
                    workflowId,
                    workflowId,
                    $"name: {workflowId}\nsteps: []\n",
                    DateTimeOffset.UtcNow));

        public Task DeleteDraftAsync(string scopeId, string workflowId, CancellationToken ct) =>
            Task.FromException(_exception);
    }

    private sealed record ScopedWorkflowDelete(string ScopeId, string WorkflowId);

    private sealed class RecordingWorkspaceStore : IStudioWorkspaceStore
    {
        private StudioWorkspaceSettings _settings;
        private readonly List<StoredWorkflowFile> _workflowFiles = [];

        public RecordingWorkspaceStore(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            _settings = new StudioWorkspaceSettings(
                RuntimeBaseUrl: "http://127.0.0.1:5100",
                Directories:
                [
                    new StudioWorkspaceDirectory("dir-1", "Drafts", RootDirectory),
                ],
                AppearanceTheme: "default",
                ColorMode: "system");
        }

        public string RootDirectory { get; }

        public List<string> DeletedWorkflowIds { get; } = [];

        public bool ReturnFallbackWorkflowFile { get; set; } = true;

        public StoredWorkflowFile? SavedWorkflowFile
        {
            get => _workflowFiles.LastOrDefault();
            set => SetWorkflowFiles(value is null ? [] : [value]);
        }

        public void SetWorkflowFiles(params StoredWorkflowFile[] workflowFiles)
        {
            _workflowFiles.Clear();
            _workflowFiles.AddRange(workflowFiles);
        }

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task DeleteWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default)
        {
            DeletedWorkflowIds.Add(workflowId);
            return Task.CompletedTask;
        }

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowFile>>(_workflowFiles.ToList());

        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workflowId))
            {
                return Task.FromResult<StoredWorkflowFile?>(null);
            }

            var workflowFile = _workflowFiles.FirstOrDefault(file =>
                string.Equals(file.WorkflowId, workflowId, StringComparison.Ordinal));
            if (workflowFile is not null)
            {
                return Task.FromResult<StoredWorkflowFile?>(workflowFile);
            }

            if (!ReturnFallbackWorkflowFile)
            {
                return Task.FromResult<StoredWorkflowFile?>(null);
            }

            return Task.FromResult<StoredWorkflowFile?>(new StoredWorkflowFile(
                WorkflowId: workflowId,
                Name: Path.GetFileNameWithoutExtension(workflowId),
                FileName: "hello.yaml",
                FilePath: Path.Combine(RootDirectory, "drafts", "hello.yaml"),
                DirectoryId: "dir-1",
                DirectoryLabel: "Drafts",
                Yaml: "name: hello\nsteps: []\n",
                Layout: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }

        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default)
        {
            var existingIndex = _workflowFiles.FindIndex(item =>
                string.Equals(item.WorkflowId, workflowFile.WorkflowId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                _workflowFiles[existingIndex] = workflowFile;
            }
            else
            {
                _workflowFiles.Add(workflowFile);
            }

            return Task.FromResult(workflowFile);
        }

        public Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
