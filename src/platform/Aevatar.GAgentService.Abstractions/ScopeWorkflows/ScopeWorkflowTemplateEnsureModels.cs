using System.Text.Json.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;

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

public sealed record ScopeWorkflowDefinitionBindingResolveRequest(
    string ScopeId,
    string WorkflowId);

public enum ScopeWorkflowDefinitionBindingResolveStatus
{
    NotRunnable = 0,
    Resolved = 1,
}

public sealed record ScopeWorkflowDefinitionBindingResolveResult(
    ScopeWorkflowDefinitionBindingResolveStatus Status,
    string ScopeId,
    string WorkflowId,
    string Reason,
    WorkflowDefinitionBinding? DefinitionBinding = null)
{
    public bool Succeeded => Status == ScopeWorkflowDefinitionBindingResolveStatus.Resolved && DefinitionBinding is not null;

    public static ScopeWorkflowDefinitionBindingResolveResult NotRunnable(
        string scopeId,
        string workflowId,
        string reason) =>
        new(
            ScopeWorkflowDefinitionBindingResolveStatus.NotRunnable,
            scopeId,
            workflowId,
            string.IsNullOrWhiteSpace(reason) ? "workflow_not_runnable" : reason);

    public static ScopeWorkflowDefinitionBindingResolveResult Resolved(
        string scopeId,
        string workflowId,
        WorkflowDefinitionBinding definitionBinding) =>
        new(
            ScopeWorkflowDefinitionBindingResolveStatus.Resolved,
            scopeId,
            workflowId,
            "resolved",
            definitionBinding);
}

public interface IScopeWorkflowDefinitionBindingResolvePort
{
    Task<ScopeWorkflowDefinitionBindingResolveResult> ResolveAsync(
        ScopeWorkflowDefinitionBindingResolveRequest request,
        CancellationToken ct = default);
}
