namespace Aevatar.GAgents.Scheduled;

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
