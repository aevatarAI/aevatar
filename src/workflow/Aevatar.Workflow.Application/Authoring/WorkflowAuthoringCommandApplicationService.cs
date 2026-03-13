using Aevatar.Workflow.Application.Abstractions.Authoring;

namespace Aevatar.Workflow.Application.Authoring;

public sealed class WorkflowAuthoringCommandApplicationService : IWorkflowAuthoringCommandApplicationService
{
    private readonly IWorkflowDefinitionValidationPort _validationPort;
    private readonly IWorkflowAuthoringPersistencePort _persistencePort;

    public WorkflowAuthoringCommandApplicationService(
        IWorkflowDefinitionValidationPort validationPort,
        IWorkflowAuthoringPersistencePort persistencePort)
    {
        _validationPort = validationPort ?? throw new ArgumentNullException(nameof(validationPort));
        _persistencePort = persistencePort ?? throw new ArgumentNullException(nameof(persistencePort));
    }

    public async Task<PlaygroundWorkflowSaveResult> SaveWorkflowAsync(
        PlaygroundWorkflowSaveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parseResult = await _validationPort.ParseWorkflowAsync(
            new PlaygroundWorkflowParseRequest
            {
                Yaml = request.Yaml,
            },
            ct);

        if (!parseResult.Valid || parseResult.Definition == null)
        {
            throw new WorkflowAuthoringValidationException(
                parseResult.Error ?? "workflow yaml validation failed",
                parseResult.Errors);
        }

        return await _persistencePort.SaveWorkflowAsync(
            request,
            parseResult.Definition.Name,
            ct);
    }
}
