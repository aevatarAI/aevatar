namespace Aevatar.Workflow.Core.Modules;

public interface IWorkflowTool
{
    string Name { get; }

    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}

public interface IWorkflowAgentTool : IWorkflowTool
{
    Aevatar.AI.Abstractions.ToolProviders.IAgentTool AgentTool { get; }
}

public interface IWorkflowToolSource
{
    Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default);
}
