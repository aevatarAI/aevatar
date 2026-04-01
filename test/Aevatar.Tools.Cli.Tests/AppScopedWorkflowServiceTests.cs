using System.Net;
using System.Text;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Workflow.Application.Abstractions.Runs;
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
    public async Task GetAsync_WhenLifecycleQueryPortIsUnavailable_ShouldSkipRevisionFallback()
    {
        var workflow = new ScopeWorkflowSummary(
            ScopeId: "scope-1",
            WorkflowId: "workflow-1",
            DisplayName: "Workflow 1",
            ServiceKey: "scope-1:default:default:workflow-1",
            WorkflowName: "Workflow 1",
            ActorId: "actor-1",
            ActiveRevisionId: string.Empty,
            DeploymentId: string.Empty,
            DeploymentStatus: "active",
            UpdatedAt: DateTimeOffset.UtcNow);

        var service = new AppScopedWorkflowService(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP backend should not be called.")))
            {
                BaseAddress = new Uri("https://backend.example"),
            }),
            new StubWorkflowYamlDocumentService(),
            new StubScopeWorkflowQueryPort(workflow),
            workflowActorBindingReader: new StubWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    "actor-1",
                    "actor-1",
                    string.Empty,
                    "Workflow 1",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal))),
            artifactStore: new StubArtifactStore());

        var response = await service.GetAsync("scope-1", "workflow-1");

        response.Should().NotBeNull();
        response!.Yaml.Should().BeEmpty();
        response.Findings.Should().ContainSingle();
        response.Findings[0].Message.Should().Be("Workflow YAML is not available yet.");
    }

    private static AppScopedWorkflowService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var handler = new StubHttpMessageHandler(responseFactory);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backend.example"),
        };

        return new AppScopedWorkflowService(
            new StubHttpClientFactory(httpClient),
            new StubWorkflowYamlDocumentService());
    }

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

    private sealed class StubScopeWorkflowQueryPort : IScopeWorkflowQueryPort
    {
        private readonly ScopeWorkflowSummary _workflow;

        public StubScopeWorkflowQueryPort(ScopeWorkflowSummary workflow)
        {
            _workflow = workflow;
        }

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>([_workflow]);

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(string.Equals(workflowId, _workflow.WorkflowId, StringComparison.Ordinal) ? _workflow : null);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(string scopeId, string actorId, CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(string.Equals(actorId, _workflow.ActorId, StringComparison.Ordinal) ? _workflow : null);
    }

    private sealed class StubWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly WorkflowActorBinding _binding;

        public StubWorkflowActorBindingReader(WorkflowActorBinding binding)
        {
            _binding = binding;
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorBinding?>(string.Equals(actorId, _binding.ActorId, StringComparison.Ordinal) ? _binding : null);
    }

    private sealed class StubArtifactStore : IServiceRevisionArtifactStore
    {
        public Task SaveAsync(string serviceKey, string revisionId, PreparedServiceRevisionArtifact artifact, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<PreparedServiceRevisionArtifact?> GetAsync(string serviceKey, string revisionId, CancellationToken ct = default) =>
            Task.FromResult<PreparedServiceRevisionArtifact?>(null);
    }
}
