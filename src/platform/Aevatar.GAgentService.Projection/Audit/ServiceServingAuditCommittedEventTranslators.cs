using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Projection.Audit;

public sealed class ServiceServingSetUpdatedAuditTranslator
    : AuditTranslatorBase<ServiceServingSetUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceServingSetUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceServingSetUpdatedEvent evt) =>
        ServiceSeed(
            "service.serving_set.updated",
            evt.Identity,
            evt.Identity?.ServiceId ?? string.Empty,
            "",
            $"Service serving set updated for {evt.Identity?.ServiceId ?? string.Empty} (generation {evt.Generation}).",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["generation"] = evt.Generation.ToString(),
                ["rollout_id"] = evt.RolloutId ?? string.Empty,
                ["target_count"] = evt.Targets.Count.ToString(),
                ["reason"] = evt.Reason ?? string.Empty,
            });
}

public sealed class ServiceRolloutStartedAuditTranslator
    : AuditTranslatorBase<ServiceRolloutStartedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRolloutStartedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRolloutStartedEvent evt) =>
        ServiceSeed(
            "service.rollout.started",
            evt.Identity,
            evt.Plan?.RolloutId ?? string.Empty,
            "",
            $"Service rollout started: {evt.Plan?.RolloutId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rollout_id"] = evt.Plan?.RolloutId ?? string.Empty,
                ["stage_count"] = (evt.Plan?.Stages.Count ?? 0).ToString(),
            },
            lifecyclePhase: AuditLifecyclePhase.Running,
            terminalOutcome: AuditTerminalOutcome.Unspecified);
}

public sealed class ServiceRolloutStageAdvancedAuditTranslator
    : AuditTranslatorBase<ServiceRolloutStageAdvancedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRolloutStageAdvancedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRolloutStageAdvancedEvent evt) =>
        ServiceSeed(
            "service.rollout.stage_advanced",
            evt.Identity,
            evt.RolloutId ?? string.Empty,
            "",
            $"Service rollout {evt.RolloutId ?? string.Empty} advanced to stage {evt.StageId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rollout_id"] = evt.RolloutId ?? string.Empty,
                ["stage_index"] = evt.StageIndex.ToString(),
                ["stage_id"] = evt.StageId ?? string.Empty,
            },
            lifecyclePhase: AuditLifecyclePhase.Running,
            terminalOutcome: AuditTerminalOutcome.Unspecified);
}

public sealed class ServiceRolloutPausedAuditTranslator
    : AuditTranslatorBase<ServiceRolloutPausedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRolloutPausedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRolloutPausedEvent evt) =>
        ServiceSeed(
            "service.rollout.paused",
            evt.Identity,
            evt.RolloutId ?? string.Empty,
            "",
            $"Service rollout paused: {evt.RolloutId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rollout_id"] = evt.RolloutId ?? string.Empty,
                ["reason"] = evt.Reason ?? string.Empty,
            },
            lifecyclePhase: AuditLifecyclePhase.Running,
            terminalOutcome: AuditTerminalOutcome.Unspecified);
}

public sealed class ServiceRolloutResumedAuditTranslator
    : AuditTranslatorBase<ServiceRolloutResumedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRolloutResumedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRolloutResumedEvent evt) =>
        ServiceSeed(
            "service.rollout.resumed",
            evt.Identity,
            evt.RolloutId ?? string.Empty,
            "",
            $"Service rollout resumed: {evt.RolloutId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rollout_id"] = evt.RolloutId ?? string.Empty,
            },
            lifecyclePhase: AuditLifecyclePhase.Running,
            terminalOutcome: AuditTerminalOutcome.Unspecified);
}

public sealed class ServiceRolloutCompletedAuditTranslator
    : AuditTranslatorBase<ServiceRolloutCompletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRolloutCompletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRolloutCompletedEvent evt) =>
        ServiceSeed(
            "service.rollout.completed",
            evt.Identity,
            evt.RolloutId ?? string.Empty,
            "",
            $"Service rollout completed: {evt.RolloutId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rollout_id"] = evt.RolloutId ?? string.Empty,
            });
}

public sealed class ServiceRolloutRolledBackAuditTranslator
    : AuditTranslatorBase<ServiceRolloutRolledBackEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRolloutRolledBackEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRolloutRolledBackEvent evt) =>
        ServiceSeed(
            "service.rollout.rolled_back",
            evt.Identity,
            evt.RolloutId ?? string.Empty,
            "",
            $"Service rollout rolled back: {evt.RolloutId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rollout_id"] = evt.RolloutId ?? string.Empty,
                ["reason"] = evt.Reason ?? string.Empty,
                ["target_count"] = evt.Targets.Count.ToString(),
            },
            isDestructive: true);
}

public sealed class ServiceRolloutFailedAuditTranslator
    : AuditTranslatorBase<ServiceRolloutFailedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ServiceRolloutFailedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ServiceRolloutFailedEvent evt) =>
        ServiceSeed(
            "service.rollout.failed",
            evt.Identity,
            evt.RolloutId ?? string.Empty,
            "",
            $"Service rollout failed: {evt.RolloutId ?? string.Empty}.",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rollout_id"] = evt.RolloutId ?? string.Empty,
            },
            terminalOutcome: AuditTerminalOutcome.Failed,
            failure: Failure(
                "service_rollout_failed",
                AuditFailureCategory.Execution,
                AuditLifecyclePhase.Running));
}
