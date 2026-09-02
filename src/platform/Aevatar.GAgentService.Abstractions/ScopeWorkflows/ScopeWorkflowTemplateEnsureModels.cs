using System.Text.Json.Serialization;

namespace Aevatar.GAgentService.Abstractions;

public sealed record ScopeWorkflowTemplateEnsureRequest(
    string ScopeId,
    string WorkflowId)
{
    [JsonIgnore]
    public WorkflowCapabilityAdmissionContext? CapabilityAdmission { get; init; }
}

public enum ScopeWorkflowTemplateEnsureStatus
{
    NotConfigured = 0,
    AlreadyCurrent = 1,
    SaveAndBindAccepted = 2,
    Failed = 3,
}

public sealed record ScopeWorkflowTemplateEnsureResult(
    ScopeWorkflowTemplateEnsureStatus Status,
    string ScopeId,
    string WorkflowId,
    string RevisionId,
    string Reason,
    ScopeWorkflowSaveAndBindResult? SaveAndBind = null)
{
    public bool IsConfigured => Status != ScopeWorkflowTemplateEnsureStatus.NotConfigured;

    public bool Succeeded => Status is ScopeWorkflowTemplateEnsureStatus.NotConfigured
        or ScopeWorkflowTemplateEnsureStatus.AlreadyCurrent
        or ScopeWorkflowTemplateEnsureStatus.SaveAndBindAccepted;

    public static ScopeWorkflowTemplateEnsureResult NotConfigured(
        string scopeId,
        string workflowId) =>
        new(
            ScopeWorkflowTemplateEnsureStatus.NotConfigured,
            scopeId,
            workflowId,
            string.Empty,
            "workflow_template_not_configured");

    public static ScopeWorkflowTemplateEnsureResult AlreadyCurrent(
        ScopeWorkflowSummary workflow,
        string revisionId) =>
        new(
            ScopeWorkflowTemplateEnsureStatus.AlreadyCurrent,
            workflow.ScopeId,
            workflow.WorkflowId,
            revisionId,
            "workflow_template_current");

    public static ScopeWorkflowTemplateEnsureResult SaveAndBindAccepted(
        ScopeWorkflowSaveAndBindResult result,
        string reason) =>
        new(
            ScopeWorkflowTemplateEnsureStatus.SaveAndBindAccepted,
            result.ScopeId,
            result.WorkflowId,
            result.RevisionId,
            reason,
            result);

    public static ScopeWorkflowTemplateEnsureResult Failed(
        string scopeId,
        string workflowId,
        string revisionId,
        string reason,
        ScopeWorkflowSaveAndBindResult? saveAndBind = null) =>
        new(
            ScopeWorkflowTemplateEnsureStatus.Failed,
            scopeId,
            workflowId,
            revisionId,
            reason,
            saveAndBind);
}
