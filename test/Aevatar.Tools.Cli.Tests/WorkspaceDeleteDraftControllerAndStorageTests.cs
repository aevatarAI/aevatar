using System.Net;
using System.Text.RegularExpressions;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Hosting.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class WorkspaceDeleteDraftControllerAndStorageTests
{
    [Fact]
    public async Task GetSettings_WhenScopeIsResolved_ReturnsScopedDirectoryOnly()
    {
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
    public async Task GetSettings_WhenQueryFallbackIsEnabledOutsideDevelopment_ReturnsUnauthorized()
    {
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = true,
            },
            Environments.Production);

        var result = await controller.GetSettings("scope-1", CancellationToken.None);

        var unauthorized = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        unauthorized.Value.Should().BeEquivalentTo(new
        {
            message = "Studio authentication is required before accessing a scoped workflow workspace.",
        });
    }

    [Fact]
    public async Task UpdateSettings_ReturnsNormalizedRuntimeBaseUrl()
    {
        var store = new RecordingWorkspaceStore(Path.GetTempPath());
        var controller = CreateController(
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
            new StubScopeResolver());
        var workflowPath = Path.Combine(workspaceRoot, "drafts", "hello.yaml");
        var workflowId = WorkspaceService.CreateStableId(workflowPath);
        store.SavedWorkflowFile = store.CreateWorkflowDraft(workflowId);

        var result = await controller.DeleteDraft(workflowId, null, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        store.DeletedWorkflowIds.Should().ContainSingle().Which.Should().Be(workflowId);
    }

    [Fact]
    public async Task DeleteDraft_WhenScopeIsResolved_DeletesScopedDraftAndReturnsNoContent()
    {
        var workflowId = $"workflow-{Guid.NewGuid():N}";
        var storagePort = new RecordingScopedWorkspacePorts([
            new ScopedDraft(
                "scope-1",
                NewScopedDraft(workflowId, workflowId, $"name: {workflowId}\nsteps: []\n", DateTimeOffset.UtcNow)),
        ]);
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.DeleteDraft(workflowId, null, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        storagePort.DeletedWorkflows.Should().ContainSingle();
        storagePort.DeletedWorkflows[0].ScopeId.Should().Be("scope-1");
        storagePort.DeletedWorkflows[0].WorkflowId.Should().Be(workflowId);
    }

    [Fact]
    public async Task DeleteDraft_WhenQueryFallbackIsEnabled_ReturnsUnauthorizedForScopedWrites()
    {
        var storagePort = new RecordingScopedWorkspacePorts();
        var workflowId = $"workflow-{Guid.NewGuid():N}";
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = true,
            });

        var result = await controller.DeleteDraft(workflowId, "scope-1", CancellationToken.None);

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        unauthorized.Value.Should().BeEquivalentTo(new
        {
            message = "Studio authentication is required before mutating a scoped workflow workspace.",
        });
        storagePort.DeletedWorkflows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraft_WhenQueryFallbackIsDisabled_ReturnsUnauthorized()
    {
        var storagePort = new RecordingScopedWorkspacePorts();
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
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
            message = "Studio authentication is required before mutating a scoped workflow workspace.",
        });
        storagePort.DeletedWorkflows.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateDraft_WhenQueryFallbackIsEnabled_ReturnsUnauthorizedForScopedWrites()
    {
        var storagePort = new RecordingScopedWorkspacePorts();
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = true,
            });

        var result = await controller.CreateDraft(
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"),
            "scope-1",
            CancellationToken.None);

        var unauthorized = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        unauthorized.Value.Should().BeEquivalentTo(new
        {
            message = "Studio authentication is required before mutating a scoped workflow workspace.",
        });
        storagePort.SavedWorkflows.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDraft_WhenQueryFallbackIsEnabled_ReturnsUnauthorizedForScopedWrites()
    {
        var storagePort = new RecordingScopedWorkspacePorts();
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = true,
            });

        var result = await controller.UpdateDraft(
            "workflow-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"),
            "scope-1",
            CancellationToken.None);

        var unauthorized = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        unauthorized.Value.Should().BeEquivalentTo(new
        {
            message = "Studio authentication is required before mutating a scoped workflow workspace.",
        });
        storagePort.SavedWorkflows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraft_WhenScopedDeleteThrowsAppApiException_ReturnsStatusCodePayload()
    {
        var exception = new AppApiException(
            StatusCodes.Status502BadGateway,
            AppApiErrors.BackendInvalidResponseCode,
            "delete failed");
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new ThrowingScopedWorkspaceCommandPort(exception)),
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
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
    public async Task AddDirectory_WhenQueryFallbackIsEnabled_ReturnsUnauthorizedForScopedWrites()
    {
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = true,
            });

        var result = await controller.AddDirectory(
            new AddWorkflowDirectoryRequest(Path.Combine(Path.GetTempPath(), $"scoped-dir-{Guid.NewGuid():N}"), "Scoped"),
            "scope-1",
            CancellationToken.None);

        var unauthorized = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        unauthorized.Value.Should().BeEquivalentTo(new
        {
            message = "Studio authentication is required before mutating a scoped workflow workspace.",
        });
    }

    [Fact]
    public async Task RemoveDirectory_WhenScopeIsResolved_ReturnsBadRequest()
    {
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
    public async Task RemoveDirectory_WhenQueryFallbackIsEnabled_ReturnsUnauthorizedForScopedWrites()
    {
        var controller = CreateController(
            CreateWorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath())),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = true,
            });

        var result = await controller.RemoveDirectory("dir-1", "scope-1", CancellationToken.None);

        var unauthorized = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        unauthorized.Value.Should().BeEquivalentTo(new
        {
            message = "Studio authentication is required before mutating a scoped workflow workspace.",
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
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
            store.CreateWorkflowDraft(
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
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
        store.SavedWorkflowFile = store.CreateWorkflowDraft(
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
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
        var store = new RecordingWorkspaceStore(Path.GetTempPath());
        var controller = CreateController(
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
            store.CreateWorkflowDraft(
                WorkflowId: "workflow-1",
                Name: "first-workflow",
                FileName: "first.yaml",
                FilePath: Path.Combine(store.RootDirectory, "first.yaml"),
                DirectoryId: "dir-1",
                DirectoryLabel: "Drafts",
                Yaml: "name: first-workflow\nsteps: []\n",
                Layout: null,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            store.CreateWorkflowDraft(
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
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
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
        store.SavedWorkflowFile = store.CreateWorkflowDraft(
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
            CreateWorkspaceService(store),
            CreateScopeWorkflowService(new RecordingScopedWorkspacePorts()),
            new StubScopeResolver());

        var result = await controller.ListWorkflows(null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeAssignableTo<IReadOnlyList<WorkflowSummary>>().Subject;
        payload.Should().ContainSingle();
        payload[0].WorkflowId.Should().Be("workflow-1");
        payload[0].Name.Should().Be("legacy-list");
    }

    private static WorkspaceController CreateController(
        WorkspaceService workspaceService,
        AppScopedWorkflowService scopeWorkflowService,
        IAppScopeResolver scopeResolver,
        StudioHostingOptions? hostingOptions = null,
        string environmentName = "Development")
    {
        var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = environmentName })
            .BuildServiceProvider();
        var controller = new WorkspaceController(
            workspaceService,
            scopeWorkflowService,
            scopeResolver,
            Options.Create(hostingOptions ?? new StudioHostingOptions()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                },
            },
        };
        return controller;
    }

    private static WorkspaceService CreateWorkspaceService(RecordingWorkspaceStore store) =>
        new(store, store, new StubWorkflowYamlDocumentService());

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.Tools.Cli.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static AppScopedWorkflowService CreateScopeWorkflowService(RecordingScopedWorkspacePorts workspacePorts) =>
        new(
            new StubHttpClientFactory(new HttpClient(new ThrowingHttpMessageHandler())),
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: workspacePorts,
            workspaceCommandPort: workspacePorts);

    private static AppScopedWorkflowService CreateScopeWorkflowService(ThrowingScopedWorkspaceCommandPort commandPort) =>
        new(
            new StubHttpClientFactory(new HttpClient(new ThrowingHttpMessageHandler())),
            new StubWorkflowYamlDocumentService(),
            workspaceQueryPort: new RecordingScopedWorkspacePorts([
                new ScopedDraft(
                    "scope-1",
                    NewScopedDraft("workflow-1", "workflow-1", "name: workflow-1\nsteps: []\n", DateTimeOffset.UtcNow)),
            ]),
            workspaceCommandPort: commandPort);

    private sealed class StubScopeResolver : IAppScopeResolver
    {
        public string? ScopeIdToReturn { get; set; }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            ScopeIdToReturn is null ? null : new AppScopeContext(ScopeIdToReturn, "test");

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
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

    // Refactor (iter38/cluster-038-studio-workspace-reuse-existing):
    //   Old pattern: Studio scoped workflow drafts 通过 ChronoStorage external storage authority + workspace ports routing 不一致(scopeId routing 显式 vs 隐藏)。
    //   New principle: Delete ChronoStorage draft authority。Route scoped workflow drafts through existing IStudioWorkspaceCommandPort / IStudioWorkspaceQueryPort with explicit scopeId。**禁止** new IScopedStudioWorkspacePort / 新 scoped actor / 新 envelope / 新 projection phase / docs/canon change。
    private sealed class RecordingScopedWorkspacePorts : IStudioWorkspaceQueryPort, IStudioWorkspaceCommandPort
    {
        public List<ScopedWorkflowSave> SavedWorkflows { get; } = [];
        public List<ScopedWorkflowDelete> DeletedWorkflows { get; } = [];
        private readonly Dictionary<string, Dictionary<string, StudioWorkflowDraftRecord>> _drafts = new(StringComparer.Ordinal);

        public RecordingScopedWorkspacePorts()
        {
        }

        public RecordingScopedWorkspacePorts(IEnumerable<ScopedDraft> drafts)
        {
            foreach (var draft in drafts)
            {
                GetOrCreateScope(draft.ScopeId)[draft.Draft.WorkflowId] = draft.Draft;
            }
        }

        public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
            GetAsync("scope-1", ct);

        public Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default)
        {
            _drafts.TryGetValue(scopeId, out var scopeDrafts);
            return Task.FromResult(new StudioWorkspaceSnapshot(
                $"studio-workspace:{scopeId}",
                scopeId,
                new StudioWorkspaceSettings(
                    UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
                    [new StudioWorkspaceDirectory($"scope:{scopeId}", scopeId, $"scope://{scopeId}", true)],
                    "blue",
                    "light"),
                [new StudioWorkspaceDirectory($"scope:{scopeId}", scopeId, $"scope://{scopeId}", true)],
                scopeDrafts?.Values.ToList() ?? [],
                5,
                DateTimeOffset.UtcNow));
        }

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
            StudioWorkflowDraftRecord draft,
            long? expectedVersion = null,
            CancellationToken ct = default) =>
            SaveDraftAsync("scope-1", draft, expectedVersion, ct);

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
            string scopeId,
            StudioWorkflowDraftRecord draft,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            SavedWorkflows.Add(new ScopedWorkflowSave(scopeId, draft.WorkflowId, draft.Name, draft.Yaml));
            GetOrCreateScope(scopeId)[draft.WorkflowId] = draft;
            return Task.FromResult(Receipt(scopeId, expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
            string workflowId,
            long? expectedVersion = null,
            CancellationToken ct = default) =>
            DeleteDraftAsync("scope-1", workflowId, expectedVersion, ct);

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
            string scopeId,
            string workflowId,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            DeletedWorkflows.Add(new ScopedWorkflowDelete(scopeId, workflowId));
            GetOrCreateScope(scopeId).Remove(workflowId);
            return Task.FromResult(Receipt(scopeId, expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(StudioWorkspaceSettings settings, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromResult(Receipt("scope-1", expectedVersion));

        public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(StudioWorkspaceDirectory directory, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromResult(Receipt("scope-1", expectedVersion));

        public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(string directoryId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromResult(Receipt("scope-1", expectedVersion));

        private Dictionary<string, StudioWorkflowDraftRecord> GetOrCreateScope(string scopeId)
        {
            if (_drafts.TryGetValue(scopeId, out var scopeDrafts))
                return scopeDrafts;

            scopeDrafts = new Dictionary<string, StudioWorkflowDraftRecord>(StringComparer.Ordinal);
            _drafts[scopeId] = scopeDrafts;
            return scopeDrafts;
        }

        private static StudioWorkspaceCommandReceipt Receipt(string scopeId, long? expectedVersion) =>
            new($"studio-workspace:{scopeId}", $"studio-workspace:{scopeId}", Guid.NewGuid().ToString("N"), expectedVersion);
    }

    private sealed class ThrowingScopedWorkspaceCommandPort : IStudioWorkspaceCommandPort
    {
        private readonly Exception _exception;

        public ThrowingScopedWorkspaceCommandPort(Exception exception)
        {
            _exception = exception;
        }

        public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(StudioWorkspaceSettings settings, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(StudioWorkspaceDirectory directory, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(string directoryId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(StudioWorkflowDraftRecord draft, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(string workflowId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(string scopeId, string workflowId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);
    }

    private sealed record ScopedWorkflowSave(string ScopeId, string WorkflowId, string WorkflowName, string Yaml);
    private sealed record ScopedWorkflowDelete(string ScopeId, string WorkflowId);
    private sealed record ScopedDraft(string ScopeId, StudioWorkflowDraftRecord Draft);

    private static StudioWorkflowDraftRecord NewScopedDraft(
        string workflowId,
        string name,
        string yaml,
        DateTimeOffset updatedAtUtc) =>
        new(
            workflowId,
            name,
            $"{workflowId}.yaml",
            $"scope://scope-1/{workflowId}.yaml",
            "scope:scope-1",
            "scope-1",
            yaml,
            Layout: null,
            updatedAtUtc,
            updatedAtUtc,
            1);

    private sealed class RecordingWorkspaceStore : IStudioWorkspaceQueryPort, IStudioWorkspaceCommandPort
    {
        private StudioWorkspaceSettings _settings;
        private readonly List<StudioWorkflowDraftRecord> _workflowFiles = [];
        private long _stateVersion;

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

        public StudioWorkflowDraftRecord? SavedWorkflowFile
        {
            get => _workflowFiles.LastOrDefault();
            set => SetWorkflowFiles(value is null ? [] : [value]);
        }

        public void SetWorkflowFiles(params StudioWorkflowDraftRecord[] workflowFiles)
        {
            _workflowFiles.Clear();
            _workflowFiles.AddRange(workflowFiles);
        }

        public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new StudioWorkspaceSnapshot(
                "workspace-test",
                "scope-test",
                _settings,
                _settings.Directories,
                _workflowFiles.ToList(),
                _stateVersion,
                DateTimeOffset.UtcNow));

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
            string workflowId,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            DeletedWorkflowIds.Add(workflowId);
            _workflowFiles.RemoveAll(file => string.Equals(file.WorkflowId, workflowId, StringComparison.Ordinal));
            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(
            StudioWorkspaceSettings settings,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            _settings = settings;
            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(
            StudioWorkspaceDirectory directory,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            _settings = _settings with { Directories = _settings.Directories.Append(directory).ToList() };
            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(
            string directoryId,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            _settings = _settings with
            {
                Directories = _settings.Directories
                    .Where(directory => directory.IsBuiltIn || !string.Equals(directory.DirectoryId, directoryId, StringComparison.Ordinal))
                    .ToList(),
            };
            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
            StudioWorkflowDraftRecord draft,
            long? expectedVersion = null,
            CancellationToken ct = default)
        {
            var existingIndex = _workflowFiles.FindIndex(item =>
                string.Equals(item.WorkflowId, draft.WorkflowId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                _workflowFiles[existingIndex] = draft;
            }
            else
            {
                _workflowFiles.Add(draft);
            }

            _stateVersion++;
            return Task.FromResult(Receipt(expectedVersion));
        }

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        private static StudioWorkspaceCommandReceipt Receipt(long? expectedVersion) =>
            new("workspace-test", "workspace-test", Guid.NewGuid().ToString("N"), expectedVersion);

        public StudioWorkflowDraftRecord CreateWorkflowDraft(
            string WorkflowId) =>
            CreateWorkflowDraft(
                WorkflowId,
                Path.GetFileNameWithoutExtension(WorkflowId),
                "hello.yaml",
                Path.Combine(RootDirectory, "drafts", "hello.yaml"),
                "dir-1",
                "Drafts",
                "name: hello\nsteps: []\n",
                Layout: null,
                DateTimeOffset.UtcNow);

        public StudioWorkflowDraftRecord CreateWorkflowDraft(
            string WorkflowId,
            string Name,
            string FileName,
            string FilePath,
            string DirectoryId,
            string DirectoryLabel,
            string Yaml,
            WorkflowLayoutDocument? Layout,
            DateTimeOffset UpdatedAtUtc) =>
            new(
                WorkflowId,
                Name,
                FileName,
                FilePath,
                DirectoryId,
                DirectoryLabel,
                Yaml,
                Layout,
                UpdatedAtUtc,
                UpdatedAtUtc,
                Version: 1);
    }
}
