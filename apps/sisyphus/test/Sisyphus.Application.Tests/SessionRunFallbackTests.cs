using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sisyphus.Application.Models;
using Sisyphus.Application.Services;
using Sisyphus.Application.Tests.Fakes;

namespace Sisyphus.Application.Tests;

/// <summary>
/// Tests the exception convergence behavior: when TriggerAsync throws,
/// the background wrapper (RunTriggerWithFallbackAsync in SessionEndpoints)
/// must converge the session to Failed. We test the same pattern here at
/// the unit level by wrapping TriggerAsync with try/catch.
/// </summary>
public class SessionRunFallbackTests
{
    private readonly FakeWorkflowRunCommandService _runService = new();
    private readonly FakeWorkflowQueryService _queryService = new();
    private readonly GraphIdProvider _graphIdProvider = new();

    public SessionRunFallbackTests()
    {
        _graphIdProvider.SetRead("read-graph-id");
        _graphIdProvider.SetWrite("write-graph-id");
    }

    private WorkflowTriggerService CreateSut() => new(
        _runService,
        _queryService,
        _graphIdProvider,
        NullLogger<WorkflowTriggerService>.Instance);

    [Fact]
    public async Task Background_ExecuteThrows_SessionConvergesToFailed()
    {
        _runService.ThrowOnExecute = new InvalidOperationException("workflow exploded");
        var session = new ResearchSession { Topic = "test" };
        session.Status = SessionStatus.Running;

        // Simulate the RunTriggerWithFallbackAsync pattern from SessionEndpoints
        try
        {
            await CreateSut().TriggerAsync(session, ct: CancellationToken.None);
        }
        catch
        {
            session.Status = SessionStatus.Failed;
            session.CompletedAt = DateTime.UtcNow;
        }

        session.Status.Should().Be(SessionStatus.Failed);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Background_ExecuteSucceeds_SessionDoesNotStayRunning()
    {
        _runService.NextResult = new WorkflowChatRunExecutionResult(
            WorkflowChatRunStartError.None,
            new WorkflowChatRunStarted("a", "wf", "c"),
            new WorkflowChatRunFinalizeResult(
                WorkflowProjectionCompletionStatus.Completed, true));

        var session = new ResearchSession { Topic = "test" };
        session.Status = SessionStatus.Running;

        await CreateSut().TriggerAsync(session, ct: CancellationToken.None);

        session.Status.Should().NotBe(SessionStatus.Running,
            "session must converge to a terminal state, never stay Running");
    }

    [Fact]
    public async Task Background_WorkflowTimedOut_SessionConvergesToFailed()
    {
        _runService.NextResult = new WorkflowChatRunExecutionResult(
            WorkflowChatRunStartError.None,
            new WorkflowChatRunStarted("a", "wf", "c"),
            new WorkflowChatRunFinalizeResult(
                WorkflowProjectionCompletionStatus.TimedOut, false));

        var session = new ResearchSession { Topic = "test" };
        session.Status = SessionStatus.Running;

        await CreateSut().TriggerAsync(session, ct: CancellationToken.None);

        session.Status.Should().Be(SessionStatus.Failed,
            "TimedOut must map to Failed, not Completed");
    }
}
