using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowTemplateEnsureService : IScopeWorkflowTemplateEnsurePort
{
    private readonly IScopeWorkflowQueryPort _workflowQueryPort;
    private readonly IScopeWorkflowSaveAndBindPort _saveAndBindPort;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeWorkflowTemplateEnsureService(
        IScopeWorkflowQueryPort workflowQueryPort,
        IScopeWorkflowSaveAndBindPort saveAndBindPort,
        IOptions<ScopeWorkflowCapabilityOptions> options)
    {
        _workflowQueryPort = workflowQueryPort ?? throw new ArgumentNullException(nameof(workflowQueryPort));
        _saveAndBindPort = saveAndBindPort ?? throw new ArgumentNullException(nameof(saveAndBindPort));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ScopeWorkflowTemplateEnsureResult> EnsureAsync(
        ScopeWorkflowTemplateEnsureRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var workflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(request.WorkflowId);
        var template = ResolveTemplate(workflowId);
        if (template is null)
            return ScopeWorkflowTemplateEnsureResult.NotConfigured(scopeId, workflowId);

        var revisionId = ScopeWorkflowCapabilityConventions.ResolveRevisionId(template.RevisionId);
        var lookup = await _workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct).ConfigureAwait(false);
        if (lookup.IsRunnable &&
            string.Equals(lookup.Workflow!.ActiveRevisionId, revisionId, StringComparison.Ordinal))
        {
            return ScopeWorkflowTemplateEnsureResult.AlreadyCurrent(lookup.Workflow, revisionId);
        }

        var workflowYaml = ResolveWorkflowYaml(template);
        var result = await _saveAndBindPort.SaveAndBindAsync(
            new ScopeWorkflowSaveAndBindRequest(
                scopeId,
                workflowId,
                workflowYaml,
                NormalizeOptional(template.WorkflowName),
                NormalizeOptional(template.DisplayName),
                AppId: NormalizeOptional(template.AppId),
                ServiceId: NormalizeOptional(template.ServiceId),
                ExposureDesired: template.ExposureDesired,
                RevisionId: revisionId)
            {
                CapabilityAdmission = request.CapabilityAdmission,
            },
            ct).ConfigureAwait(false);

        var observed = await WaitForRunnableRevisionAsync(scopeId, workflowId, revisionId, ct).ConfigureAwait(false);
        return observed is null
            ? ScopeWorkflowTemplateEnsureResult.Failed(
                scopeId,
                workflowId,
                revisionId,
                "workflow_template_readmodel_not_observed",
                result)
            : ScopeWorkflowTemplateEnsureResult.SaveAndBindAccepted(
                result,
                lookup.Status == ScopeWorkflowLookupStatus.NotFound
                    ? "workflow_template_missing"
                    : "workflow_template_stale");
    }

    private async Task<ScopeWorkflowSummary?> WaitForRunnableRevisionAsync(
        string scopeId,
        string workflowId,
        string revisionId,
        CancellationToken ct)
    {
        var timeout = _options.TemplateEnsureProjectionWaitTimeout;
        var interval = _options.TemplateEnsureProjectionPollInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(250)
            : _options.TemplateEnsureProjectionPollInterval;
        var deadline = TimeProvider.System.GetUtcNow() + timeout;

        while (true)
        {
            var lookup = await _workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct).ConfigureAwait(false);
            if (lookup.IsRunnable &&
                string.Equals(lookup.Workflow!.ActiveRevisionId, revisionId, StringComparison.Ordinal))
            {
                return lookup.Workflow;
            }

            if (timeout <= TimeSpan.Zero || TimeProvider.System.GetUtcNow() >= deadline)
                return null;

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private ScopeWorkflowConfiguredTemplateOptions? ResolveTemplate(string workflowId)
    {
        foreach (var template in _options.ConfiguredTemplates)
        {
            if (!template.Enabled)
                continue;

            var configuredWorkflowId = ScopeWorkflowCapabilityConventions.NormalizeOptional(template.WorkflowId);
            if (string.Equals(configuredWorkflowId, workflowId, StringComparison.Ordinal))
                return template;
        }

        return null;
    }

    private static string ResolveWorkflowYaml(ScopeWorkflowConfiguredTemplateOptions template)
    {
        var workflowYaml = ScopeWorkflowCapabilityConventions.NormalizeOptional(template.WorkflowYaml);
        if (!string.IsNullOrWhiteSpace(workflowYaml))
            return workflowYaml;

        var workflowYamlPath = ScopeWorkflowCapabilityOptions.NormalizeRequired(
            template.WorkflowYamlPath,
            nameof(template.WorkflowYamlPath));
        var resolvedWorkflowYamlPath = ResolveWorkflowYamlPath(workflowYamlPath);
        return ScopeWorkflowCapabilityOptions.NormalizeRequired(
            File.ReadAllText(resolvedWorkflowYamlPath),
            nameof(template.WorkflowYaml));
    }

    private static string ResolveWorkflowYamlPath(string workflowYamlPath)
    {
        if (Path.IsPathRooted(workflowYamlPath) || File.Exists(workflowYamlPath))
            return workflowYamlPath;

        var baseDirectoryPath = Path.Combine(AppContext.BaseDirectory, workflowYamlPath);
        return File.Exists(baseDirectoryPath) ? baseDirectoryPath : workflowYamlPath;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = ScopeWorkflowCapabilityConventions.NormalizeOptional(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
