using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Scripting.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Audit;

// Committed-fact audit translators for the scripting governance trail.
//
// Ownership / dedup (see report): every event below is a SINGLE-WRITE committed
// fact persisted by exactly one authoritative actor, and each is observed by the
// materialization scope that its own commit activates (authority scope for the
// definition/catalog actors; evolution scope for the session actor). The
// double-written evolution index-mirror events (proposed/rejected/promoted/
// rollback-requested/rolled-back, persisted by BOTH the session owner and the
// manager index) are intentionally NOT translated to avoid double-audit; the
// terminal ScriptEvolutionSessionCompletedEvent plus the catalog events capture
// the decision and the live-code change.
//
// Subject-bearing: script actors are keyed by scope / definition / session id
// (opaque internal identifiers), never a raw external subject, so the plain
// base is used (no origin-actor-id hashing).
//
// Confidentiality: no source text, candidate source, credential, or verbose
// build diagnostic is recorded — only stable governance identifiers plus the
// evolution decision (accepted / status / failure reason).

public sealed class ScriptDefinitionUpsertedAuditTranslator
    : ScriptAuditTranslatorBase<ScriptDefinitionUpsertedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScriptDefinitionUpsertedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScriptDefinitionUpsertedEvent evt) =>
        ScriptSeed(
            "script.definition.upserted",
            "script_definition",
            evt.ScriptId,
            evt.ScopeId,
            $"Script definition upserted for `{evt.ScriptId}` at revision `{evt.ScriptRevision}`.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["revision"] = evt.ScriptRevision ?? string.Empty,
                ["source_hash"] = evt.SourceHash ?? string.Empty,
            });
}

public sealed class ScriptCatalogRevisionPromotedAuditTranslator
    : ScriptAuditTranslatorBase<ScriptCatalogRevisionPromotedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScriptCatalogRevisionPromotedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScriptCatalogRevisionPromotedEvent evt) =>
        ScriptSeed(
            "script.catalog.revision.promoted",
            "script_catalog",
            evt.ScriptId,
            evt.ScopeId,
            $"Catalog live revision promoted to `{evt.Revision}` for `{evt.ScriptId}`.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["revision"] = evt.Revision ?? string.Empty,
                ["proposal_id"] = evt.ProposalId ?? string.Empty,
            });
}

public sealed class ScriptCatalogRollbackRequestedAuditTranslator
    : ScriptAuditTranslatorBase<ScriptCatalogRollbackRequestedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScriptCatalogRollbackRequestedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScriptCatalogRollbackRequestedEvent evt) =>
        ScriptSeed(
            "script.catalog.rollback.requested",
            "script_catalog",
            evt.ScriptId,
            evt.ScopeId,
            $"Catalog rollback requested to `{evt.TargetRevision}` for `{evt.ScriptId}`.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target_revision"] = evt.TargetRevision ?? string.Empty,
                ["proposal_id"] = evt.ProposalId ?? string.Empty,
                ["reason"] = evt.Reason ?? string.Empty,
            },
            lifecyclePhase: AuditLifecyclePhase.Accepted,
            terminalOutcome: AuditTerminalOutcome.Unspecified);
}

public sealed class ScriptCatalogRolledBackAuditTranslator
    : ScriptAuditTranslatorBase<ScriptCatalogRolledBackEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScriptCatalogRolledBackEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScriptCatalogRolledBackEvent evt) =>
        ScriptSeed(
            "script.catalog.rolled-back",
            "script_catalog",
            evt.ScriptId,
            evt.ScopeId,
            $"Catalog rolled back to `{evt.TargetRevision}` (from `{evt.PreviousRevision}`) for `{evt.ScriptId}`.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true,
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target_revision"] = evt.TargetRevision ?? string.Empty,
                ["previous_revision"] = evt.PreviousRevision ?? string.Empty,
                ["proposal_id"] = evt.ProposalId ?? string.Empty,
            });
}

public sealed class ScriptEvolutionSessionCompletedAuditTranslator
    : ScriptAuditTranslatorBase<ScriptEvolutionSessionCompletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScriptEvolutionSessionCompletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScriptEvolutionSessionCompletedEvent evt) =>
        ScriptSeed(
            "script.evolution.session.completed",
            "script_evolution_session",
            evt.ProposalId,
            evt.ScopeId,
            evt.Accepted
                ? $"Script evolution session `{evt.ProposalId}` accepted (status `{evt.Status}`)."
                : $"Script evolution session `{evt.ProposalId}` rejected (status `{evt.Status}`).",
            // Terminal AI-evolution decision is a governance fact.
            AuditSensitivityLevel.Restricted,
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["accepted"] = evt.Accepted ? "true" : "false",
                ["status"] = evt.Status ?? string.Empty,
                ["failure_reason"] = evt.FailureReason ?? string.Empty,
                ["definition_actor_id"] = evt.DefinitionActorId ?? string.Empty,
                ["catalog_actor_id"] = evt.CatalogActorId ?? string.Empty,
            });
}

public sealed class ScriptRunOutcomeRecordedAuditTranslator
    : ScriptAuditTranslatorBase<ScriptRunOutcomeRecordedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ScriptRunOutcomeRecordedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ScriptRunOutcomeRecordedEvent evt)
    {
        var succeeded = evt.Status == ScriptRunOutcomeStatus.Succeeded;
        var statusLabel = evt.Status switch
        {
            ScriptRunOutcomeStatus.Succeeded => "succeeded",
            ScriptRunOutcomeStatus.Failed => "failed",
            _ => "unspecified",
        };
        // Confidentiality: the free-text `error` and the `result` payload may embed
        // business output — only the terminal status, whether an error was present,
        // and stable governance identifiers/counts are recorded, never the bodies.
        return ScriptSeed(
            "script.run.outcome",
            "script_run",
            evt.ScriptRunId,
            evt.ScopeId,
            succeeded
                ? $"Script run `{evt.ScriptRunId}` succeeded ({evt.CommittedFactCount} committed facts)."
                : $"Script run `{evt.ScriptRunId}` {statusLabel}.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = statusLabel,
                ["has_error"] = string.IsNullOrWhiteSpace(evt.Error) ? "false" : "true",
                ["committed_fact_count"] = evt.CommittedFactCount.ToString(),
                ["script_id"] = evt.ScriptId ?? string.Empty,
                ["script_revision"] = evt.ScriptRevision ?? string.Empty,
                ["command_id"] = evt.CommandId ?? string.Empty,
                ["correlation_id"] = evt.CorrelationId ?? string.Empty,
            },
            terminalOutcome: succeeded
                ? AuditTerminalOutcome.Succeeded
                : AuditTerminalOutcome.Failed,
            failure: succeeded
                ? null
                : new AuditFailure
                {
                    Code = "script_run_failed",
                    Category = AuditFailureCategory.Execution,
                    Retryability = AuditRetryability.Unknown,
                    FailedPhase = AuditLifecyclePhase.Running,
                    SanitizedMessage = "Script run failed.",
                },
            runId: evt.ScriptRunId,
            omittedFields: ["script_run.result", "script_run.error"]);
    }
}

public abstract class ScriptAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
    where TEvent : class, IMessage<TEvent>, new()
{
    public abstract string EventTypeUrl { get; }

    public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(new TEvent().Descriptor))
            return [];

        var evt = eventPayload.Unpack<TEvent>();
        return [CommittedAuditRecordFactory.CreateSystemRecord(context, BuildSeed(context, evt))];
    }

    protected abstract CommittedAuditSeed BuildSeed(CommittedAuditTranslationContext context, TEvent evt);

    protected static CommittedAuditSeed ScriptSeed(
        string operationName,
        string targetKind,
        string targetId,
        string scopeId,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        bool isDestructive = false,
        IReadOnlyDictionary<string, string>? annotations = null,
        AuditLifecyclePhase lifecyclePhase = AuditLifecyclePhase.Terminal,
        AuditTerminalOutcome terminalOutcome = AuditTerminalOutcome.Succeeded,
        AuditFailure? failure = null,
        string runId = "",
        IReadOnlyList<string>? omittedFields = null) =>
        new(
            operationName,
            targetKind,
            targetId ?? string.Empty,
            scopeId ?? string.Empty,
            sensitivityLevel,
            isDestructive,
            ResultSummary: resultSummary,
            Annotations: annotations,
            LifecyclePhase: lifecyclePhase,
            TerminalOutcome: terminalOutcome,
            Failure: failure,
            RunId: runId,
            OmittedFields: omittedFields);
}
