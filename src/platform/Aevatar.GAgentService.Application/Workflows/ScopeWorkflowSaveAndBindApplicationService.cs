using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowSaveAndBindApplicationService : IScopeWorkflowSaveAndBindPort
{
    private readonly IScopeWorkflowCommandPort _workflowCommandPort;
    private readonly IScopeBindingCommandPort _bindingCommandPort;

    public ScopeWorkflowSaveAndBindApplicationService(
        IScopeWorkflowCommandPort workflowCommandPort,
        IScopeBindingCommandPort bindingCommandPort)
    {
        _workflowCommandPort = workflowCommandPort ?? throw new ArgumentNullException(nameof(workflowCommandPort));
        _bindingCommandPort = bindingCommandPort ?? throw new ArgumentNullException(nameof(bindingCommandPort));
    }

    public async Task<ScopeWorkflowSaveAndBindResult> SaveAndBindAsync(
        ScopeWorkflowSaveAndBindRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var normalizedWorkflowId = ResolveWorkflowId(request.WorkflowId);
        var revisionId = ScopeWorkflowCapabilityConventions.ResolveRevisionId(null);
        var workflowYaml = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(request.InlineWorkflowYamls);

        var workflowResult = await _workflowCommandPort.UpsertAsync(
            new ScopeWorkflowUpsertRequest(
                normalizedScopeId,
                normalizedWorkflowId,
                workflowYaml,
                request.WorkflowName,
                request.DisplayName,
                inlineWorkflowYamls,
                revisionId),
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
                ServiceId: ResolveBindingServiceId(request.ServiceId),
                ExposureDesired: request.ExposureDesired),
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

    private static string? ResolveBindingServiceId(string? serviceId)
    {
        var normalized = ScopeWorkflowCapabilityConventions.NormalizeOptional(serviceId);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
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
