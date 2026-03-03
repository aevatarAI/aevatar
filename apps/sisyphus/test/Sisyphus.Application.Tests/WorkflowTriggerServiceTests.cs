using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sisyphus.Application.Models;
using Sisyphus.Application.Services;
using Sisyphus.Application.Tests.Fakes;

namespace Sisyphus.Application.Tests;

public class WorkflowTriggerServiceTests
{
    private readonly FakeWorkflowRunCommandService _runService = new();
    private readonly FakeWorkflowDefinitionRegistry _registry = new();
    private readonly GraphIdProvider _graphIdProvider = new();

    public WorkflowTriggerServiceTests()
    {
        _graphIdProvider.SetRead("read-graph-id");
        _graphIdProvider.SetWrite("write-graph-id");
    }

    private WorkflowTriggerService CreateSut() => new(
        _runService,
        _registry,
        _graphIdProvider,
        NullLogger<WorkflowTriggerService>.Instance);

    private static ResearchSession MakeSession(int maxRounds = 20) => new()
    {
        Topic = "test topic",
        MaxRounds = maxRounds,
    };

    // ─── P1-1: Terminal status mapping ───

    [Fact]
    public async Task TriggerAsync_Completed_SetsSessionCompleted()
    {
        _runService.NextResult = MakeResult(
            WorkflowChatRunStartError.None,
            WorkflowProjectionCompletionStatus.Completed);

        var session = MakeSession();
        await CreateSut().TriggerAsync(session);

        session.Status.Should().Be(SessionStatus.Completed);
        session.CompletedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(WorkflowProjectionCompletionStatus.Failed)]
    [InlineData(WorkflowProjectionCompletionStatus.TimedOut)]
    [InlineData(WorkflowProjectionCompletionStatus.Stopped)]
    [InlineData(WorkflowProjectionCompletionStatus.Unknown)]
    [InlineData(WorkflowProjectionCompletionStatus.NotFound)]
    [InlineData(WorkflowProjectionCompletionStatus.Disabled)]
    public async Task TriggerAsync_NonCompleted_SetsSessionFailed(
        WorkflowProjectionCompletionStatus completionStatus)
    {
        _runService.NextResult = MakeResult(
            WorkflowChatRunStartError.None,
            completionStatus);

        var session = MakeSession();
        await CreateSut().TriggerAsync(session);

        session.Status.Should().Be(SessionStatus.Failed);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TriggerAsync_StartError_SetsSessionFailed()
    {
        _runService.NextResult = new WorkflowChatRunExecutionResult(
            WorkflowChatRunStartError.WorkflowNotFound, null, null);

        var session = MakeSession();
        await CreateSut().TriggerAsync(session);

        session.Status.Should().Be(SessionStatus.Failed);
    }

    [Fact]
    public async Task TriggerAsync_SucceededButNullFinalizeResult_SetsSessionFailed()
    {
        // Succeeded == true (Error is None) but FinalizeResult is null
        _runService.NextResult = new WorkflowChatRunExecutionResult(
            WorkflowChatRunStartError.None,
            new WorkflowChatRunStarted("actor", "wf", "cmd"),
            null);

        var session = MakeSession();
        await CreateSut().TriggerAsync(session);

        session.Status.Should().Be(SessionStatus.Failed);
    }

    // ─── P1-2: Exception convergence (tested via TriggerAsync throwing) ───

    [Fact]
    public async Task TriggerAsync_ThrowsOnExecute_PropagatesException()
    {
        _runService.ThrowOnExecute = new InvalidOperationException("boom");

        var session = MakeSession();
        var act = () => CreateSut().TriggerAsync(session);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("boom");
    }

    // ─── P2-1: maxRounds passthrough ───

    [Fact]
    public async Task TriggerAsync_PatchesMaxIterationsInYaml()
    {
        _registry.WorkflowYaml = """
            steps:
              - id: research_loop
                type: while
                parameters:
                  max_iterations: "20"
            """;
        _runService.NextResult = MakeResult(
            WorkflowChatRunStartError.None,
            WorkflowProjectionCompletionStatus.Completed);

        var session = MakeSession(maxRounds: 42);
        await CreateSut().TriggerAsync(session);

        _runService.CapturedRequest.Should().NotBeNull();
        _runService.CapturedRequest!.WorkflowYaml.Should().Contain("""max_iterations: "42" """.Trim());
        _runService.CapturedRequest!.WorkflowYaml.Should().NotContain("""max_iterations: "20" """.Trim());
    }

    [Fact]
    public async Task TriggerAsync_NullYaml_FallsBackToNameOnly()
    {
        _registry.WorkflowYaml = null;
        _runService.NextResult = MakeResult(
            WorkflowChatRunStartError.None,
            WorkflowProjectionCompletionStatus.Completed);

        var session = MakeSession(maxRounds: 42);
        await CreateSut().TriggerAsync(session);

        _runService.CapturedRequest.Should().NotBeNull();
        _runService.CapturedRequest!.WorkflowYaml.Should().BeNull();
        _runService.CapturedRequest!.WorkflowName.Should().Be(WorkflowTriggerService.WorkflowName);
    }

    // ─── PatchMaxIterations unit tests ───

    [Fact]
    public void PatchMaxIterations_ReplacesQuotedValue()
    {
        var yaml = """
            parameters:
              max_iterations: "20"
            """;

        var patched = WorkflowTriggerService.PatchMaxIterations(yaml, 5);
        patched.Should().Contain("""max_iterations: "5" """.Trim());
    }

    [Fact]
    public void PatchMaxIterations_ReplacesUnquotedValue()
    {
        var yaml = """
            parameters:
              max_iterations: 20
            """;

        var patched = WorkflowTriggerService.PatchMaxIterations(yaml, 100);
        patched.Should().Contain("""max_iterations: "100" """.Trim());
    }

    [Fact]
    public void PatchMaxIterations_NullYaml_ReturnsNull()
    {
        WorkflowTriggerService.PatchMaxIterations(null, 10).Should().BeNull();
    }

    // ─── Helpers ───

    private static WorkflowChatRunExecutionResult MakeResult(
        WorkflowChatRunStartError error,
        WorkflowProjectionCompletionStatus completionStatus) =>
        new(
            error,
            error == WorkflowChatRunStartError.None
                ? new WorkflowChatRunStarted("actor-1", "sisyphus_research", "cmd-1")
                : null,
            new WorkflowChatRunFinalizeResult(completionStatus, completionStatus == WorkflowProjectionCompletionStatus.Completed));
}
