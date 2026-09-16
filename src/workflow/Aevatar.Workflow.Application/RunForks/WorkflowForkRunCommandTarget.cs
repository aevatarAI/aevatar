using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunCommandTarget : ICommandDispatchTarget, ICommandDispatchCleanupAware
{
    private readonly WorkflowRunAcceptedCommandTarget _innerTarget;

    public WorkflowForkRunCommandTarget(
        string sourceRunId,
        string originalRunId,
        string startAtStepId,
        string actorId,
        string runId,
        string workflowName,
        WorkflowChatRunRequest preparedRequest,
        IReadOnlyList<string>? createdActorIds,
        IWorkflowRunProvisioningPort runProvisioningPort)
    {
        SourceRunId = Normalize(sourceRunId);
        OriginalRunId = Normalize(originalRunId);
        StartAtStepId = Normalize(startAtStepId);
        RunId = Normalize(runId);
        PreparedRequest = preparedRequest ?? throw new ArgumentNullException(nameof(preparedRequest));
        _innerTarget = new WorkflowRunAcceptedCommandTarget(
            actorId,
            workflowName,
            createdActorIds,
            runProvisioningPort);
    }

    public string SourceRunId { get; }

    public string OriginalRunId { get; }

    public string StartAtStepId { get; }

    public string RunId { get; }

    public WorkflowChatRunRequest PreparedRequest { get; }

    public string ActorId => _innerTarget.ActorId;

    public string WorkflowName => _innerTarget.WorkflowName;

    public IReadOnlyList<string> CreatedActorIds => _innerTarget.CreatedActorIds;

    public string TargetId => _innerTarget.TargetId;

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        _innerTarget.CleanupAfterDispatchFailureAsync(ct);

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
