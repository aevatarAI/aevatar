using System.Collections.Concurrent;
using System.Reflection;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public sealed class WorkflowTriggerServiceTests
{
    [Fact]
    public async Task TriggerAsync_WhenProjectionCompleted_ShouldMarkSessionCompleted()
    {
        var lifecycle = CreateLifecycle();
        var session = SeedSession(lifecycle, maxRounds: 9);
        lifecycle.TryStartSession(session.Id).Should().BeTrue();

        var runService = new FakeRunService(_ => Task.FromResult(new WorkflowChatRunExecutionResult(
            WorkflowChatRunStartError.None,
            new WorkflowChatRunStarted("actor-1", "sisyphus_research", "cmd-1"),
            new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true))));

        var trigger = CreateTrigger(runService, lifecycle);
        await trigger.TriggerAsync(session.Id, ct: CancellationToken.None);

        var updated = lifecycle.GetSession(session.Id)!;
        updated.Status.Should().Be(SessionStatus.Completed);
        updated.FailureReason.Should().BeNull();
        updated.CompletedAt.Should().NotBeNull();
        updated.ActorId.Should().Be("actor-1");
        updated.CommandId.Should().Be("cmd-1");
        runService.LastRequest!.WorkflowYaml.Should().Contain("max_iterations: \"9\"");
    }

    [Fact]
    public async Task TriggerAsync_WhenFinalizeStatusIsFailed_ShouldMarkSessionFailed()
    {
        var lifecycle = CreateLifecycle();
        var session = SeedSession(lifecycle, maxRounds: 5);
        lifecycle.TryStartSession(session.Id).Should().BeTrue();

        var runService = new FakeRunService(_ => Task.FromResult(new WorkflowChatRunExecutionResult(
            WorkflowChatRunStartError.None,
            new WorkflowChatRunStarted("actor-2", "sisyphus_research", "cmd-2"),
            new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Failed, true))));

        var trigger = CreateTrigger(runService, lifecycle);
        await trigger.TriggerAsync(session.Id, ct: CancellationToken.None);

        var updated = lifecycle.GetSession(session.Id)!;
        updated.Status.Should().Be(SessionStatus.Failed);
        updated.FailureReason.Should().Be("FINALIZE_STATUS:Failed");
        updated.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TriggerAsync_WhenStartError_ShouldMarkSessionFailed()
    {
        var lifecycle = CreateLifecycle();
        var session = SeedSession(lifecycle, maxRounds: 20);
        lifecycle.TryStartSession(session.Id).Should().BeTrue();

        var runService = new FakeRunService(_ => Task.FromResult(new WorkflowChatRunExecutionResult(
            WorkflowChatRunStartError.WorkflowNotFound,
            null,
            null)));

        var trigger = CreateTrigger(runService, lifecycle);
        await trigger.TriggerAsync(session.Id, ct: CancellationToken.None);

        var updated = lifecycle.GetSession(session.Id)!;
        updated.Status.Should().Be(SessionStatus.Failed);
        updated.FailureReason.Should().Be("START_ERROR:WorkflowNotFound");
    }

    [Fact]
    public async Task TriggerAsync_WhenRunThrows_ShouldMarkSessionFailedAndRethrow()
    {
        var lifecycle = CreateLifecycle();
        var session = SeedSession(lifecycle, maxRounds: 7);
        lifecycle.TryStartSession(session.Id).Should().BeTrue();

        var runService = new FakeRunService(_ => throw new InvalidOperationException("boom"));
        var trigger = CreateTrigger(runService, lifecycle);

        var act = () => trigger.TriggerAsync(session.Id, ct: CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        var updated = lifecycle.GetSession(session.Id)!;
        updated.Status.Should().Be(SessionStatus.Failed);
        updated.FailureReason.Should().Be("RUN_EXCEPTION:InvalidOperationException");
        updated.CompletedAt.Should().NotBeNull();
    }

    private static WorkflowTriggerService CreateTrigger(
        FakeRunService runService,
        SessionLifecycleService lifecycle)
    {
        var registry = new InMemoryRegistry();
        registry.Register("sisyphus_research", SampleWorkflowYaml);

        return new WorkflowTriggerService(
            runService,
            registry,
            lifecycle,
            NullLogger<WorkflowTriggerService>.Instance);
    }

    private static SessionLifecycleService CreateLifecycle()
    {
        var options = Options.Create(new NyxIdOptions
        {
            BaseUrl = "http://localhost",
            TokenUrl = "http://localhost/token",
            ClientId = "id",
            ClientSecret = "secret",
            ChronoGraphServiceId = "chrono-service",
        });

        var handler = new StubHttpMessageHandler();
        var tokenService = new NyxIdTokenService(new HttpClient(handler), options);
        var chronoGraph = new ChronoGraphClient(new HttpClient(handler), tokenService, options);
        return new SessionLifecycleService(chronoGraph);
    }

    private static ResearchSession SeedSession(SessionLifecycleService lifecycle, int maxRounds)
    {
        var session = new ResearchSession
        {
            Topic = "topic",
            GraphId = "graph-1",
            MaxRounds = maxRounds,
        };

        var field = typeof(SessionLifecycleService).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sessions = (ConcurrentDictionary<Guid, ResearchSession>)field.GetValue(lifecycle)!;
        sessions[session.Id] = session;
        return session;
    }

    private const string SampleWorkflowYaml = """
name: sisyphus_research
steps:
  - id: research_loop
    type: while
    role: researcher
    parameters:
      max_iterations: "20"
      step: llm_call
""";

    private sealed class FakeRunService(
        Func<WorkflowChatRunRequest, Task<WorkflowChatRunExecutionResult>> onExecute)
        : IWorkflowRunCommandService
    {
        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public async Task<WorkflowChatRunExecutionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            _ = emitAsync;
            var result = await onExecute(request);
            if (result.Started != null && onStartedAsync != null)
                await onStartedAsync(result.Started, ct);
            return result;
        }
    }

    private sealed class InMemoryRegistry : IWorkflowDefinitionRegistry
    {
        private readonly Dictionary<string, string> _yamlByName = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string name, string yaml) => _yamlByName[name] = yaml;

        public string? GetYaml(string name) => _yamlByName.TryGetValue(name, out var yaml) ? yaml : null;

        public IReadOnlyList<string> GetNames() => _yamlByName.Keys.ToList();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
