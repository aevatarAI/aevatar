using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunCommandTarget
    : ICommandDispatchTarget,
      ICommandDispatchCleanupAware
{
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private bool _createdActorsDestroyed;

    public WorkflowForkRunCommandTarget(
        string sourceRunId,
        string actorId,
        string workflowName,
        WorkflowChatRunRequest request,
        IReadOnlyList<string>? createdActorIds,
        IWorkflowRunProvisioningPort runProvisioningPort)
    {
        SourceRunId = string.IsNullOrWhiteSpace(sourceRunId)
            ? throw new ArgumentException("Source run id is required.", nameof(sourceRunId))
            : sourceRunId;
        ActorId = string.IsNullOrWhiteSpace(actorId)
            ? throw new ArgumentException("Actor id is required.", nameof(actorId))
            : actorId;
        WorkflowName = string.IsNullOrWhiteSpace(workflowName)
            ? throw new ArgumentException("Workflow name is required.", nameof(workflowName))
            : workflowName;
        Request = request ?? throw new ArgumentNullException(nameof(request));
        CreatedActorIds = createdActorIds ?? [];
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
    }

    public string SourceRunId { get; }
    public string ActorId { get; }
    public string TargetId => ActorId;
    public string WorkflowName { get; }
    public WorkflowChatRunRequest Request { get; }
    public IReadOnlyList<string> CreatedActorIds { get; }

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        DestroyCreatedActorsAsync(ct);

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
                await _runProvisioningPort.DestroyAsync(actorId, ct).ConfigureAwait(false);
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
