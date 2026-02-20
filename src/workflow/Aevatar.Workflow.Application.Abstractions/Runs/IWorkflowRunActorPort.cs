using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

/// <summary>
/// Port for resolving, creating, and configuring workflow-capable actors.
/// Implemented by infrastructure to avoid Application depending on Workflow.Core.
/// </summary>
public interface IWorkflowRunActorPort
{
    Task<IActor?> GetAsync(string actorId, CancellationToken ct = default);

    Task<IActor> CreateAsync(CancellationToken ct = default);

    bool IsWorkflowActor(IActor actor);

    string? GetBoundWorkflowName(IActor actor);

    void ConfigureWorkflow(IActor actor, string workflowYaml, string workflowName);
}
