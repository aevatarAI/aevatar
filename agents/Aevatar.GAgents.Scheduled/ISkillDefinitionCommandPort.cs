namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Dispatches scheduled-skill definition commands through actor envelopes while preserving
/// the catalog projection activation ordering needed by agent status reads.
/// </summary>
public interface ISkillDefinitionCommandPort
{
    Task InitializeAsync(
        string agentId,
        InitializeSkillDefinitionCommand command,
        bool runImmediately,
        CancellationToken ct = default);

    Task TriggerAsync(string agentId, string reason, CancellationToken ct = default);

    Task DisableAsync(string agentId, string reason, CancellationToken ct = default);

    Task EnableAsync(string agentId, string reason, CancellationToken ct = default);
}
