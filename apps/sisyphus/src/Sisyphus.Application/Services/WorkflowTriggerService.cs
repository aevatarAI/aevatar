using Sisyphus.Application.Models;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Sisyphus.Application.Services;

public sealed class WorkflowTriggerService(
    IWorkflowRunCommandService workflowRunService,
    GraphIdProvider graphIdProvider)
{
    public async Task TriggerAsync(
        ResearchSession session,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask>? emitAsync = null,
        CancellationToken ct = default)
    {
        var graphId = await graphIdProvider.WaitAsync(ct);

        var prompt = $"""
            Research Topic: {session.Topic}
            Graph ID: {graphId}
            Max Rounds: {session.MaxRounds}

            Begin the research loop. Read the current graph state, identify knowledge gaps, produce claims, verify them, and write verified claims back to the graph.
            """;

        var result = await workflowRunService.ExecuteAsync(
            new WorkflowChatRunRequest(prompt, "sisyphus_research", session.ActorId),
            emitAsync ?? ((_, _) => ValueTask.CompletedTask),
            onStartedAsync: (started, _) =>
            {
                session.ActorId = started.ActorId;
                session.CommandId = started.CommandId;
                return ValueTask.CompletedTask;
            },
            ct);

        session.Status = result.Succeeded ? SessionStatus.Completed : SessionStatus.Failed;
        session.CompletedAt = DateTime.UtcNow;
    }
}
