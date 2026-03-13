namespace Aevatar.Workflow.Application.Abstractions.Authoring;

public interface IWorkflowAuthoringQueryApplicationService
{
    Task<PlaygroundWorkflowParseResult> ParseWorkflowAsync(
        PlaygroundWorkflowParseRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowPrimitiveDescriptor>> ListPrimitivesAsync(
        CancellationToken ct = default);

    Task<WorkflowLlmStatus> GetLlmStatusAsync(CancellationToken ct = default);
}
