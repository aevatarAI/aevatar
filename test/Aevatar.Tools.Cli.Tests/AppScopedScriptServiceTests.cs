using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Tools.Cli.Hosting;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppScopedScriptServiceTests
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
        exception.Message.Should().Be("Script backend returned a non-JSON response.");
    }

    [Fact]
    public async Task ProposeEvolutionAsync_ShouldCallScopedBackendRoute()
    {
        HttpRequestMessage? captured = null;
        var service = CreateService(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    accepted = true,
                    proposalId = "scope-1:proposal-1",
                    scriptId = "script-1",
                    baseRevision = "rev-1",
                    candidateRevision = "rev-2",
                    status = "promoted",
                    failureReason = "",
                    definitionActorId = "definition-1",
                    catalogActorId = "catalog-1",
                    validationReport = new
                    {
                        isSuccess = true,
                        diagnostics = Array.Empty<string>(),
                    },
                }),
            };
        });

        var decision = await service.ProposeEvolutionAsync(
            "scope-1",
            new AppScopeScriptEvolutionRequest(
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "public sealed class DemoScriptV2 {}",
                CandidateSourceHash: "hash-2",
                Reason: "rollout",
                ProposalId: "proposal-1"));

        decision.Accepted.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.PathAndQuery.Should().Be("/api/scopes/scope-1/scripts/script-1/evolutions/proposals");
    }

    [Fact]
    public async Task SaveAsync_ShouldReturnCommandDetailWithoutImmediateReadBack()
    {
        var requests = new List<string>();
        var service = CreateService(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            request.Method.Should().Be(HttpMethod.Put);
            request.RequestUri!.PathAndQuery.Should().Be("/api/scopes/scope-1/scripts/script-1");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    script = new
                    {
                        scopeId = "scope-1",
                        scriptId = "script-1",
                        catalogActorId = "catalog-1",
                        definitionActorId = "definition-1",
                        activeRevision = "rev-1",
                        activeSourceHash = "hash-1",
                        updatedAt = DateTimeOffset.UtcNow,
                    },
                    revisionId = "rev-1",
                    catalogActorId = "catalog-1",
                    definitionActorId = "definition-1",
                }),
            };
        });

        var detail = await service.SaveAsync(
            "scope-1",
            new AppScopeScriptSaveRequest(
                ScriptId: "script-1",
                SourceText: "public sealed class DemoScript {}",
                RevisionId: "rev-1"));

        detail.Available.Should().BeTrue();
        detail.ScopeId.Should().Be("scope-1");
        detail.Script.Should().NotBeNull();
        detail.Script!.ScriptId.Should().Be("script-1");
        detail.Script.ActiveRevision.Should().Be("rev-1");
        detail.Source.Should().NotBeNull();
        detail.Source!.SourceText.Should().Be("public sealed class DemoScript {}");
        detail.Source.DefinitionActorId.Should().Be("definition-1");
        detail.Source.Revision.Should().Be("rev-1");
        detail.Source.SourceHash.Should().Be("hash-1");
        requests.Should().ContainSingle().Which.Should().Be("PUT /api/scopes/scope-1/scripts/script-1");
    }

    [Fact]
    public async Task ListAsync_WhenRuntimeTargetIsRemote_ShouldBypassLocalScriptQueryPort()
    {
        HttpRequestMessage? capturedRequest = null;
        var service = CreateService(
            request =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<object>()),
                };
            },
            CreateRuntimeTargetResolver("https://api.aevatar.ai"),
            new ThrowingScopeScriptQueryPort());

        var scripts = await service.ListAsync("scope-1");

        scripts.Should().BeEmpty();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.ToString().Should().Be("https://api.aevatar.ai/api/scopes/scope-1/scripts");
    }

    private static AppScopedScriptService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        AppRuntimeTargetResolver? runtimeTargetResolver = null,
        IScopeScriptQueryPort? scriptQueryPort = null)
    {
        var handler = new StubHttpMessageHandler(responseFactory);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backend.example"),
        };

        return new AppScopedScriptService(
            new StubHttpClientFactory(httpClient),
            runtimeTargetResolver,
            scriptQueryPort);
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

    private sealed class ThrowingScopeScriptQueryPort : IScopeScriptQueryPort
    {
        public Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            throw new InvalidOperationException("The local script query port should not be used when a remote runtime is configured.");

        public Task<ScopeScriptSummary?> GetByScriptIdAsync(string scopeId, string scriptId, CancellationToken ct = default) =>
            throw new InvalidOperationException("The local script query port should not be used when a remote runtime is configured.");
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
