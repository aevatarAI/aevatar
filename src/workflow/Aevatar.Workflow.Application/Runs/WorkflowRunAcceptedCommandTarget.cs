using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

// Refactor (iter18/cluster-005):
//   Old pattern: accepted-only dispatch reused interaction targets that owned live sinks
//   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
internal sealed class WorkflowRunAcceptedCommandTarget
    : ICommandDispatchTarget,
      ICommandDispatchCleanupAware
{
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private bool _createdActorsDestroyed;

    public WorkflowRunAcceptedCommandTarget(
        string actorId,
        string workflowName,
        IReadOnlyList<string>? createdActorIds,
        IWorkflowRunProvisioningPort runProvisioningPort)
    {
        ActorId = string.IsNullOrWhiteSpace(actorId)
            ? throw new ArgumentException("Actor id is required.", nameof(actorId))
            : actorId;
        WorkflowName = string.IsNullOrWhiteSpace(workflowName)
            ? throw new ArgumentException("Workflow name is required.", nameof(workflowName))
            : workflowName;
        CreatedActorIds = createdActorIds ?? [];
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
    }

    public string ActorId { get; }
    public string TargetId => ActorId;
    public string WorkflowName { get; }
    public IReadOnlyList<string> CreatedActorIds { get; }

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default)
    {
        // Refactor (iter18/cluster-005):
        //   Old pattern: accepted-only dispatch reused interaction targets that owned live sinks
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        return DestroyCreatedActorsAsync(ct);
    }

    private async Task DestroyCreatedActorsAsync(CancellationToken ct)
    {
        if (_createdActorsDestroyed || CreatedActorIds.Count == 0)
            return;

        List<Exception>? failures = null;
        foreach (var actorId in CreatedActorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
        {
            try
            {
                await _runProvisioningPort.DestroyAsync(actorId, ct);
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(new InvalidOperationException(
                    $"Failed to destroy workflow actor '{actorId}'.",
                    ex));
            }
        }

        if (failures == null)
        {
            _createdActorsDestroyed = true;
            return;
        }

        ExceptionDispatchInfo.Capture(
            failures.Count == 1
                ? failures[0]
                : new AggregateException("Workflow actor cleanup failed.", failures)).Throw();
    }
}
