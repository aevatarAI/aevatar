using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Application.Contracts;
using Aevatar.Tools.Cli.Studio.Application.Services;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ExecutionServiceTests
{
    [Fact]
    public async Task StartAsync_WhenPublishedWorkflowTargetProvided_ShouldCallScopeRunStreamEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(CreateSseResponse("""
                data: {"custom":{"name":"aevatar.run.context","payload":{"actorId":"run-actor-1","workflowName":"approval"}}}

                data: {"runFinished":{"threadId":"run-1"}}

                data: [DONE]

                """)));
        var service = CreateService(handler);

        var detail = await service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            WorkflowYamls: ["name: approval"],
            RuntimeBaseUrl: "https://runtime.example",
            ScopeId: "scope-a",
            WorkflowId: "workflow-1"));

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().Should().Be("https://runtime.example/api/scopes/scope-a/workflows/workflow-1/runs:stream");
        handler.LastBody.Should().NotBeNull();
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("prompt").GetString().Should().Be("hello");
        body.RootElement.GetProperty("eventFormat").GetString().Should().Be("workflow");
        body.RootElement.TryGetProperty("workflowYamls", out _).Should().BeFalse();
        detail.ActorId.Should().Be("run-actor-1");
        detail.Status.Should().Be("completed");
    }

    [Fact]
    public async Task StartAsync_WhenPublishedWorkflowTargetMissing_ShouldCallChatEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(CreateSseResponse("""
                data: {"custom":{"name":"aevatar.run.context","payload":{"actorId":"run-actor-2","workflowName":"draft"}}}

                data: {"runFinished":{"threadId":"run-2"}}

                data: [DONE]

                """)));
        var service = CreateService(handler);

        var detail = await service.StartAsync(new StartExecutionRequest(
            WorkflowName: "draft",
            Prompt: "hello",
            WorkflowYamls: ["name: draft"],
            RuntimeBaseUrl: "https://runtime.example"));

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().Should().Be("https://runtime.example/api/chat");
        handler.LastBody.Should().NotBeNull();
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("prompt").GetString().Should().Be("hello");
        body.RootElement.GetProperty("workflow").GetString().Should().Be("draft");
        body.RootElement.GetProperty("workflowYamls")[0].GetString().Should().Be("name: draft");
        detail.ActorId.Should().Be("run-actor-2");
        detail.Status.Should().Be("completed");
    }

    private static ExecutionService CreateService(RecordingHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backend.example"),
        };

        return new ExecutionService(
            new InMemoryStudioWorkspaceStore(),
            new StubHttpClientFactory(httpClient));
    }

    private static HttpResponseMessage CreateSseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream"),
        };

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(string name) => _httpClient;
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        public RecordingHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = await _responseFactory(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class InMemoryStudioWorkspaceStore : IStudioWorkspaceStore
    {
        private StudioWorkspaceSettings _settings = new(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [],
            AppearanceTheme: "blue",
            ColorMode: "light");

        private readonly Dictionary<string, StoredExecutionRecord> _executions = new(StringComparer.Ordinal);

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowFile>>([]);

        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredWorkflowFile?>(null);

        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredExecutionRecord>>(_executions.Values.ToList());

        public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_executions.TryGetValue(executionId, out var record) ? record : null);

        public Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default)
        {
            _executions[execution.ExecutionId] = execution;
            return Task.FromResult(execution);
        }

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
