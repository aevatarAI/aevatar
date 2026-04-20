using System.Net;
using System.Text;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;

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

    [Fact]
    public async Task SaveAsync_ShouldRewriteYamlNameFromRequestedWorkflowName()
    {
        var storagePort = new StubWorkflowStoragePort();
        var service = new AppScopedWorkflowService(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP backend should not be called.")))
            {
                BaseAddress = new Uri("https://backend.example"),
            }),
            new StubWorkflowYamlDocumentService(),
            workflowStoragePort: storagePort);

        var response = await service.SaveAsync(
            "scope-1",
            new SaveWorkflowFileRequest(
                WorkflowId: null,
                DirectoryId: "scope:scope-1",
                WorkflowName: "renamed-workflow",
                FileName: null,
                Yaml: "name: draft\nsteps: []\n"));

        storagePort.LastUpload.Should().NotBeNull();
        storagePort.LastUpload!.WorkflowId.Should().Be("renamed-workflow");
        storagePort.LastUpload.WorkflowName.Should().Be("renamed-workflow");
        storagePort.LastUpload.Yaml.Should().StartWith("name: renamed-workflow");
        response.Name.Should().Be("renamed-workflow");
        response.Yaml.Should().StartWith("name: renamed-workflow");
    }

    [Fact]
    public async Task ListAsync_WhenRuntimeListIsEmpty_ShouldFallbackToStoredWorkflowYaml()
    {
        var service = new AppScopedWorkflowService(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP backend should not be called.")))
            {
                BaseAddress = new Uri("https://backend.example"),
            }),
            new StubWorkflowYamlDocumentService(),
            workflowQueryPort: new StubScopeWorkflowQueryPort(),
            workflowStoragePort: new StubWorkflowStoragePort(
                new StoredWorkflowYaml(
                    "hello-chat",
                    "hello-chat",
                    "name: hello-chat\ndescription: stored workflow\nsteps: []\n",
                    new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero))));

        var workflows = await service.ListAsync("scope-1");

        workflows.Should().ContainSingle();
        workflows[0].WorkflowId.Should().Be("hello-chat");
        workflows[0].Name.Should().Be("hello-chat");
        workflows[0].Description.Should().Be("stored workflow");
    }

    [Fact]
    public async Task ListAsync_WhenRuntimeWorkflowExists_ShouldUseStoredYamlToPopulateStepCount()
    {
        var storedUpdatedAt = new DateTimeOffset(2026, 4, 16, 10, 53, 48, TimeSpan.Zero);
        var workflow = new ScopeWorkflowSummary(
            ScopeId: "scope-1",
            WorkflowId: "test03",
            DisplayName: "test03",
            ServiceKey: "scope-1:default:default:test03",
            WorkflowName: "test03",
            ActorId: "actor-1",
            ActiveRevisionId: "rev-1",
            DeploymentId: "deploy-1",
            DeploymentStatus: "active",
            UpdatedAt: storedUpdatedAt.AddMinutes(-10));

        var service = new AppScopedWorkflowService(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP backend should not be called.")))
            {
                BaseAddress = new Uri("https://backend.example"),
            }),
            new StubWorkflowYamlDocumentService(),
            workflowQueryPort: new StubScopeWorkflowQueryPort(workflow),
            workflowStoragePort: new StubWorkflowStoragePort(
                new StoredWorkflowYaml(
                    "test03",
                    "test03",
                    "name: test03\ndescription: restored from storage\nsteps:\n  - id: llm_call\n",
                    storedUpdatedAt)));

        var workflows = await service.ListAsync("scope-1");

        workflows.Should().ContainSingle();
        workflows[0].WorkflowId.Should().Be("test03");
        workflows[0].StepCount.Should().Be(1);
        workflows[0].Description.Should().Be("restored from storage");
        workflows[0].UpdatedAtUtc.Should().Be(storedUpdatedAt);
    }

    [Fact]
    public async Task GetAsync_WhenRuntimeWorkflowMissing_ShouldFallbackToStoredWorkflowYaml()
    {
        var service = new AppScopedWorkflowService(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP backend should not be called.")))
            {
                BaseAddress = new Uri("https://backend.example"),
            }),
            new StubWorkflowYamlDocumentService(),
            workflowQueryPort: new StubScopeWorkflowQueryPort(),
            workflowActorBindingReader: new StubWorkflowActorBindingReader(null),
            workflowStoragePort: new StubWorkflowStoragePort(
                new StoredWorkflowYaml(
                    "hello-chat",
                    "hello-chat",
                    "name: hello-chat\ndescription: restored from storage\nsteps: []\n",
                    new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero))));

        var workflow = await service.GetAsync("scope-1", "hello-chat");

        workflow.Should().NotBeNull();
        workflow!.WorkflowId.Should().Be("hello-chat");
        workflow.Name.Should().Be("hello-chat");
        workflow.Yaml.Should().Contain("restored from storage");
        workflow.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WhenRuntimeWorkflowExistsButBindingYamlIsEmpty_ShouldFallbackToStoredWorkflowYaml()
    {
        var workflow = new ScopeWorkflowSummary(
            ScopeId: "scope-1",
            WorkflowId: "test03",
            DisplayName: "test03",
            ServiceKey: "scope-1:default:default:test03",
            WorkflowName: "test03",
            ActorId: "actor-1",
            ActiveRevisionId: "rev-1",
            DeploymentId: "deploy-1",
            DeploymentStatus: "active",
            UpdatedAt: DateTimeOffset.UtcNow);

        var service = new AppScopedWorkflowService(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP backend should not be called.")))
            {
                BaseAddress = new Uri("https://backend.example"),
            }),
            new StubWorkflowYamlDocumentService(),
            workflowQueryPort: new StubScopeWorkflowQueryPort(workflow),
            workflowActorBindingReader: new StubWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    "actor-1",
                    "actor-1",
                    string.Empty,
                    "test03",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal))),
            workflowStoragePort: new StubWorkflowStoragePort(
                new StoredWorkflowYaml(
                    "test03",
                    "test03",
                    "name: test03\ndescription: restored from storage\nsteps:\n  - id: llm_call\n",
                    new DateTimeOffset(2026, 4, 16, 10, 53, 48, TimeSpan.Zero))));

        var result = await service.GetAsync("scope-1", "test03");

        result.Should().NotBeNull();
        result!.Yaml.Should().Contain("llm_call");
        result.Document.Should().NotBeNull();
        result.Document!.Steps.Should().ContainSingle();
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WhenStoredDraftExists_ShouldPreferStoredWorkflowYamlOverRuntimeBindingYaml()
    {
        var workflow = new ScopeWorkflowSummary(
            ScopeId: "scope-1",
            WorkflowId: "test03",
            DisplayName: "test03",
            ServiceKey: "scope-1:default:default:test03",
            WorkflowName: "test03",
            ActorId: "actor-1",
            ActiveRevisionId: "rev-1",
            DeploymentId: "deploy-1",
            DeploymentStatus: "active",
            UpdatedAt: DateTimeOffset.UtcNow);

        var service = new AppScopedWorkflowService(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP backend should not be called.")))
            {
                BaseAddress = new Uri("https://backend.example"),
            }),
            new StubWorkflowYamlDocumentService(),
            workflowQueryPort: new StubScopeWorkflowQueryPort(workflow),
            workflowActorBindingReader: new StubWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    "actor-1",
                    "actor-1",
                    string.Empty,
                    "test03",
                    "name: runtime-version\nsteps: []\n",
                    new Dictionary<string, string>(StringComparer.Ordinal))),
            workflowStoragePort: new StubWorkflowStoragePort(
                new StoredWorkflowYaml(
                    "test03",
                    "test03",
                    "name: draft-version\ndescription: prefer stored draft\nsteps:\n  - id: llm_call\n",
                    new DateTimeOffset(2026, 4, 16, 10, 53, 48, TimeSpan.Zero))));

        var result = await service.GetAsync("scope-1", "test03");

        result.Should().NotBeNull();
        result!.Name.Should().Be("draft-version");
        result.Yaml.Should().Contain("draft-version");
        result.Yaml.Should().NotContain("runtime-version");
        result.Document.Should().NotBeNull();
        result.Document!.Steps.Should().ContainSingle();
        result.Findings.Should().BeEmpty();
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
        private static readonly Regex NameRegex = new(@"(?m)^name:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex DescriptionRegex = new(@"(?m)^description:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex StepsBlockRegex = new(@"(?ms)^steps:\s*\n(?<items>(?:\s*-\s.*\n?)*)", RegexOptions.Compiled);
        private static readonly Regex StepItemRegex = new(@"(?m)^\s*-\s+", RegexOptions.Compiled);

        public WorkflowParseResult Parse(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml))
                return new(null, []);

            var input = yaml ?? string.Empty;
            var nameMatch = NameRegex.Match(input);
            var descriptionMatch = DescriptionRegex.Match(input);
            var steps = new List<StepModel>();
            var stepsMatch = StepsBlockRegex.Match(input);
            if (stepsMatch.Success)
            {
                var stepItems = StepItemRegex.Matches(stepsMatch.Groups["items"].Value).Count;
                for (var index = 0; index < stepItems; index++)
                {
                    steps.Add(new StepModel());
                }
            }

            return new(new WorkflowDocument
            {
                Name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : string.Empty,
                Description = descriptionMatch.Success ? descriptionMatch.Groups[1].Value.Trim() : string.Empty,
                Steps = steps,
            }, []);
        }

        public string Serialize(WorkflowDocument document) => $"name: {document.Name}\nsteps: []\n";
    }

    private sealed class StubScopeWorkflowQueryPort : IScopeWorkflowQueryPort
    {
        private readonly ScopeWorkflowSummary? _workflow;

        public StubScopeWorkflowQueryPort()
        {
        }

        public StubScopeWorkflowQueryPort(ScopeWorkflowSummary workflow)
        {
            _workflow = workflow;
        }

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>(_workflow == null ? [] : [_workflow]);

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(
                _workflow != null && string.Equals(workflowId, _workflow.WorkflowId, StringComparison.Ordinal)
                    ? _workflow
                    : null);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(string scopeId, string actorId, CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(
                _workflow != null && string.Equals(actorId, _workflow.ActorId, StringComparison.Ordinal)
                    ? _workflow
                    : null);
    }

    private sealed class StubWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly WorkflowActorBinding? _binding;

        public StubWorkflowActorBindingReader(WorkflowActorBinding? binding)
        {
            _binding = binding;
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorBinding?>(
                _binding != null && string.Equals(actorId, _binding.ActorId, StringComparison.Ordinal)
                    ? _binding
                    : null);
    }

    private sealed class StubArtifactStore : IServiceRevisionArtifactStore
    {
        public Task SaveAsync(string serviceKey, string revisionId, PreparedServiceRevisionArtifact artifact, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<PreparedServiceRevisionArtifact?> GetAsync(string serviceKey, string revisionId, CancellationToken ct = default) =>
            Task.FromResult<PreparedServiceRevisionArtifact?>(null);
    }

    private sealed class StubWorkflowStoragePort : IWorkflowStoragePort
    {
        private readonly Dictionary<string, StoredWorkflowYaml> _storedWorkflows;
        public UploadedWorkflowYaml? LastUpload { get; private set; }

        public StubWorkflowStoragePort(params StoredWorkflowYaml[] storedWorkflows)
        {
            _storedWorkflows = storedWorkflows.ToDictionary(item => item.WorkflowId, StringComparer.Ordinal);
        }

        public Task UploadWorkflowYamlAsync(string workflowId, string workflowName, string yaml, CancellationToken ct)
        {
            var uploadedAt = DateTimeOffset.UtcNow;
            LastUpload = new UploadedWorkflowYaml(workflowId, workflowName, yaml, uploadedAt);
            _storedWorkflows[workflowId] = new StoredWorkflowYaml(workflowId, workflowName, yaml, uploadedAt);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredWorkflowYaml>> ListWorkflowYamlsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowYaml>>(_storedWorkflows.Values.ToList());

        public Task<StoredWorkflowYaml?> GetWorkflowYamlAsync(string workflowId, CancellationToken ct) =>
            Task.FromResult<StoredWorkflowYaml?>(
                _storedWorkflows.TryGetValue(workflowId, out var storedWorkflow)
                    ? storedWorkflow
                    : null);

        public Task DeleteWorkflowYamlAsync(string workflowId, CancellationToken ct)
        {
            _storedWorkflows.Remove(workflowId);
            return Task.CompletedTask;
        }
    }

    private sealed record UploadedWorkflowYaml(
        string WorkflowId,
        string WorkflowName,
        string Yaml,
        DateTimeOffset UploadedAtUtc);
}
