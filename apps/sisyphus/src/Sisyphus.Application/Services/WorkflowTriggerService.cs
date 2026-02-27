using System.Text.RegularExpressions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Sisyphus.Application.Models;

namespace Sisyphus.Application.Services;

public sealed class WorkflowTriggerService(
    IWorkflowRunCommandService workflowRunService,
    IWorkflowExecutionQueryApplicationService workflowQueryService,
    GraphIdProvider graphIdProvider,
    ILogger<WorkflowTriggerService> logger)
{
    internal const string WorkflowName = "sisyphus_research";

    public async Task TriggerAsync(
        ResearchSession session,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask>? emitAsync = null,
        CancellationToken ct = default)
    {
        var readGraphId = await graphIdProvider.WaitReadAsync(ct);
        var writeGraphId = await graphIdProvider.WaitWriteAsync(ct);

        var prompt = $"""
            Research Topic: {session.Topic}
            Read Graph ID: {readGraphId}
            Write Graph ID: {writeGraphId}
            Max Rounds: {session.MaxRounds}

            Begin the research loop. Read the current graph state using the Read Graph ID, identify knowledge gaps, produce claims, verify them, and write verified claims to the Write Graph ID.
            """;

        // Patch workflow YAML to inject session's MaxRounds into while.max_iterations.
        // If the YAML registry returns null, fall back to name-only resolution (hardcoded default).
        var workflowYaml = PatchMaxIterations(
            workflowQueryService.GetWorkflowYaml(WorkflowName),
            session.MaxRounds);

        if (workflowYaml is null)
        {
            logger.LogWarning(
                "Workflow YAML for '{WorkflowName}' not found in registry — " +
                "max_iterations will use the YAML file default instead of session MaxRounds={MaxRounds}",
                WorkflowName, session.MaxRounds);
        }

        var result = await workflowRunService.ExecuteAsync(
            new WorkflowChatRunRequest(prompt, WorkflowName, session.ActorId, workflowYaml),
            emitAsync ?? ((_, _) => ValueTask.CompletedTask),
            onStartedAsync: (started, _) =>
            {
                session.ActorId = started.ActorId;
                session.CommandId = started.CommandId;
                return ValueTask.CompletedTask;
            },
            ct);

        // Map terminal status from FinalizeResult.ProjectionCompletionStatus,
        // not just result.Succeeded (which only indicates workflow *start* success).
        session.Status = result is
        {
            Succeeded: true,
            FinalizeResult.ProjectionCompletionStatus: WorkflowProjectionCompletionStatus.Completed
        }
            ? SessionStatus.Completed
            : SessionStatus.Failed;
        session.CompletedAt = DateTime.UtcNow;
    }

    internal static string? PatchMaxIterations(string? yaml, int maxRounds) =>
        yaml is null
            ? null
            : Regex.Replace(yaml, @"max_iterations:\s*""?\d+""?", $"""max_iterations: "{maxRounds}" """.Trim());
}
