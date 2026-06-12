using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Modules;

public interface IWorkflowTool
{
    string Name { get; }

    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);

    Task<string> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default) =>
        ExecuteAsync(request.ArgumentsJson, ct);
}

public sealed record WorkflowToolExecutionRequest
{
    public WorkflowToolExecutionRequest(
        string ArgumentsJson,
        IReadOnlyList<WorkflowFileRef>? InputFileRefs = null)
    {
        this.ArgumentsJson = ArgumentsJson;
        this.InputFileRefs = CopyInputFileRefs(InputFileRefs);
    }

    public string ArgumentsJson { get; init; }

    public IReadOnlyList<WorkflowFileRef> InputFileRefs { get; private init; }

    private static IReadOnlyList<WorkflowFileRef> CopyInputFileRefs(
        IReadOnlyList<WorkflowFileRef>? inputFileRefs) =>
        inputFileRefs == null || inputFileRefs.Count == 0
            ? []
            : inputFileRefs.Select(static fileRef => fileRef.Clone()).ToArray();
}

public interface IWorkflowToolSource
{
    Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default);
}
