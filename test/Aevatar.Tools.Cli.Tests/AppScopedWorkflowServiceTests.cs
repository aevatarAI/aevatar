using System.Net;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Tools.Cli.Hosting;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Domain.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppScopedWorkflowServiceTests
{
    [Fact]
    public async Task ListAsync_WhenBackendRedirectsToLogin_ShouldThrowAuthRequiredException()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers =
            {
                Location = new Uri("https://login.example/sign-in", UriKind.Absolute),
            },
        });

        var act = () => service.ListAsync("scope-1");

        var exception = await Assert.ThrowsAsync<AppApiException>(act);
        exception.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        exception.Code.Should().Be(AppApiErrors.BackendAuthRequiredCode);
        exception.LoginUrl.Should().Be("https://login.example/sign-in");
    }

    [Fact]
    public async Task ListAsync_WhenBackendReturnsHtml_ShouldThrowInvalidResponseException()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<!DOCTYPE html><html><body>sign in</body></html>",
                Encoding.UTF8,
                "text/html"),
        });

        var act = () => service.ListAsync("scope-1");

        var exception = await Assert.ThrowsAsync<AppApiException>(act);
        exception.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        exception.Code.Should().Be(AppApiErrors.BackendInvalidResponseCode);
        exception.Message.Should().Be("Workflow backend returned a non-JSON response.");
    }

    [Fact]
    public async Task ListAsync_WhenRuntimeTargetIsRemote_ShouldBypassLocalWorkflowQueryPort()
    {
        HttpRequestMessage? capturedRequest = null;
        var runtimeTargetResolver = CreateRuntimeTargetResolver("https://api.aevatar.ai");
        var service = CreateService(
            request =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                };
            },
            runtimeTargetResolver,
            new ThrowingScopeWorkflowQueryPort());

        var workflows = await service.ListAsync("scope-1");

        workflows.Should().BeEmpty();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.ToString().Should().Be("https://api.aevatar.ai/api/scopes/scope-1/workflows");
    }

    private static AppScopedWorkflowService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        AppRuntimeTargetResolver? runtimeTargetResolver = null,
        IScopeWorkflowQueryPort? workflowQueryPort = null)
    {
        var handler = new StubHttpMessageHandler(responseFactory);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backend.example"),
        };

        return new AppScopedWorkflowService(
            new StubHttpClientFactory(httpClient),
            new StubWorkflowYamlDocumentService(),
            runtimeTargetResolver,
            workflowQueryPort);
    }

    private static AppRuntimeTargetResolver CreateRuntimeTargetResolver(string runtimeBaseUrl) =>
        new(
            new InMemoryStudioWorkspaceStore(runtimeBaseUrl),
            "http://localhost:6688",
            "http://localhost:6688",
            embeddedCapabilitiesAvailable: true);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(string name) => _httpClient;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = _responseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml) => new(null, []);

        public string Serialize(WorkflowDocument document) => string.Empty;
    }

    private sealed class ThrowingScopeWorkflowQueryPort : IScopeWorkflowQueryPort
    {
        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            throw new InvalidOperationException("The local workflow query port should not be used when a remote runtime is configured.");

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default) =>
            throw new InvalidOperationException("The local workflow query port should not be used when a remote runtime is configured.");

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(string scopeId, string actorId, CancellationToken ct = default) =>
            throw new InvalidOperationException("The local workflow query port should not be used when a remote runtime is configured.");
    }

    private sealed class InMemoryStudioWorkspaceStore : IStudioWorkspaceStore
    {
        private readonly StudioWorkspaceSettings _settings;

        public InMemoryStudioWorkspaceStore(string runtimeBaseUrl)
        {
            _settings = new StudioWorkspaceSettings(
                RuntimeBaseUrl: runtimeBaseUrl,
                Directories: [],
                AppearanceTheme: "blue",
                ColorMode: "light");
        }

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

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
