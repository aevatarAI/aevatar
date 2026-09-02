using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowSaveAndBindApplicationService : IScopeWorkflowSaveAndBindPort
{
    private readonly IScopeWorkflowCommandPort _workflowCommandPort;
    private readonly IScopeBindingCommandPort _bindingCommandPort;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;

    public ScopeWorkflowSaveAndBindApplicationService(
        IScopeWorkflowCommandPort workflowCommandPort,
        IScopeBindingCommandPort bindingCommandPort,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService)
    {
        _workflowCommandPort = workflowCommandPort ?? throw new ArgumentNullException(nameof(workflowCommandPort));
        _bindingCommandPort = bindingCommandPort ?? throw new ArgumentNullException(nameof(bindingCommandPort));
        _capabilityAdmissionService = capabilityAdmissionService
            ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
    }

    public async Task<ScopeWorkflowSaveAndBindResult> SaveAndBindAsync(
        ScopeWorkflowSaveAndBindRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var normalizedWorkflowId = ResolveWorkflowId(request.WorkflowId);
        var revisionId = ScopeWorkflowCapabilityConventions.ResolveRevisionId(request.RevisionId);
        var workflowYaml = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(request.InlineWorkflowYamls);
        var admissionContext = request.CapabilityAdmission;
        var executionMode = admissionContext?.ExecutionMode ?? ExternalCapabilityExecutionMode.Interactive;
        var explicitRequestConfirmations = admissionContext?.ExplicitRequestConfirmations;
        var capabilityAccess = new ExternalWorkflowCapabilityAccessContext(
            normalizedScopeId,
            admissionContext?.CallerId ?? string.Empty,
            admissionContext?.NyxIdCallerCredential,
            admissionContext?.NyxIdOrganizationBearerToken);
        var capabilityAdmissionPlan = admissionContext?.ExistingPlan is { } existingPlan
            ? admissionContext.NyxIdCallerCredential is not null
                ? await _capabilityAdmissionService.RefreshPersistedAsync(
                    new RefreshPersistedWorkflowCapabilityAdmissionRequest(
                        new PersistedWorkflowCapabilityAdmissionRequest(
                            existingPlan,
                            workflowYaml,
                            inlineWorkflowYamls,
                            "scope_workflow_save_and_bind",
                            executionMode,
                            normalizedWorkflowId,
                            revisionId),
                        capabilityAccess,
                        explicitRequestConfirmations),
                    ct)
                : await _capabilityAdmissionService.RevalidatePersistedAsync(
                    new PersistedWorkflowCapabilityAdmissionRequest(
                        existingPlan,
                        workflowYaml,
                        inlineWorkflowYamls,
                        "scope_workflow_save_and_bind",
                        executionMode,
                        normalizedWorkflowId,
                        revisionId),
                    ct)
            : await _capabilityAdmissionService.AdmitAsync(
                new WorkflowExternalCapabilityAdmissionRequest(
                    capabilityAccess,
                    workflowYaml,
                inlineWorkflowYamls,
                "scope_workflow_save_and_bind",
                executionMode,
                explicitRequestConfirmations,
                normalizedWorkflowId,
                revisionId),
                ct);
        var trustedAdmissionContext = new WorkflowCapabilityAdmissionContext(
            admissionContext?.CallerId ?? string.Empty,
            admissionContext?.NyxIdCallerCredential,
            admissionContext?.NyxIdOrganizationBearerToken,
            executionMode,
            capabilityAdmissionPlan,
            explicitRequestConfirmations);

        var workflowResult = await _workflowCommandPort.UpsertAsync(
            new ScopeWorkflowUpsertRequest(
                normalizedScopeId,
                normalizedWorkflowId,
                workflowYaml,
                request.WorkflowName,
                request.DisplayName,
                inlineWorkflowYamls,
                revisionId)
            {
                CapabilityAdmission = trustedAdmissionContext,
            },
            ct);

        var workflowYamls = BuildWorkflowYamls(workflowYaml, inlineWorkflowYamls);
        var bindingResult = await _bindingCommandPort.UpsertAsync(
            new ScopeBindingUpsertRequest(
                normalizedScopeId,
                ScopeBindingImplementationKind.Workflow,
                new ScopeBindingWorkflowSpec(normalizedWorkflowId, workflowYamls),
                DisplayName: request.DisplayName,
                RevisionId: revisionId,
                AppId: request.AppId,
                ServiceId: ResolveBindingServiceId(request.ServiceId, normalizedWorkflowId),
                ExposureDesired: request.ExposureDesired,
                AllowExistingRevisionReplay: true,
                ReplayRevisionId: revisionId)
            {
                CapabilityAdmission = trustedAdmissionContext,
                AcceptedRevisionCreation = new ScopeBindingAcceptedRevisionCreation(
                    workflowResult.ServiceKey,
                    workflowResult.RevisionId),
            },
            ct);

        if (!string.Equals(workflowResult.RevisionId, revisionId, StringComparison.Ordinal) ||
            !string.Equals(bindingResult.RevisionId, revisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Save-and-bind revision identity must match workflow and binding results.");
        }

        return new ScopeWorkflowSaveAndBindResult(
            normalizedScopeId,
            normalizedWorkflowId,
            revisionId,
            workflowResult,
            bindingResult);
    }

    private static string ResolveWorkflowId(string? workflowId)
    {
        var normalized = ScopeWorkflowCapabilityConventions.NormalizeOptional(workflowId);
        return string.IsNullOrWhiteSpace(normalized)
            ? $"wf-{Guid.NewGuid():N}"
            : ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(normalized);
    }

    private static string ResolveBindingServiceId(string? serviceId, string workflowId)
    {
        var normalized = ScopeWorkflowCapabilityConventions.NormalizeOptional(serviceId);
        return string.IsNullOrWhiteSpace(normalized)
            ? ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(workflowId)
            : ScopeWorkflowCapabilityOptions.NormalizeRequired(normalized, nameof(serviceId));
    }

    private static IReadOnlyDictionary<string, string>? NormalizeInlineWorkflowYamls(
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls)
    {
        if (inlineWorkflowYamls == null || inlineWorkflowYamls.Count == 0)
            return null;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in inlineWorkflowYamls)
        {
            var normalizedKey = ScopeWorkflowCapabilityConventions.NormalizeOptional(key);
            var normalizedValue = ScopeWorkflowCapabilityConventions.NormalizeOptional(value);
            if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(normalizedValue))
                continue;

            normalized[normalizedKey] = normalizedValue;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static IReadOnlyList<string> BuildWorkflowYamls(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls)
    {
        if (inlineWorkflowYamls == null || inlineWorkflowYamls.Count == 0)
            return [workflowYaml];

        var workflowYamls = new List<string>(inlineWorkflowYamls.Count + 1) { workflowYaml };
        workflowYamls.AddRange(inlineWorkflowYamls.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value));
        return workflowYamls;
    }
}
