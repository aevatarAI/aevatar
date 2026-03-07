namespace Aevatar.Workflow.Application.Workflows;

public interface IWorkflowDefinitionSeedSource
{
    IReadOnlyDictionary<string, string> GetSeedDefinitions();
}
