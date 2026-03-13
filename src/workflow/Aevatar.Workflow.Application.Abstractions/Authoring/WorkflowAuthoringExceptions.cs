namespace Aevatar.Workflow.Application.Abstractions.Authoring;

public sealed class WorkflowAuthoringValidationException : InvalidOperationException
{
    public WorkflowAuthoringValidationException(
        string message,
        IReadOnlyList<string>? errors = null)
        : base(message)
    {
        Errors = errors?.ToArray() ?? [];
    }

    public IReadOnlyList<string> Errors { get; }
}

public sealed class WorkflowAuthoringConflictException : InvalidOperationException
{
    public WorkflowAuthoringConflictException(
        string message,
        string filename,
        string savedPath)
        : base(message)
    {
        Filename = filename;
        SavedPath = savedPath;
    }

    public string Filename { get; }

    public string SavedPath { get; }
}
