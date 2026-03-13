namespace Aevatar.Workflow.Application.Abstractions.Authoring;

public interface IWorkflowDefinitionValidationPort
{
    Task<PlaygroundWorkflowParseResult> ParseWorkflowAsync(
        PlaygroundWorkflowParseRequest request,
        CancellationToken ct = default);
}
