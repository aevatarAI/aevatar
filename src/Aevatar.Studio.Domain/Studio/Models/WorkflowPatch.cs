namespace Aevatar.Studio.Domain.Studio.Models;

public sealed record WorkflowPatch
{
    public List<WorkflowPatchOperation> Operations { get; init; } = [];
}

public sealed record WorkflowPatchOperation(string Operation, string Path, string Description);
