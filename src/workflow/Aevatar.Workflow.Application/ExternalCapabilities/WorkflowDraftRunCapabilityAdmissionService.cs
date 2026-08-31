using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed class WorkflowDraftRunCapabilityAdmissionService(
    IWorkflowExplicitRequestPreviewService previewService,
    IWorkflowExternalCapabilityAdmissionService admissionService) :
    IWorkflowDraftRunCapabilityAdmissionService
{
    private const string SourceKind = "workflow_draft_run";
    private const string NyxIdAgentKeyPrefix = "nyxid_ag_";

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
        var access = BuildAccess(
            scopeId,
            request.CallerCredential,
            request.CallerNyxIdCredentialSelection);

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
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? callerCredential,
        NyxIdCallerCredentialSelection? callerNyxIdCredentialSelection) =>
        new(
            scopeId,
            callerCredential?.NyxIdAuthority?.ExternalUserId?.Trim() ?? string.Empty,
            BindCredentialSelection(callerCredential, callerNyxIdCredentialSelection));

    private static NyxIdCallerCredentialSelection? BindCredentialSelection(
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? callerCredential,
        NyxIdCallerCredentialSelection? selection)
    {
        if (selection == null)
            return BuildReadOnlyCredentialSelection(callerCredential);

        var selectedSourceToken = selection.SourceReadableUserBearerToken;
        if (!string.IsNullOrWhiteSpace(selectedSourceToken))
        {
            var boundSourceToken = !string.IsNullOrWhiteSpace(
                callerCredential?.SourceReadableUserBearerToken)
                ? callerCredential.SourceReadableUserBearerToken.Trim()
                : callerCredential?.Kind == NyxIdCallerCredentialKind.SourceReadableUserBearer
                    ? callerCredential.BearerToken?.Trim()
                    : null;
            return string.Equals(selectedSourceToken, boundSourceToken, StringComparison.Ordinal)
                ? selection
                : BuildReadOnlyCredentialSelection(callerCredential);
        }

        var selectedProxyToken = selection.ProxyDelegationToken;
        if (!string.IsNullOrWhiteSpace(selectedProxyToken) &&
            (IsMatchingProxyExecutionCredential(callerCredential, selectedProxyToken) ||
             IsAgentKeyExecutionCredential(callerCredential)))
        {
            return selection;
        }

        return BuildReadOnlyCredentialSelection(callerCredential);
    }

    private static NyxIdCallerCredentialSelection? BuildReadOnlyCredentialSelection(
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? callerCredential)
    {
        var sourceReadableBearerToken = callerCredential?.SourceReadableUserBearerToken?.Trim();
        if (!string.IsNullOrWhiteSpace(sourceReadableBearerToken))
        {
            return NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                sourceReadableBearerToken);
        }

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

    private static bool IsMatchingProxyExecutionCredential(
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? callerCredential,
        string selectedProxyToken) =>
        callerCredential?.Kind == NyxIdCallerCredentialKind.ProxyDelegation &&
        string.Equals(
            selectedProxyToken,
            callerCredential.BearerToken?.Trim(),
            StringComparison.Ordinal);

    private static bool IsAgentKeyExecutionCredential(
        Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? callerCredential)
    {
        if (callerCredential?.Kind != NyxIdCallerCredentialKind.AgentKey ||
            WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                callerCredential.BearerToken,
                callerCredential.Kind,
                callerCredential.SourceReadableUserBearerToken))
        {
            return false;
        }

        var parsed = WorkflowCallerCredentialTokens.ParseOptional(callerCredential.BearerToken);
        return parsed.IsValid &&
               parsed.NormalizedBearerToken?.StartsWith(
                   NyxIdAgentKeyPrefix,
                   StringComparison.Ordinal) == true;
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
