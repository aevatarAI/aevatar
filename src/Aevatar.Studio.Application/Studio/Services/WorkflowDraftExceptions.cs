namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkflowDraftNotFoundException : InvalidOperationException
{
    public WorkflowDraftNotFoundException(string workflowId)
        : base($"Workflow draft '{workflowId}' was not found.")
    {
        WorkflowId = workflowId;
    }

    public string WorkflowId { get; }
}

public sealed class WorkflowDraftPathConflictException : InvalidOperationException
{
    public WorkflowDraftPathConflictException(
        string workflowId,
        string targetPath,
        string conflictingWorkflowId)
        : base(
            $"Draft '{workflowId}' cannot move to '{targetPath}' because that path is already used by draft '{conflictingWorkflowId}'.")
    {
        WorkflowId = workflowId;
        TargetPath = targetPath;
        ConflictingWorkflowId = conflictingWorkflowId;
    }

    public string WorkflowId { get; }

    public string TargetPath { get; }

    public string ConflictingWorkflowId { get; }
}

public sealed class WorkflowDraftVersionConflictException : InvalidOperationException
{
    public WorkflowDraftVersionConflictException(
        string workflowId,
        long expectedDraftVersion,
        long currentDraftVersion)
        : base(
            $"Workflow draft '{workflowId}' expected draftVersion {expectedDraftVersion} but current draftVersion is {currentDraftVersion}.")
    {
        WorkflowId = workflowId;
        ExpectedDraftVersion = expectedDraftVersion;
        CurrentDraftVersion = currentDraftVersion;
    }

    public string WorkflowId { get; }

    public long ExpectedDraftVersion { get; }

    public long CurrentDraftVersion { get; }
}
