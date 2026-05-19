using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ExecutionServiceTests
{
    [Fact]
    public async Task StartAsync_WhenPublishedWorkflowTargetProvided_ShouldInvokeServicePort()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var service = CreateService(invocationPort: invocationPort);

        var detail = await service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            ScopeId: "scope-a",
            WorkflowId: "workflow-1"));

        invocationPort.LastRequest.Should().NotBeNull();
        invocationPort.LastRequest!.Identity.TenantId.Should().Be("scope-a");
        invocationPort.LastRequest.Identity.ServiceId.Should().Be("workflow-1");
        invocationPort.LastRequest.EndpointId.Should().Be("chat");
        detail.Status.Should().Be("accepted");
        detail.ActorId.Should().Be("run-actor-1");
    }

    [Fact]
    public async Task StartAsync_WhenRegisteredWorkflowTargetMissing_ShouldFailFast()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var service = CreateService(invocationPort: invocationPort);

        var act = () => service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*scopeId and workflowId are required*");
        invocationPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_WhenScopeResolverReturnsScope_ShouldOnlyQueryThatScope()
    {
        var runQueryPort = new RecordingServiceRunQueryPort();
        runQueryPort.Runs.Add(CreateRun("scope-a", "workflow-1", "run-a", "cmd-a"));
        var service = CreateService(
            runQueryPort: runQueryPort,
            scopeResolver: new StubAppScopeResolver("scope-a"));

        var summaries = await service.ListAsync();

        runQueryPort.LastQuery.Should().NotBeNull();
        runQueryPort.LastQuery!.ScopeId.Should().Be("scope-a");
        summaries.Should().ContainSingle(summary => summary.ExecutionId == "cmd-a");
    }

    [Fact]
    public async Task GetAsync_WhenCommandIdMatches_ShouldReturnDetail()
    {
        var run = CreateRun("scope-a", "workflow-1", "run-a", "cmd-a", ServiceRunStatus.Completed);
        var runQueryPort = new RecordingServiceRunQueryPort();
        runQueryPort.Runs.Add(run);
        var service = CreateService(
            runQueryPort: runQueryPort,
            scopeResolver: new StubAppScopeResolver("scope-a"));

        var detail = await service.GetAsync("cmd-a");

        detail.Should().NotBeNull();
        detail!.ExecutionId.Should().Be("cmd-a");
        detail.Status.Should().Be("completed");
        detail.CompletedAtUtc.Should().Be(run.UpdatedAt);
    }

    [Fact]
    public async Task ResumeAsync_ShouldDispatchWorkflowResumeCommand()
    {
        var runQueryPort = new RecordingServiceRunQueryPort();
        runQueryPort.Runs.Add(CreateRun("scope-a", "workflow-1", "run-a", "cmd-a"));
        var resumeDispatch = new RecordingResumeDispatchService();
        var service = CreateService(
            runQueryPort: runQueryPort,
            resumeDispatch: resumeDispatch,
            scopeResolver: new StubAppScopeResolver("scope-a"));

        var detail = await service.ResumeAsync(
            "cmd-a",
            new ResumeExecutionRequest("run-a", "step-1", Approved: true, UserInput: "approved"));

        detail.Should().NotBeNull();
        detail!.Status.Should().Be("running");
        resumeDispatch.LastCommand.Should().NotBeNull();
        resumeDispatch.LastCommand!.ActorId.Should().Be("run-actor-1");
        resumeDispatch.LastCommand.RunId.Should().Be("run-a");
        resumeDispatch.LastCommand.StepId.Should().Be("step-1");
    }

    [Fact]
    public async Task StopAsync_ShouldDispatchWorkflowStopCommand()
    {
        var runQueryPort = new RecordingServiceRunQueryPort();
        runQueryPort.Runs.Add(CreateRun("scope-a", "workflow-1", "run-a", "cmd-a"));
        var stopDispatch = new RecordingStopDispatchService();
        var service = CreateService(
            runQueryPort: runQueryPort,
            stopDispatch: stopDispatch,
            scopeResolver: new StubAppScopeResolver("scope-a"));

        var detail = await service.StopAsync("cmd-a", new StopExecutionRequest("manual"));

        detail.Should().NotBeNull();
        detail!.Status.Should().Be("stopped");
        stopDispatch.LastCommand.Should().NotBeNull();
        stopDispatch.LastCommand!.ActorId.Should().Be("run-actor-1");
        stopDispatch.LastCommand.Reason.Should().Be("manual");
    }

    [Fact]
    public async Task StartAsync_WhenRequestedScopeDoesNotMatchCaller_ShouldThrow()
    {
        var service = CreateService(scopeResolver: new StubAppScopeResolver("scope-a"));

        var act = () => service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            ScopeId: "scope-b",
            WorkflowId: "workflow-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Requested scope does not match the authenticated Studio scope*");
    }

    [Fact]
    public async Task ListAsync_WhenAuthenticatedCallerHasNoScope_ShouldFailClosed()
    {
        var runQueryPort = new RecordingServiceRunQueryPort();
        runQueryPort.Runs.Add(CreateRun("scope-a", "workflow-1", "run-a", "cmd-a"));
        var service = CreateService(
            runQueryPort: runQueryPort,
            scopeResolver: new StubAppScopeResolver(scopeId: null, authenticatedWithoutScope: true));

        var summaries = await service.ListAsync();

        summaries.Should().BeEmpty();
        runQueryPort.LastQuery.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenAuthenticatedCallerHasNoScope_ShouldReturnNull()
    {
        var service = CreateService(scopeResolver: new StubAppScopeResolver(scopeId: null, authenticatedWithoutScope: true));

        var detail = await service.GetAsync("cmd-a");

        detail.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_WhenAuthenticatedCallerHasNoScope_ShouldThrow()
    {
        var service = CreateService(scopeResolver: new StubAppScopeResolver(scopeId: null, authenticatedWithoutScope: true));

        var act = () => service.StartAsync(new StartExecutionRequest(
            WorkflowName: "approval",
            Prompt: "hello",
            RuntimeBaseUrl: "https://runtime.example",
            ScopeId: "scope-a",
            WorkflowId: "workflow-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Authenticated caller has no resolvable scope*");
    }

    private static ExecutionService CreateService(
        IServiceInvocationPort? invocationPort = null,
        RecordingServiceRunQueryPort? runQueryPort = null,
        ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>? resumeDispatch = null,
        ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>? stopDispatch = null,
        IAppScopeResolver? scopeResolver = null)
    {
        return new ExecutionService(
            invocationPort ?? new RecordingServiceInvocationPort(),
            runQueryPort ?? new RecordingServiceRunQueryPort(),
            resumeDispatch ?? new RecordingResumeDispatchService(),
            stopDispatch ?? new RecordingStopDispatchService(),
            scopeResolver: scopeResolver);
    }

    private static ServiceRunSnapshot CreateRun(
        string scopeId,
        string serviceId,
        string runId,
        string commandId,
        ServiceRunStatus status = ServiceRunStatus.Accepted)
    {
        var now = DateTimeOffset.UtcNow;
        return new ServiceRunSnapshot(
            ScopeId: scopeId,
            ServiceId: serviceId,
            ServiceKey: serviceId,
            RunId: runId,
            CommandId: commandId,
            CorrelationId: commandId,
            EndpointId: "chat",
            ImplementationKind: ServiceImplementationKind.Workflow,
            TargetActorId: "run-actor-1",
            RevisionId: "revision-1",
            DeploymentId: "deployment-1",
            Status: status,
            ActorId: $"service-run-{runId}",
            TenantId: scopeId,
            AppId: string.Empty,
            Namespace: string.Empty,
            StateVersion: 1,
            LastEventId: "event-1",
            CreatedAt: now.AddMinutes(-1),
            UpdatedAt: now);
    }

    private sealed class RecordingServiceInvocationPort : IServiceInvocationPort
    {
        public ServiceInvocationRequest? LastRequest { get; private set; }

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                RequestId = request.CommandId,
                ServiceKey = request.Identity.ServiceId,
                DeploymentId = "deployment-1",
                TargetActorId = "run-actor-1",
                EndpointId = request.EndpointId,
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
            });
        }
    }

    private sealed class RecordingServiceRunQueryPort : IServiceRunQueryPort
    {
        public List<ServiceRunSnapshot> Runs { get; } = [];

        public ServiceRunQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<ServiceRunSnapshot>>(Runs
                .Where(run => string.Equals(run.ScopeId, query.ScopeId, StringComparison.Ordinal))
                .ToList());
        }

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default)
        {
            return Task.FromResult(Runs.FirstOrDefault(run =>
                string.Equals(run.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(run.RunId, runId, StringComparison.Ordinal)));
        }

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default)
        {
            return Task.FromResult(Runs.FirstOrDefault(run =>
                string.Equals(run.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(run.CommandId, commandId, StringComparison.Ordinal)));
        }
    }

    private sealed class RecordingResumeDispatchService
        : ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowResumeCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowResumeCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, command.CommandId!, command.CorrelationId!)));
        }
    }

    private sealed class RecordingStopDispatchService
        : ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowStopCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowStopCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, command.CommandId!, command.CorrelationId!)));
        }
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
}
