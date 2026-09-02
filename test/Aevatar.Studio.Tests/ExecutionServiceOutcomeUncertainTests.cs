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

namespace Aevatar.Studio.Tests;

public sealed class ExecutionServiceOutcomeUncertainTests
{
    [Fact]
    public async Task ResumeAsync_ShouldRejectOutcomeUncertainExecution_AsTerminal()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-02T00:00:00+00:00");
        var run = new ServiceRunSnapshot(
            ScopeId: "scope-1",
            ServiceId: "service-1",
            ServiceKey: "scope-1/service-1",
            RunId: "run-1",
            CommandId: "command-1",
            CorrelationId: "correlation-1",
            EndpointId: "chat",
            ScheduleId: string.Empty,
            ImplementationKind: ServiceImplementationKind.Static,
            TargetActorId: "actor-1",
            RevisionId: "revision-1",
            DeploymentId: "deployment-1",
            Status: ServiceRunStatus.OutcomeUncertain,
            ActorId: "service-run-actor-1",
            TenantId: "scope-1",
            AppId: string.Empty,
            Namespace: string.Empty,
            StateVersion: 4,
            LastEventId: "evt-uncertain",
            CreatedAt: observedAt.AddMinutes(-1),
            UpdatedAt: observedAt,
            LastOutput: string.Empty,
            LastError: "The interrupted session may have produced side effects.");
        var resumeDispatch = new RecordingWorkflowControlDispatchService<WorkflowResumeCommand>();
        var service = new ExecutionService(
            new UnexpectedServiceInvocationPort(),
            new FixedServiceRunQueryPort(run),
            resumeDispatch,
            new RecordingWorkflowControlDispatchService<WorkflowStopCommand>(),
            scopeResolver: new FixedAppScopeResolver("scope-1"));

        var detail = await service.GetAsync("command-1");
        var act = () => service.ResumeAsync(
            "command-1",
            new ResumeExecutionRequest("run-1", "step-1", Approved: true));

        detail.Should().NotBeNull();
        detail!.Status.Should().Be("outcome_uncertain");
        detail.CompletedAtUtc.Should().Be(observedAt);
        detail.Error.Should().Be("service run outcome is uncertain");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal status 'outcome_uncertain'*");
        resumeDispatch.Commands.Should().BeEmpty();
    }

    private sealed class UnexpectedServiceInvocationPort : IServiceInvocationPort
    {
        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Invocation was not expected.");
    }

    private sealed class FixedServiceRunQueryPort(ServiceRunSnapshot run) : IServiceRunQueryPort
    {
        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceRunSnapshot>>([run]);

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRunSnapshot?>(run);

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRunSnapshot?>(run);
    }

    private sealed class RecordingWorkflowControlDispatchService<TCommand>
        : ICommandDispatchService<TCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public List<TCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            TCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(
                CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                    new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "command-control-1", "correlation-control-1")));
        }
    }

    private sealed class FixedAppScopeResolver(string scopeId) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) => new(scopeId, "test");

        public bool HasHttpRequestContext(HttpContext? httpContext = null) => false;

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
    }
}
