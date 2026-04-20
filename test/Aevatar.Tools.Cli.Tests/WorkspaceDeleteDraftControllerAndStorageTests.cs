using System.Net;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
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
    public async Task DeleteWorkflow_WhenScopeIsNotResolved_DeletesWorkspaceDraftAndReturnsNoContent()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"workspace-delete-{Guid.NewGuid():N}");
        var store = new RecordingWorkspaceStore(workspaceRoot);
        var controller = CreateController(
            new WorkspaceService(store, new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowStoragePort()),
            new StubScopeResolver());
        var workflowPath = Path.Combine(workspaceRoot, "drafts", "hello.yaml");
        var workflowId = WorkspaceService.CreateStableId(workflowPath);

        var result = await controller.DeleteWorkflow(workflowId, null, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        store.DeletedWorkflowIds.Should().ContainSingle().Which.Should().Be(workflowId);
    }

    [Fact]
    public async Task DeleteWorkflow_WhenScopeIsResolved_DeletesScopedDraftAndReturnsNoContent()
    {
        var storagePort = new RecordingWorkflowStoragePort();
        var workflowId = $"workflow-{Guid.NewGuid():N}";
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.DeleteWorkflow(workflowId, null, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        storagePort.DeletedWorkflows.Should().ContainSingle();
        storagePort.DeletedWorkflows[0].ScopeId.Should().Be("scope-1");
        storagePort.DeletedWorkflows[0].WorkflowId.Should().Be(workflowId);
    }

    [Fact]
    public async Task DeleteWorkflow_WhenQueryFallbackIsEnabled_UsesRequestedScopeId()
    {
        var storagePort = new RecordingWorkflowStoragePort();
        var workflowId = $"workflow-{Guid.NewGuid():N}";
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = true,
            });

        var result = await controller.DeleteWorkflow(workflowId, "scope-1", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        storagePort.DeletedWorkflows.Should().ContainSingle();
        storagePort.DeletedWorkflows[0].ScopeId.Should().Be("scope-1");
        storagePort.DeletedWorkflows[0].WorkflowId.Should().Be(workflowId);
    }

    [Fact]
    public async Task DeleteWorkflow_WhenQueryFallbackIsDisabled_ReturnsUnauthorized()
    {
        var storagePort = new RecordingWorkflowStoragePort();
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(storagePort),
            new StubScopeResolver(),
            new StudioHostingOptions
            {
                AllowUnauthenticatedScopeQueryFallback = false,
            });

        var result = await controller.DeleteWorkflow("workflow-1", "scope-1", CancellationToken.None);

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        unauthorized.Value.Should().BeEquivalentTo(new
        {
            message = "Studio authentication is required before accessing a scoped workflow workspace.",
        });
        storagePort.DeletedWorkflows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteWorkflow_WhenScopedDeleteThrowsAppApiException_ReturnsStatusCodePayload()
    {
        var exception = new AppApiException(
            StatusCodes.Status502BadGateway,
            AppApiErrors.BackendInvalidResponseCode,
            "delete failed");
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new ThrowingWorkflowStoragePort(exception)),
            new StubScopeResolver { ScopeIdToReturn = "scope-1" });

        var result = await controller.DeleteWorkflow("workflow-1", null, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        objectResult.Value.Should().BeEquivalentTo(new AppApiErrorResponse(
            AppApiErrors.BackendInvalidResponseCode,
            "delete failed"));
    }

    [Fact]
    public async Task DeleteWorkflow_WhenDeleteThrowsInvalidOperationException_ReturnsBadRequest()
    {
        var controller = CreateController(
            new WorkspaceService(new RecordingWorkspaceStore(Path.GetTempPath()), new StubWorkflowYamlDocumentService()),
            CreateScopeWorkflowService(new RecordingWorkflowStoragePort()),
            new StubScopeResolver());

        var result = await controller.DeleteWorkflow(string.Empty, null, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequest.Value.Should().BeEquivalentTo(new { message = "workflowId is required." });
    }

    [Fact]
    public async Task DeleteWorkflowYamlAsync_WhenWorkflowIdIsBlank_DoesNotSendRequest()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.NoContent);
        var port = CreateWorkflowStoragePort(handler, enabled: true);

        await port.DeleteWorkflowYamlAsync("scope-1", "   ", CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteWorkflowYamlAsync_WhenWorkflowIdIsNull_DoesNotSendRequest()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.NoContent);
        var port = CreateWorkflowStoragePort(handler, enabled: true);

        await port.DeleteWorkflowYamlAsync("scope-1", null!, CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteWorkflowYamlAsync_WhenChronoStorageIsDisabled_DoesNotSendRequest()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.NoContent);
        var port = CreateWorkflowStoragePort(handler, enabled: false);

        await port.DeleteWorkflowYamlAsync("scope-1", "workflow-1", CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteWorkflowYamlAsync_WhenWorkflowIdIsValid_SendsDeleteToScopedObjectKey()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.NoContent);
        var port = CreateWorkflowStoragePort(handler, enabled: true, ambientScopeId: "ambient-scope");

        await port.DeleteWorkflowYamlAsync("scope-1", " workflow-1 ", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Delete);
        request.RequestUri.Should().Be("https://chrono.example.com/api/buckets/test-bucket/objects?key=scope-1%2Fworkflows%2Fworkflow-1.yaml");
    }

    [Fact]
    public async Task UploadWorkflowYamlAsync_WhenExplicitScopeDiffersFromAmbient_UsesRequestedScopeObjectKey()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK);
        var port = CreateWorkflowStoragePort(handler, enabled: true, ambientScopeId: "ambient-scope");

        await port.UploadWorkflowYamlAsync(
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
    public async Task UploadWorkflowYamlAsync_WhenChronoStorageIsDisabled_ShouldThrow()
    {
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.OK);
        var port = CreateWorkflowStoragePort(handler, enabled: false, ambientScopeId: "ambient-scope");

        var act = () => port.UploadWorkflowYamlAsync(
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

    private static AppScopedWorkflowService CreateScopeWorkflowService(IWorkflowStoragePort? workflowStoragePort) =>
        new(
            new StubHttpClientFactory(new HttpClient(new ThrowingHttpMessageHandler())),
            new StubWorkflowYamlDocumentService(),
            workflowStoragePort: workflowStoragePort);

    private static ChronoStorageWorkflowStoragePort CreateWorkflowStoragePort(
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
        return new ChronoStorageWorkflowStoragePort(blobClient);
    }

    private sealed class StubScopeResolver : IAppScopeResolver
    {
        public string? ScopeIdToReturn { get; set; }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            ScopeIdToReturn is null ? null : new AppScopeContext(ScopeIdToReturn, "test");
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml) =>
            new(new WorkflowDocument { Name = "workflow" }, []);

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

    private sealed class RecordingWorkflowStoragePort : IWorkflowStoragePort
    {
        public List<ScopedWorkflowDelete> DeletedWorkflows { get; } = [];

        public Task UploadWorkflowYamlAsync(string scopeId, string workflowId, string workflowName, string yaml, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StoredWorkflowYaml>> ListWorkflowYamlsAsync(string scopeId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowYaml>>([]);

        public Task<StoredWorkflowYaml?> GetWorkflowYamlAsync(string scopeId, string workflowId, CancellationToken ct) =>
            Task.FromResult<StoredWorkflowYaml?>(null);

        public Task DeleteWorkflowYamlAsync(string scopeId, string workflowId, CancellationToken ct)
        {
            DeletedWorkflows.Add(new ScopedWorkflowDelete(scopeId, workflowId));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingWorkflowStoragePort : IWorkflowStoragePort
    {
        private readonly Exception _exception;

        public ThrowingWorkflowStoragePort(Exception exception)
        {
            _exception = exception;
        }

        public Task UploadWorkflowYamlAsync(string scopeId, string workflowId, string workflowName, string yaml, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StoredWorkflowYaml>> ListWorkflowYamlsAsync(string scopeId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowYaml>>([]);

        public Task<StoredWorkflowYaml?> GetWorkflowYamlAsync(string scopeId, string workflowId, CancellationToken ct) =>
            Task.FromResult<StoredWorkflowYaml?>(null);

        public Task DeleteWorkflowYamlAsync(string scopeId, string workflowId, CancellationToken ct) =>
            Task.FromException(_exception);
    }

    private sealed record ScopedWorkflowDelete(string ScopeId, string WorkflowId);

    private sealed class RecordingWorkspaceStore : IStudioWorkspaceStore
    {
        public RecordingWorkspaceStore(string rootDirectory)
        {
            RootDirectory = rootDirectory;
        }

        public string RootDirectory { get; }

        public List<string> DeletedWorkflowIds { get; } = [];

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioWorkspaceSettings(
                RuntimeBaseUrl: "http://127.0.0.1:5100",
                Directories:
                [
                    new StudioWorkspaceDirectory("dir-1", "Drafts", RootDirectory),
                ],
                AppearanceTheme: "default",
                ColorMode: "system"));

        public Task DeleteWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default)
        {
            DeletedWorkflowIds.Add(workflowId);
            return Task.CompletedTask;
        }

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
