namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Read port for SkillRunner-owned execution read models. Callers compose these rows
/// with catalog rows only at their own consumer boundary.
/// </summary>
public interface ISkillRunnerExecutionQueryPort
{
    Task<SkillRunnerExecutionDocument?> GetAsync(string agentId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, SkillRunnerExecutionDocument>> QueryByAgentIdsAsync(
        IReadOnlyCollection<string> agentIds,
        CancellationToken ct = default);
}
