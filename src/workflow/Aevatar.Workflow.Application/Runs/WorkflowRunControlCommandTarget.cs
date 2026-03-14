using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunControlCommandTarget : IActorCommandDispatchTarget
{
    public WorkflowRunControlCommandTarget(
        IActor actor,
        string runId)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        RunId = string.IsNullOrWhiteSpace(runId)
            ? throw new ArgumentException("Run id is required.", nameof(runId))
            : runId;
    }

    public IActor Actor { get; }

    public string RunId { get; }

    public string TargetId => Actor.Id;

    public string ActorId => Actor.Id;
}
