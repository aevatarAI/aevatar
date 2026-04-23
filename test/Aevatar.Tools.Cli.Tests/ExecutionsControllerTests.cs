using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Hosting.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.Cli.Tests;

/// <summary>
/// Locks in the controller-level error contract for <c>POST /api/executions</c>. These tests
/// verify that the fail-closed paths introduced in <see cref="ExecutionService"/> surface as
/// <c>400 Bad Request</c> at the HTTP boundary instead of bubbling up as <c>500</c>.
/// </summary>
public sealed class ExecutionsControllerTests
{
    [Fact]
    public async Task Start_WhenAuthenticatedCallerHasNoScope_ShouldReturnBadRequest()
    {
        var controller = CreateController(new StubAppScopeResolver(scopeId: null, authenticatedWithoutScope: true));

        var result = await controller.Start(
            new StartExecutionRequest(
                WorkflowName: "approval",
                Prompt: "hello",
                RuntimeBaseUrl: "https://runtime.example",
                ScopeId: "scope-a",
                WorkflowId: "workflow-1"),
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ExtractMessage(badRequest).Should().Contain("no resolvable scope");
    }

    [Fact]
    public async Task Start_WhenRequestedScopeDoesNotMatchAuthenticatedScope_ShouldReturnBadRequest()
    {
        var controller = CreateController(new StubAppScopeResolver(scopeId: "scope-a"));

        var result = await controller.Start(
            new StartExecutionRequest(
                WorkflowName: "approval",
                Prompt: "hello",
                RuntimeBaseUrl: "https://runtime.example",
                ScopeId: "scope-b",
                WorkflowId: "workflow-1"),
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ExtractMessage(badRequest).Should().Contain("does not match the authenticated Studio scope");
    }

    [Fact]
    public async Task Start_WhenScopeOrWorkflowMissing_ShouldReturnBadRequest()
    {
        var controller = CreateController(scopeResolver: null);

        var result = await controller.Start(
            new StartExecutionRequest(
                WorkflowName: "approval",
                Prompt: "hello",
                RuntimeBaseUrl: "https://runtime.example"),
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ExtractMessage(badRequest).Should().Contain("scopeId and workflowId are required");
    }

    private static ExecutionsController CreateController(IAppScopeResolver? scopeResolver)
    {
        var handler = new NoHttpMessageHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backend.example"),
        };
        var service = new ExecutionService(
            new NoOpWorkspaceStore(),
            new StubHttpClientFactory(httpClient),
            authSnapshotProvider: null,
            userConfigStore: null,
            scopeResolver: scopeResolver);
        return new ExecutionsController(service);
    }

    private static string ExtractMessage(BadRequestObjectResult badRequest)
    {
        var value = badRequest.Value;
        if (value is null)
            return string.Empty;

        var property = value.GetType().GetProperty("message");
        return property?.GetValue(value) as string ?? string.Empty;
    }

    private sealed class StubAppScopeResolver : IAppScopeResolver
    {
        private readonly AppScopeContext? _context;
        private readonly bool _authenticatedWithoutScope;

        public StubAppScopeResolver(string? scopeId, bool authenticatedWithoutScope = false)
        {
            _context = scopeId is null ? null : new AppScopeContext(scopeId, "test:stub");
            _authenticatedWithoutScope = authenticatedWithoutScope;
        }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) => _context;

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null)
            => _authenticatedWithoutScope;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpClient httpClient)
            => _httpClient = httpClient;

        public HttpClient CreateClient(string name) => _httpClient;
    }

    private sealed class NoHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                $"Controller test must fail before issuing an HTTP call. Unexpected request: {request.RequestUri}");
    }

    private sealed class NoOpWorkspaceStore : IStudioWorkspaceStore
    {
        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioWorkspaceSettings("http://127.0.0.1:5100", [], "blue", "light"));

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowFile>>([]);

        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredWorkflowFile?>(null);

        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredExecutionRecord>>([]);

        public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredExecutionRecord?>(null);

        public Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default) =>
            Task.FromResult(execution);

        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredConnectorCatalog("", "", false, []));

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default) =>
            Task.FromResult(catalog);

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredConnectorDraft("", "", false, null, null));

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromResult(draft);

        public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredRoleCatalog("", "", false, []));

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, CancellationToken cancellationToken = default) =>
            Task.FromResult(catalog);

        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredRoleDraft("", "", false, null, null));

        public Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromResult(draft);

        public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
