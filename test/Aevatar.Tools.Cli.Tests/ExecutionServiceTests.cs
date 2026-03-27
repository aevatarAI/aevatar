using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ExecutionServiceTests
{
    [Fact]
    public async Task StartAsync_WhenPublishedWorkflowTargetProvided_ShouldCallScopeServiceStreamEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(CreateSseResponse("""
                data: {"custom":{"name":"aevatar.run.context","payload":{"actorId":"run-actor-1","workflowName":"approval"}}}

                data: {"runFinished":{"threadId":"run-1"}}

                data: [DONE]

                """)));
        var (service, store) = CreateService(handler);

        var detail = await service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            ScopeId: "scope-a",
            WorkflowId: "workflow-1"));
        var completed = await store.WaitForExecutionAsync(detail.ExecutionId, record => record.Status == "completed");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().Should().Be("https://runtime.example/api/scopes/scope-a/services/workflow-1/invoke/chat:stream");
        handler.LastBody.Should().NotBeNull();
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("prompt").GetString().Should().Be("hello");
        completed.ActorId.Should().Be("run-actor-1");
        completed.Status.Should().Be("completed");
    }

    [Fact]
    public async Task StartAsync_WhenRegisteredWorkflowTargetMissing_ShouldFailFast()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}"));
        var (service, _) = CreateService(handler);

        var act = () => service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*scopeId and workflowId are required*");
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_WhenRuntimeReturnsStructuredError_ShouldPersistSyntheticRunErrorFrame()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """
                    {"code":"AUTH_REQUIRED","message":"Sign in to use the service APIs.","loginUrl":"/auth/login?returnUrl=%2F"}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));
        var (service, store) = CreateService(handler);

        var detail = await service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            ScopeId: "scope-a",
            WorkflowId: "workflow-1"));
        var failed = await store.WaitForExecutionAsync(detail.ExecutionId, record => record.Status == "failed");

        failed.Error.Should().Be("Sign in to use the service APIs.");
        failed.Frames.Should().HaveCount(1);
        using var payload = JsonDocument.Parse(failed.Frames[0].Payload);
        payload.RootElement.GetProperty("runError").GetProperty("code").GetString().Should().Be("AUTH_REQUIRED");
    }

    [Fact]
    public async Task StartAsync_WhenStreamEndsWithoutTerminalEvent_ShouldPersistFailureAndKeepObservedFrames()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(CreateSseResponse("""
                data: {"custom":{"name":"aevatar.run.context","payload":{"actorId":"run-actor-eof","workflowName":"approval"}}}

                data: {"runStarted":{"threadId":"run-actor-eof","runId":"run-eof-1"}}

                data: [DONE]

                """)));
        var (service, store) = CreateService(handler);

        var detail = await service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            ScopeId: "scope-a",
            WorkflowId: "workflow-1"));
        var failed = await store.WaitForExecutionAsync(detail.ExecutionId, record => record.Status == "failed");

        failed.ActorId.Should().Be("run-actor-eof");
        failed.Error.Should().Be("Execution stream ended before a terminal event was observed.");
        failed.Frames.Should().HaveCount(3);
        using var payload = JsonDocument.Parse(failed.Frames.Last().Payload);
        payload.RootElement.GetProperty("runError").GetProperty("code").GetString().Should().Be("EXECUTION_STREAM_TERMINATED");
    }

    [Fact]
    public async Task StartAsync_WhenAuthSnapshotCaptured_ShouldReplayHeadersInBackgroundRequest()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(CreateSseResponse("""
                data: {"runFinished":{"threadId":"run-3"}}

                data: [DONE]

                """)));
        var snapshotProvider = new StubAuthSnapshotProvider(new StudioBackendRequestAuthSnapshot(
            LocalOrigin: "https://runtime.example",
            BearerToken: "token-123",
            InternalAuthHeaderName: "X-Aevatar-Internal-Auth",
            InternalAuthToken: "internal-456"));
        var (service, store) = CreateService(handler, snapshotProvider);

        var detail = await service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            ScopeId: "scope-a",
            WorkflowId: "workflow-1"));
        await store.WaitForExecutionAsync(detail.ExecutionId, record => record.Status == "completed");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("token-123");
        handler.LastRequest.Headers.GetValues("X-Aevatar-Internal-Auth").Should().ContainSingle("internal-456");
    }

    [Fact]
    public async Task StopAsync_ShouldCallScopeServiceStopEndpoint_AndAppendLocalStopFrame()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            }));
        var (service, store) = CreateService(handler);
        var seed = new StoredExecutionRecord(
            ExecutionId: "exec-stop-1",
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            Status: "waiting",
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: null,
            ActorId: "run-actor-stop-1",
            Error: null,
            Frames:
            [
                new StoredExecutionFrame(
                    DateTimeOffset.UtcNow,
                    """{"runStarted":{"threadId":"run-actor-stop-1","runId":"run-stop-1"}}"""),
                new StoredExecutionFrame(
                    DateTimeOffset.UtcNow,
                    """{"custom":{"name":"aevatar.human_input.request","payload":{"stepId":"approval-step-1"}}}""")
            ],
            ScopeId: "scope-a",
            WorkflowId: "workflow-1");
        await store.SaveExecutionAsync(seed);

        var detail = await service.StopAsync("exec-stop-1", new StopExecutionRequest("manual"), CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://runtime.example/api/scopes/scope-a/services/workflow-1/runs/run-stop-1:stop");
        handler.LastBody.Should().NotBeNull();
        using (var body = JsonDocument.Parse(handler.LastBody!))
        {
            body.RootElement.GetProperty("actorId").GetString().Should().Be("run-actor-stop-1");
            body.RootElement.GetProperty("reason").GetString().Should().Be("manual");
        }

        detail.Should().NotBeNull();
        detail!.Frames.Should().HaveCount(3);
    }

    [Fact]
    public async Task ResumeAsync_ShouldCallScopeServiceResumeEndpoint_AndPersistResumeFrame()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/api/scopes/scope-a/services/workflow-1/runs/run-resume-1:resume" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"accepted":true,"actorId":"run-actor-resume-1","runId":"run-resume-1","commandId":"cmd-resume-1"}""",
                        Encoding.UTF8,
                        "application/json"),
                },
                _ => throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}"),
            }));
        var (service, store) = CreateService(handler);
        var observedAtUtc = DateTimeOffset.UtcNow;
        var seed = new StoredExecutionRecord(
            ExecutionId: "exec-resume-1",
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            Status: "waiting",
            StartedAtUtc: observedAtUtc.AddMinutes(-1),
            CompletedAtUtc: null,
            ActorId: "run-actor-resume-1",
            Error: null,
            Frames:
            [
                new StoredExecutionFrame(
                    observedAtUtc.AddSeconds(-2),
                    """{"runStarted":{"threadId":"run-actor-resume-1","runId":"run-resume-1"}}"""),
                new StoredExecutionFrame(
                    observedAtUtc,
                    """{"custom":{"name":"aevatar.human_input.request","payload":{"stepId":"approval-step-1"}}}""")
            ],
            ScopeId: "scope-a",
            WorkflowId: "workflow-1");
        await store.SaveExecutionAsync(seed);

        var resumed = await service.ResumeAsync(
            "exec-resume-1",
            new ResumeExecutionRequest(
                RunId: "run-resume-1",
                StepId: "approval-step-1",
                Approved: true,
                UserInput: "approved"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://runtime.example/api/scopes/scope-a/services/workflow-1/runs/run-resume-1:resume");
        resumed.Should().NotBeNull();
        resumed!.Status.Should().Be("running");
        resumed.Frames.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetAsync_WhenObservationSessionWasLost_ShouldMarkExecutionFailed()
    {
        var store = new InMemoryStudioWorkspaceStore();
        var handler = new RecordingHttpMessageHandler((request, _) =>
            throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}"));
        var (service, _) = CreateService(handler, store: store);
        var observedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5);
        var seed = new StoredExecutionRecord(
            ExecutionId: "exec-orphan-1",
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            Status: "waiting",
            StartedAtUtc: observedAtUtc.AddMinutes(-1),
            CompletedAtUtc: null,
            ActorId: "WorkflowRun:exec-orphan-1",
            Error: null,
            Frames:
            [
                new StoredExecutionFrame(
                    observedAtUtc,
                    """{"custom":{"name":"aevatar.human_input.request","payload":{"stepId":"approval-step-1"}}}""")
            ],
            ObservationSessionId: "stale-session",
            ObservationActive: true,
            LastObservedAtUtc: observedAtUtc,
            ScopeId: "scope-a",
            WorkflowId: "workflow-1");
        await store.SaveExecutionAsync(seed);

        var detail = await service.GetAsync("exec-orphan-1", CancellationToken.None);
        var failed = await store.GetExecutionAsync("exec-orphan-1", CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.Status.Should().Be("failed");
        detail.Error.Should().Be("Studio execution observer was lost before a terminal event was observed.");
        failed.Should().NotBeNull();
        failed!.ObservationActive.Should().BeFalse();
    }

    private static (ExecutionService Service, InMemoryStudioWorkspaceStore Store) CreateService(
        RecordingHttpMessageHandler handler,
        IStudioBackendRequestAuthSnapshotProvider? authSnapshotProvider = null,
        InMemoryStudioWorkspaceStore? store = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://backend.example"),
        };
        store ??= new InMemoryStudioWorkspaceStore();

        return (
            new ExecutionService(
                store,
                new StubHttpClientFactory(httpClient),
                authSnapshotProvider),
            store);
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

    private sealed class StubAuthSnapshotProvider : IStudioBackendRequestAuthSnapshotProvider
    {
        private readonly StudioBackendRequestAuthSnapshot? _snapshot;

        public StubAuthSnapshotProvider(StudioBackendRequestAuthSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<StudioBackendRequestAuthSnapshot?> CaptureAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);
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
        private readonly object _sync = new();
        private StudioWorkspaceSettings _settings = new(
            RuntimeBaseUrl: "http://127.0.0.1:5100",
            Directories: [],
            AppearanceTheme: "blue",
            ColorMode: "light");

        private readonly Dictionary<string, StoredExecutionRecord> _executions = new(StringComparer.Ordinal);
        private readonly List<ExecutionWaiter> _waiters = [];

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

        public Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<StoredExecutionRecord>>(_executions.Values.ToList());
            }
        }

        public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult(_executions.TryGetValue(executionId, out var record) ? record : null);
            }
        }

        public Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default)
        {
            List<ExecutionWaiter> completedWaiters = [];
            lock (_sync)
            {
                _executions[execution.ExecutionId] = execution;
                for (var index = _waiters.Count - 1; index >= 0; index -= 1)
                {
                    var waiter = _waiters[index];
                    if (!string.Equals(waiter.ExecutionId, execution.ExecutionId, StringComparison.Ordinal) ||
                        !waiter.Predicate(execution))
                    {
                        continue;
                    }

                    completedWaiters.Add(waiter);
                    _waiters.RemoveAt(index);
                }
            }

            foreach (var waiter in completedWaiters)
            {
                waiter.Source.TrySetResult(execution);
            }

            return Task.FromResult(execution);
        }

        public Task<StoredExecutionRecord> WaitForExecutionAsync(
            string executionId,
            Func<StoredExecutionRecord, bool> predicate)
        {
            lock (_sync)
            {
                if (_executions.TryGetValue(executionId, out var execution) && predicate(execution))
                {
                    return Task.FromResult(execution);
                }

                var waiter = new ExecutionWaiter(
                    executionId,
                    predicate,
                    new TaskCompletionSource<StoredExecutionRecord>(TaskCreationOptions.RunContinuationsAsynchronously));
                _waiters.Add(waiter);
                return waiter.Source.Task;
            }
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

        private sealed record ExecutionWaiter(
            string ExecutionId,
            Func<StoredExecutionRecord, bool> Predicate,
            TaskCompletionSource<StoredExecutionRecord> Source);
    }
}
