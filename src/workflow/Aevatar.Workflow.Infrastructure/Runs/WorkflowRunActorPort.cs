using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Infrastructure.Runs;

/// <summary>
/// Infrastructure adapter for workflow actor lifecycle and binding operations.
/// </summary>
internal sealed class WorkflowRunActorPort : IWorkflowRunActorPort
{
    private readonly IActorRuntime _runtime;

    public WorkflowRunActorPort(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<IActor?> GetAsync(string actorId, CancellationToken ct = default)
    {
        _ = ct;
        return _runtime.GetAsync(actorId);
    }

    public Task<IActor> CreateAsync(CancellationToken ct = default) =>
        _runtime.CreateAsync<WorkflowGAgent>(ct: ct);

    public bool IsWorkflowActor(IActor actor) => actor.Agent is WorkflowGAgent;

    public string? GetBoundWorkflowName(IActor actor) =>
        (actor.Agent as WorkflowGAgent)?.State.WorkflowName;

    public void ConfigureWorkflow(IActor actor, string workflowYaml, string workflowName)
    {
        if (actor.Agent is not WorkflowGAgent workflowAgent)
            throw new InvalidOperationException("Current actor adapter requires WorkflowGAgent.");

        workflowAgent.ConfigureWorkflow(workflowYaml, workflowName);
    }
}
