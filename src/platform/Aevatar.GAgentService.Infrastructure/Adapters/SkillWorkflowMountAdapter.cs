using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class SkillWorkflowMountAdapter : ISkillWorkflowMountPort
{
    private readonly IScopeWorkflowCommandPort _scopeWorkflowCommandPort;
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;

    public SkillWorkflowMountAdapter(
        IScopeWorkflowCommandPort scopeWorkflowCommandPort,
        IWorkflowDefinitionParser workflowDefinitionParser)
    {
        _scopeWorkflowCommandPort = scopeWorkflowCommandPort ?? throw new ArgumentNullException(nameof(scopeWorkflowCommandPort));
        _workflowDefinitionParser = workflowDefinitionParser ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
    }

    public async Task<SkillWorkflowMountResult> MountAsync(
        SkillWorkflowMountRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Workflows.Count == 0)
        {
            return new SkillWorkflowMountResult(
                Status: "no_workflows",
                Mounted: false,
                Workflows: [],
                Message: "The skill does not expose workflow YAML bundles.");
        }

        if (string.IsNullOrWhiteSpace(request.CallerId))
            throw new InvalidOperationException("Skill workflow mounting requires an authenticated caller identity.");

        var mounted = new List<MountedSkillWorkflow>(request.Workflows.Count);
        foreach (var workflow in request.Workflows)
        {
            var bundle = await ParseBundleAsync(workflow, ct);
            var upsert = await _scopeWorkflowCommandPort.UpsertAsync(
                new ScopeWorkflowUpsertRequest(
                    request.ScopeId,
                    workflow.WorkflowId,
                    bundle.EntryWorkflowYaml,
                    WorkflowName: bundle.EntryWorkflowName,
                    DisplayName: workflow.WorkflowId,
                    InlineWorkflowYamls: bundle.SubWorkflowYamls)
                {
                    CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                        request.CallerId.Trim(),
                        NyxIdCallerCredentialSelection.SourceReadableUserBearerOrNull(
                            request.NyxIdAccessToken)),
                },
                ct);

            mounted.Add(new MountedSkillWorkflow(
                WorkflowId: workflow.WorkflowId,
                ServiceId: upsert.WorkflowId,
                EndpointId: "chat",
                RevisionId: upsert.RevisionId));
        }

        return new SkillWorkflowMountResult(
            Status: "mounted",
            Mounted: mounted.Count > 0,
            Workflows: mounted,
            Message: mounted.Count > 0
                ? "Mounted skill workflows into the current scope."
                : "No skill workflows were mounted.");
    }

    private async Task<WorkflowYamlBundle> ParseBundleAsync(
        SkillWorkflowDescriptor workflow,
        CancellationToken ct)
    {
        if (workflow.WorkflowYamls.Count == 0)
            throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' does not include any YAML documents.");

        string? entryWorkflowName = null;
        string? entryWorkflowYaml = null;
        var subWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenWorkflowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < workflow.WorkflowYamls.Count; index++)
        {
            var workflowYaml = workflow.WorkflowYamls[index]?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(workflowYaml))
                throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' contains an empty YAML document.");

            var parse = await _workflowDefinitionParser.ParseWorkflowYamlAsync(workflowYaml, ct);
            if (!parse.Succeeded)
                throw new InvalidOperationException(parse.Error);

            var workflowName = parse.WorkflowName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(workflowName))
                throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' must define a workflow name.");

            if (!seenWorkflowNames.Add(workflowName))
                throw new InvalidOperationException(
                    $"Skill workflow '{workflow.WorkflowId}' contains duplicate workflow name '{workflowName}'.");

            if (index == 0)
            {
                entryWorkflowName = workflowName;
                entryWorkflowYaml = workflowYaml;
                continue;
            }

            subWorkflowYamls[workflowName] = workflowYaml;
        }

        return new WorkflowYamlBundle(
            entryWorkflowName ?? throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' is missing the root workflow."),
            entryWorkflowYaml ?? throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' is missing the root workflow YAML."),
            subWorkflowYamls);
    }

    private sealed record WorkflowYamlBundle(
        string EntryWorkflowName,
        string EntryWorkflowYaml,
        IReadOnlyDictionary<string, string> SubWorkflowYamls);
}
