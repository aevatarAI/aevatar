namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Provides alias-to-agent-type resolution for workflow step <c>parameters.agent_type</c>.
/// </summary>
public interface IWorkflowAgentTypeAliasProvider
{
    bool TryResolve(string alias, out Type agentType);
}
