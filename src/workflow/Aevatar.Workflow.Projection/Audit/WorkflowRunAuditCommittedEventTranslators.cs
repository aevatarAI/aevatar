using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Audit;

// Committed-fact audit translators for the workflow run/definition governance trail.
//
// Ownership / flow:
//   * WorkflowRunGAgent (run-scoped, keyed by run id) persists the run lifecycle
//     events (started / completed / stopped / stopped-run / fork-requested) and the
//     run-side definition bind. Every WorkflowRunGAgent commit activates the
//     WorkflowExecutionMaterializationContext scope, so those events flow through
//     that context's audit materializer (verified against
//     WorkflowCommittedStateProjectionActivationPlanProvider — the plan always
//     yields the ExecutionMaterialization lease for WorkflowRunGAgent).
//   * WorkflowGAgent (definition-scoped, keyed by workflow name / scope) persists
//     the definition bind, which activates ONLY the WorkflowBindingProjectionContext
//     scope; its audit is captured there.
//
// Dedup: BindWorkflowRunDefinitionEvent flows through BOTH the Binding and the
// ExecutionMaterialization scope. The committed audit record id is deterministic
// (committed:{eventId}:{operationName}) and identical across both scopes, so the
// second append is an idempotent Duplicate — no double-record. It is deliberately
// audited under the ExecutionMaterialization context (its owning actor's run scope).
//
// Subject-bearing: run ids, workflow names and scope ids are opaque internal
// identifiers, never a raw external subject, so the plain base is used (no
// origin-actor-id hashing).
//
// Confidentiality: no input, output, error body, workflow yaml or execution
// context (llm tokens / caller credentials) is ever recorded — only stable
// governance identifiers plus the terminal status / safe error-presence flag.

public sealed class WorkflowRunExecutionStartedAuditTranslator
    : WorkflowAuditTranslatorBase<WorkflowRunExecutionStartedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(WorkflowRunExecutionStartedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        WorkflowRunExecutionStartedEvent evt)
    {
        var runId = string.IsNullOrWhiteSpace(evt.RunId) ? context.OriginActorId : evt.RunId;
        var scopeId = WorkflowAuditScopeResolver.Resolve(context, evt.ScopeId);
        return WorkflowSeed(
            "workflow.run.started",
            "workflow_run",
            runId,
            scopeId,
            $"Workflow run {runId} execution started.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_name"] = evt.WorkflowName ?? string.Empty,
                ["definition_actor_id"] = evt.DefinitionActorId ?? string.Empty,
                ["attempt"] = evt.Attempt.ToString(),
            },
            lifecyclePhase: AuditLifecyclePhase.Running,
            terminalOutcome: AuditTerminalOutcome.Unspecified,
            runId: runId,
            omittedFields: ["workflow.input"]);
    }
}

public sealed class WorkflowCompletedAuditTranslator
    : WorkflowAuditTranslatorBase<WorkflowCompletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(WorkflowCompletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        WorkflowCompletedEvent evt)
    {
        var runId = string.IsNullOrWhiteSpace(evt.RunId) ? context.OriginActorId : evt.RunId;
        var scopeId = WorkflowAuditScopeResolver.Resolve(context);
        var outcome = evt.Success ? "succeeded" : "failed";
        // `output` and `error` are free-text and may embed business payload — record
        // only the terminal outcome and whether an error was present, never the body.
        var errorPresent = !evt.Success && !string.IsNullOrWhiteSpace(evt.Error);
        return WorkflowSeed(
            "workflow.run.completed",
            "workflow_run",
            runId,
            scopeId,
            $"Workflow run {runId} completed ({outcome}).",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_name"] = evt.WorkflowName ?? string.Empty,
                ["outcome"] = outcome,
                ["error_present"] = errorPresent ? "true" : "false",
            },
            terminalOutcome: evt.Success
                ? AuditTerminalOutcome.Succeeded
                : AuditTerminalOutcome.Failed,
            failure: evt.Success
                ? null
                : new AuditFailure
                {
                    Code = "workflow_failed",
                    Category = AuditFailureCategory.Execution,
                    Retryability = AuditRetryability.Unknown,
                    FailedPhase = AuditLifecyclePhase.Running,
                    SanitizedMessage = "Workflow execution failed.",
                },
            runId: runId,
            omittedFields: ["workflow.output", "workflow.error"]);
    }
}

public sealed class WorkflowStoppedAuditTranslator
    : WorkflowAuditTranslatorBase<WorkflowStoppedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(WorkflowStoppedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        WorkflowStoppedEvent evt)
    {
        var runId = string.IsNullOrWhiteSpace(evt.RunId) ? context.OriginActorId : evt.RunId;
        var scopeId = WorkflowAuditScopeResolver.Resolve(context);
        return WorkflowSeed(
            "workflow.run.stopped",
            "workflow_run",
            runId,
            scopeId,
            $"Workflow run {runId} stopped.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_name"] = evt.WorkflowName ?? string.Empty,
                ["reason"] = evt.Reason ?? string.Empty,
            },
            terminalOutcome: AuditTerminalOutcome.Cancelled,
            runId: runId);
    }
}

public sealed class WorkflowRunStoppedAuditTranslator
    : WorkflowAuditTranslatorBase<WorkflowRunStoppedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(WorkflowRunStoppedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        WorkflowRunStoppedEvent evt)
    {
        var runId = string.IsNullOrWhiteSpace(evt.RunId) ? context.OriginActorId : evt.RunId;
        var scopeId = WorkflowAuditScopeResolver.Resolve(context);
        return WorkflowSeed(
            "workflow.run.stopped-run",
            "workflow_run",
            runId,
            scopeId,
            $"Workflow run {runId} stopped.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = evt.Reason ?? string.Empty,
            },
            terminalOutcome: AuditTerminalOutcome.Cancelled,
            runId: runId);
    }
}

public sealed class WorkflowRunForkRequestedAuditTranslator
    : WorkflowAuditTranslatorBase<WorkflowRunForkRequestedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(WorkflowRunForkRequestedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        WorkflowRunForkRequestedEvent evt)
    {
        var sourceRunId = string.IsNullOrWhiteSpace(evt.SourceRunId) ? context.OriginActorId : evt.SourceRunId;
        var scopeId = WorkflowAuditScopeResolver.Resolve(context, evt.ScopeId);
        return WorkflowSeed(
            "workflow.run.fork-requested",
            "workflow_run",
            sourceRunId,
            scopeId,
            $"Workflow run {sourceRunId} requested a fork (attempt {evt.Attempt}).",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["start_at_step_id"] = evt.StartAtStepId ?? string.Empty,
                ["attempt"] = evt.Attempt.ToString(),
            },
            lifecyclePhase: AuditLifecyclePhase.Accepted,
            terminalOutcome: AuditTerminalOutcome.Unspecified,
            runId: sourceRunId);
    }
}

public sealed class BindWorkflowRunDefinitionAuditTranslator
    : WorkflowAuditTranslatorBase<BindWorkflowRunDefinitionEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(BindWorkflowRunDefinitionEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        BindWorkflowRunDefinitionEvent evt)
    {
        var runId = string.IsNullOrWhiteSpace(evt.RunId) ? context.OriginActorId : evt.RunId;
        var scopeId = WorkflowAuditScopeResolver.Resolve(context, evt.ScopeId);
        // workflow_yaml / inline_workflow_yamls are the definition body — never recorded.
        return WorkflowSeed(
            "workflow.run.definition-bound",
            "workflow_run",
            runId,
            scopeId,
            $"Workflow run {runId} bound to definition `{evt.WorkflowName}`.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_name"] = evt.WorkflowName ?? string.Empty,
                ["definition_actor_id"] = evt.DefinitionActorId ?? string.Empty,
                ["run_origin"] = evt.RunOrigin ?? string.Empty,
                ["schedule_id"] = evt.ScheduleId ?? string.Empty,
            });
    }
}

public sealed class BindWorkflowDefinitionAuditTranslator
    : WorkflowAuditTranslatorBase<BindWorkflowDefinitionEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(BindWorkflowDefinitionEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        BindWorkflowDefinitionEvent evt)
    {
        var workflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? context.OriginActorId : evt.WorkflowName;
        // workflow_yaml / inline_workflow_yamls are the definition body — never recorded.
        return WorkflowSeed(
            "workflow.definition.bound",
            "workflow_definition",
            workflowName,
            evt.ScopeId,
            $"Workflow definition `{workflowName}` bound.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow_name"] = evt.WorkflowName ?? string.Empty,
                ["source_kind"] = evt.SourceKind ?? string.Empty,
            });
    }
}

/// <summary>
/// Local plain (non subject-bearing) base for workflow committed-fact audit
/// translators. Run/definition/scope ids are opaque internal identifiers, so the
/// origin actor id is stamped as-is (no HMAC hashing).
/// </summary>
public abstract class WorkflowAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
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

    protected static CommittedAuditSeed WorkflowSeed(
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
