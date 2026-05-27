namespace Aevatar.Workflow.Core.Modules;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old: Workflow.Core tool_call depended on AI tool provider abstractions. New: Core owns a narrow workflow tool contract and adapters bridge external tool systems outside Core.
public interface IWorkflowTool
{
    string Name { get; }

    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old: Workflow.Core discovered IAgentToolSource directly. New: Workflow.Core discovers workflow-owned tool sources.
public interface IWorkflowToolSource
{
    Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default);
}
