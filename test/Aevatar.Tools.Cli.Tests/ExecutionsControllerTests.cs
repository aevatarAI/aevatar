using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;
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
        var service = new ExecutionService(
            new NoOpServiceInvocationPort(),
            new NoOpServiceRunQueryPort(),
            new NoOpResumeDispatchService(),
            new NoOpStopDispatchService(),
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

    private sealed class NoOpServiceInvocationPort : IServiceInvocationPort
    {
        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Controller test must fail before invoking a service.");
    }

    private sealed class NoOpServiceRunQueryPort : IServiceRunQueryPort
    {
        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ServiceRunSnapshot>>([]);

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default)
            => Task.FromResult<ServiceRunSnapshot?>(null);

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default)
            => Task.FromResult<ServiceRunSnapshot?>(null);
    }

    private sealed class NoOpResumeDispatchService
        : ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowResumeCommand command,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Controller test must fail before dispatching resume.");
    }

    private sealed class NoOpStopDispatchService
        : ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowStopCommand command,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Controller test must fail before dispatching stop.");
    }
}
