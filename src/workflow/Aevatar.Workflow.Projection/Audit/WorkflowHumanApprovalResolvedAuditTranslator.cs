using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Audit;

/// <summary>
/// Translates the committed <see cref="WorkflowHumanApprovalResolvedEvent"/> into a
/// governance audit record. The owning actor is the run-scoped WorkflowRunGAgent
/// (its id addresses a run, not a raw external subject) so the plain, non
/// subject-bearing shape applies. The event is a single-write terminal fact
/// published to the run actor itself, so the type-url routes to exactly one
/// committed fact per resolution (no manager/index mirror dedup concern).
///
/// Only the governance-relevant decision surface is recorded: whether the
/// approval was granted, and whether a human or the timeout default resolved it.
/// The user input, edited content, resolved content and feedback carried by the
/// event are deliberately NOT recorded — they may embed the approval payload.
/// </summary>
public sealed class WorkflowHumanApprovalResolvedAuditTranslator : IAuditCommittedEventTranslator
{
    public string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(WorkflowHumanApprovalResolvedEvent.Descriptor);

    public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(WorkflowHumanApprovalResolvedEvent.Descriptor))
            return [];

        var evt = eventPayload.Unpack<WorkflowHumanApprovalResolvedEvent>();
        return [CommittedAuditRecordFactory.CreateSystemRecord(context, BuildSeed(context, evt))];
    }

    private static CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        WorkflowHumanApprovalResolvedEvent evt)
    {
        var runId = string.IsNullOrWhiteSpace(evt.RunId) ? context.OriginActorId : evt.RunId;
        var resolutionSource = ResolutionSourceLabel(evt.ResolutionSource);
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["approved"] = evt.Approved ? "true" : "false",
            ["resolution_source"] = resolutionSource,
        };
        if (!string.IsNullOrWhiteSpace(evt.StepId))
            annotations["step_id"] = evt.StepId;

        return new CommittedAuditSeed(
            "workflow.human-approval.resolved",
            "workflow_run",
            runId,
            ScopeId: WorkflowAuditScopeResolver.Resolve(context),
            SensitivityLevel: AuditSensitivityLevel.Restricted,
            ResultSummary: evt.Approved
                ? $"Human approval granted for run {runId} ({resolutionSource})."
                : $"Human approval denied for run {runId} ({resolutionSource}).",
            Annotations: annotations,
            RunId: runId,
            OmittedFields: [
                "approval.user_input",
                "approval.edited_content",
                "approval.feedback",
                "approval.resolved_content",
            ]);
    }

    private static string ResolutionSourceLabel(WorkflowHumanApprovalResolutionSource source) =>
        source switch
        {
            WorkflowHumanApprovalResolutionSource.User => "user",
            WorkflowHumanApprovalResolutionSource.Timeout => "timeout",
            _ => "unspecified",
        };
}
