using System.Globalization;
using System.Text.RegularExpressions;
using Sisyphus.Application.Models;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Microsoft.Extensions.Logging;

namespace Sisyphus.Application.Services;

public sealed class WorkflowTriggerService(
    IWorkflowRunCommandService workflowRunService,
    IWorkflowDefinitionRegistry workflowRegistry,
    SessionLifecycleService lifecycle,
    ILogger<WorkflowTriggerService> logger)
{
    private const string ResearchWorkflowName = "sisyphus_research";
    private static readonly Regex MaxIterationsPattern = new(
        @"^(\s*max_iterations:\s*)"".*?""(\s*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public async Task TriggerAsync(
        Guid sessionId,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask>? emitAsync = null,
        CancellationToken ct = default)
    {
        var session = lifecycle.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");

        var prompt = $"""
            Research Topic: {session.Topic}
            Graph ID: {session.GraphId}
            Max Rounds: {session.MaxRounds}

            Begin the research loop. Read the current graph state, identify knowledge gaps, produce claims, verify them, and write verified claims back to the graph.
            """;

        WorkflowChatRunExecutionResult result;
        try
        {
            var workflowYaml = BuildWorkflowYamlForSession(session.MaxRounds);

            result = await workflowRunService.ExecuteAsync(
                new WorkflowChatRunRequest(
                    Prompt: prompt,
                    WorkflowName: ResearchWorkflowName,
                    ActorId: session.ActorId,
                    WorkflowYaml: workflowYaml),
                emitAsync ?? ((_, _) => ValueTask.CompletedTask),
                onStartedAsync: (started, _) =>
                {
                    lock (session)
                    {
                        session.ActorId = started.ActorId;
                        session.CommandId = started.CommandId;
                    }

                    return ValueTask.CompletedTask;
                },
                ct);
        }
        catch (Exception ex)
        {
            lifecycle.MarkSessionFailed(sessionId, $"RUN_EXCEPTION:{ex.GetType().Name}");
            logger.LogError(ex, "Workflow run crashed for session {SessionId}", sessionId);
            throw;
        }

        if (IsCompleted(result))
        {
            lifecycle.MarkSessionCompleted(sessionId);
            return;
        }

        lifecycle.MarkSessionFailed(sessionId, BuildFailureReason(result));
    }

    private string BuildWorkflowYamlForSession(int maxRounds)
    {
        var template = workflowRegistry.GetYaml(ResearchWorkflowName);
        if (string.IsNullOrWhiteSpace(template))
            throw new InvalidOperationException($"Workflow '{ResearchWorkflowName}' is not registered.");

        var replaced = MaxIterationsPattern.Replace(
            template,
            $@"$1""{maxRounds.ToString(CultureInfo.InvariantCulture)}""$2",
            1);

        if (ReferenceEquals(replaced, template) || replaced == template)
        {
            logger.LogWarning(
                "Failed to patch max_iterations for workflow {WorkflowName}; falling back to template value.",
                ResearchWorkflowName);
        }

        return replaced;
    }

    private static bool IsCompleted(WorkflowChatRunExecutionResult result)
    {
        if (result.Error != WorkflowChatRunStartError.None)
            return false;

        return result.FinalizeResult?.ProjectionCompletionStatus == WorkflowProjectionCompletionStatus.Completed;
    }

    private static string BuildFailureReason(WorkflowChatRunExecutionResult result)
    {
        if (result.Error != WorkflowChatRunStartError.None)
            return $"START_ERROR:{result.Error}";

        if (result.FinalizeResult == null)
            return "FINALIZE_RESULT_MISSING";

        return $"FINALIZE_STATUS:{result.FinalizeResult.ProjectionCompletionStatus}";
    }
}
