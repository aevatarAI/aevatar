namespace Aevatar.Tools.Cli.Studio.Domain.Models;

public sealed record WorkflowPatch
{
    public List<WorkflowPatchOperation> Operations { get; init; } = [];
}

public sealed record WorkflowPatchOperation(string Operation, string Path, string Description);
