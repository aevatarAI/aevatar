using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunControlCommandTarget : ICommandDispatchTarget
{
    public WorkflowRunControlCommandTarget(
        string actorId,
        string runId)
    {
        ActorId = string.IsNullOrWhiteSpace(actorId)
            ? throw new ArgumentException("Actor id is required.", nameof(actorId))
            : actorId;
        RunId = string.IsNullOrWhiteSpace(runId)
            ? throw new ArgumentException("Run id is required.", nameof(runId))
            : runId;
    }

    public string ActorId { get; }

    public string RunId { get; }

    public string TargetId => ActorId;
}
