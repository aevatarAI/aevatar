namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Application-service surface for SkillRunner lifecycle. Owns actor lifecycle
/// (Get-or-Create), catalog projection priming, and envelope dispatch via
/// <see cref="Aevatar.Foundation.Abstractions.IActorDispatchPort"/> so callers
/// (LLM tools, admin endpoints) stay parameter-mapping adapters and never
/// reach for <c>actor.HandleEventAsync</c> directly.
/// </summary>
public interface ISkillRunnerCommandPort
{
    /// <summary>
    /// Materializes the SkillRunner actor for <paramref name="agentId"/>, primes the
    /// UserAgentCatalog projection scope, dispatches the supplied
    /// <see cref="InitializeSkillRunnerCommand"/>, and optionally follows it
    /// with a first <see cref="TriggerSkillRunnerExecutionCommand"/>.
    /// </summary>
    Task InitializeAsync(
        string agentId,
        InitializeSkillRunnerCommand command,
        bool runImmediately,
        CancellationToken ct = default);

    /// <summary>Dispatches a <see cref="TriggerSkillRunnerExecutionCommand"/> to an existing SkillRunner.</summary>
    Task TriggerAsync(string agentId, string reason, CancellationToken ct = default);

    /// <summary>Dispatches a <see cref="DisableSkillRunnerCommand"/> to an existing SkillRunner.</summary>
    Task DisableAsync(string agentId, string reason, CancellationToken ct = default);

    /// <summary>Dispatches an <see cref="EnableSkillRunnerCommand"/> to an existing SkillRunner.</summary>
    Task EnableAsync(string agentId, string reason, CancellationToken ct = default);
}
