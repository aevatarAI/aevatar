namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Application-service surface for WorkflowAgent lifecycle. Mirrors
/// <see cref="ISkillDefinitionCommandPort"/>: owns actor lifecycle, catalog
/// projection priming, and envelope dispatch through
/// <see cref="Aevatar.Foundation.Abstractions.IActorDispatchPort"/> so LLM
/// tools and admin endpoints stop reaching for <c>actor.HandleEventAsync</c>.
/// </summary>
public interface IWorkflowAgentCommandPort
{
    Task InitializeAsync(
        string agentId,
        InitializeWorkflowAgentCommand command,
        bool runImmediately,
        CancellationToken ct = default);

    Task TriggerAsync(
        string agentId,
        string reason,
        string? revisionFeedback,
        CancellationToken ct = default);

    Task DisableAsync(string agentId, string reason, CancellationToken ct = default);

    Task EnableAsync(string agentId, string reason, CancellationToken ct = default);
}
