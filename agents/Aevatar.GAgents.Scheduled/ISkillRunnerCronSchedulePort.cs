namespace Aevatar.GAgents.Scheduled;

internal interface ISkillRunnerCronSchedulePort
{
    Task EnsureAsync(
        string agentId,
        InitializeSkillRunnerCommand command,
        CancellationToken ct = default);

    Task EnableAsync(
        string agentId,
        string reason,
        CancellationToken ct = default);

    Task DisableAsync(
        string agentId,
        string reason,
        CancellationToken ct = default);
}
