using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed class WorkflowDraftRunCapabilityAdmissionService(
    IWorkflowExplicitRequestPreviewService previewService,
    IWorkflowExternalCapabilityAdmissionService admissionService) :
    IWorkflowDraftRunCapabilityAdmissionService
{
    private const string SourceKind = "workflow_draft_run";

    public async Task<WorkflowDraftRunCapabilityAdmissionResult> PrepareAsync(
        WorkflowDraftRunCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var commandId = NormalizeRequired(request.CommandId, nameof(request.CommandId));
        var workflowYaml = RequireDefinition(request.WorkflowYaml, nameof(request.WorkflowYaml));
        var inlineWorkflowYamls = CloneInlineWorkflowYamls(request.InlineWorkflowYamls);
        var workflowId = BuildIdentity(
            "workflow-draft-run-",
            "workflow-draft-run.workflow.v1",
            scopeId,
            commandId);
        var revisionId = BuildIdentity(
            "rev-draft-run-",
            "workflow-draft-run.revision.v1",
            workflowId,
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
                workflowYaml,
                inlineWorkflowYamls));
        var access = BuildAccess(scopeId, request.CallerCredential);

        var preview = await previewService.PreviewAsync(
            new WorkflowExplicitRequestPreviewRequest(
                access,
                workflowYaml,
                inlineWorkflowYamls,
                ExternalCapabilityExecutionMode.Interactive,
                workflowId,
                revisionId),
            cancellationToken);
        var confirmations = BuildConfirmations(preview, workflowId, revisionId);
        var plan = await admissionService.AdmitAsync(
            new WorkflowExternalCapabilityAdmissionRequest(
                access,
                workflowYaml,
                inlineWorkflowYamls,
                SourceKind,
                ExternalCapabilityExecutionMode.Interactive,
                confirmations,
                workflowId,
                revisionId),
            cancellationToken);

        return new WorkflowDraftRunCapabilityAdmissionResult(
            SourceKind,
            workflowId,
            revisionId,
            plan);
    }

    private static ExternalWorkflowCapabilityAccessContext BuildAccess(
        string scopeId,
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? callerCredential) =>
        new(
            scopeId,
            callerCredential?.NyxIdAuthority?.ExternalUserId?.Trim() ?? string.Empty,
            BuildCredentialSelection(callerCredential));

    private static NyxIdCallerCredentialSelection? BuildCredentialSelection(
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? callerCredential)
    {
        var bearerToken = callerCredential?.BearerToken?.Trim();
        if (string.IsNullOrWhiteSpace(bearerToken))
            return null;

        return callerCredential!.Kind switch
        {
            NyxIdCallerCredentialKind.SourceReadableUserBearer =>
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(bearerToken),
            NyxIdCallerCredentialKind.ProxyDelegation =>
                NyxIdCallerCredentialSelection.ProxyDelegation(bearerToken),
            _ => null,
        };
    }

    private static IReadOnlyList<NyxIdExplicitRequestConfirmation> BuildConfirmations(
        WorkflowExplicitRequestPreviewResult preview,
        string workflowId,
        string revisionId)
    {
        if (!string.Equals(preview.WorkflowId, workflowId, StringComparison.Ordinal) ||
            !string.Equals(preview.RevisionId, revisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Draft Run explicit request preview identity does not match the prepared workflow revision.");
        }

        var confirmations = new List<NyxIdExplicitRequestConfirmation>(preview.Items.Count);
        string? previousCallSiteId = null;
        foreach (var item in preview.Items.OrderBy(static item => item.CallSiteId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(item.CallSiteId) ||
                string.IsNullOrWhiteSpace(item.RequestContractDigest) ||
                item.EffectiveRisk is not (NyxIdOperationRisk.ReadOnly or NyxIdOperationRisk.Write or
                    NyxIdOperationRisk.Destructive) ||
                !item.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Interactive) ||
                string.Equals(previousCallSiteId, item.CallSiteId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Draft Run explicit request preview is not valid for interactive admission.");
            }

            previousCallSiteId = item.CallSiteId;
            confirmations.Add(new NyxIdExplicitRequestConfirmation
            {
                CallSiteId = item.CallSiteId,
                RequestContractDigest = item.RequestContractDigest,
                AttestedRisk = item.EffectiveRisk,
                WorkflowId = workflowId,
                RevisionId = revisionId,
            });
        }

        return confirmations;
    }

    private static IReadOnlyDictionary<string, string> CloneInlineWorkflowYamls(
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls) =>
        (inlineWorkflowYamls ?? new Dictionary<string, string>())
        .ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.OrdinalIgnoreCase);

    private static string BuildIdentity(
        string prefix,
        params string?[] digestComponents) =>
        prefix + ExternalWorkflowCapabilityContractDigest.Compute(digestComponents);

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new InvalidOperationException($"{parameterName} is required.")
            : normalized;
    }

    private static string RequireDefinition(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{parameterName} is required.")
            : value;
}
