using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed class WorkflowExternalCapabilityAdmissionService :
    IWorkflowExternalCapabilityAdmissionService
{
    private readonly IWorkflowDefinitionParser _parser;
    private readonly IExternalWorkflowCapabilityReadinessPort _readinessPort;
    private readonly TimeProvider _timeProvider;

    public WorkflowExternalCapabilityAdmissionService(
        IWorkflowDefinitionParser parser,
        IExternalWorkflowCapabilityReadinessPort readinessPort,
        TimeProvider? timeProvider = null)
    {
        _parser = parser;
        _readinessPort = readinessPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified)
            throw new InvalidOperationException("External capability execution mode is required.");

        var definition = await ParseDefinitionAsync(request, cancellationToken);
        if (request.ExistingPlan is not null)
        {
            WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
                request.ExistingPlan,
                definition.WorkflowYaml,
                definition.InlineWorkflowYamls,
                request.ExecutionMode,
                definition.Capabilities);
            EnsureSourcesAreFresh(request.ExistingPlan);
            return request.ExistingPlan.Clone();
        }

        var sources = new List<ExternalCapabilitySourceStamp>();
        foreach (var capability in definition.Capabilities)
        {
            var readiness = await _readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    request.Access,
                    capability,
                    request.ExecutionMode),
                cancellationToken);
            if (readiness.Status != ExternalCapabilityReadinessStatus.Ready)
                throw new WorkflowExternalCapabilityAdmissionException(readiness);
            sources.AddRange(readiness.Sources.Select(static source => source.Clone()));
        }

        return WorkflowCapabilityAdmissionPlanIntegrity.Create(
            definition.WorkflowYaml,
            definition.InlineWorkflowYamls,
            request.ExecutionMode,
            definition.Capabilities,
            sources);
    }

    private async Task<ParsedAdmissionDefinition> ParseDefinitionAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.WorkflowYamls is { } workflowYamls)
            return await ParseWorkflowBundleAsync(workflowYamls, cancellationToken);

        var definitions = new List<(string Key, string Yaml)>
        {
            ("root", request.WorkflowYaml),
        };
        definitions.AddRange(request.InlineWorkflowYamls
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => (item.Key, item.Value)));

        var capabilities = new Dictionary<string, ExternalWorkflowCapabilityRef>(StringComparer.Ordinal);
        foreach (var (key, yaml) in definitions)
        {
            var parse = await _parser.ParseWorkflowYamlAsync(yaml, cancellationToken);
            if (!parse.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Workflow definition '{key}' is invalid: {parse.Error}");
            }

            AddCapabilities(capabilities, parse);
        }

        return new ParsedAdmissionDefinition(
            request.WorkflowYaml,
            request.InlineWorkflowYamls,
            SortCapabilities(capabilities));
    }

    private async Task<ParsedAdmissionDefinition> ParseWorkflowBundleAsync(
        IReadOnlyList<string> workflowYamls,
        CancellationToken cancellationToken)
    {
        string? rootYaml = null;
        var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var workflowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capabilities = new Dictionary<string, ExternalWorkflowCapabilityRef>(StringComparer.Ordinal);

        for (var index = 0; index < workflowYamls.Count; index++)
        {
            var yaml = workflowYamls[index]?.Trim() ?? string.Empty;
            if (yaml.Length == 0)
                throw new InvalidOperationException("Workflow YAML bundle must not contain empty definitions.");

            var parse = await _parser.ParseWorkflowYamlAsync(yaml, cancellationToken);
            if (!parse.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Workflow definition at index {index} is invalid: {parse.Error}");
            }

            var workflowName = parse.WorkflowName?.Trim() ?? string.Empty;
            if (workflowName.Length == 0)
                throw new InvalidOperationException($"Workflow definition at index {index} has no workflow name.");
            if (!workflowNames.Add(workflowName))
                throw new InvalidOperationException($"Duplicate workflow name '{workflowName}' in workflow YAML bundle.");

            if (index == 0)
                rootYaml = yaml;
            else
                inlineWorkflowYamls.Add(workflowName, yaml);

            AddCapabilities(capabilities, parse);
        }

        return new ParsedAdmissionDefinition(
            rootYaml ?? throw new InvalidOperationException("Workflow YAML bundle has no root definition."),
            inlineWorkflowYamls,
            SortCapabilities(capabilities));
    }

    private static void AddCapabilities(
        IDictionary<string, ExternalWorkflowCapabilityRef> capabilities,
        WorkflowYamlParseResult parse)
    {
        foreach (var capability in parse.AuthorizationDependencies?.ExternalCapabilities ?? [])
        {
            capabilities.TryAdd(
                WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey(capability),
                capability.Clone());
        }
    }

    private static IReadOnlyList<ExternalWorkflowCapabilityRef> SortCapabilities(
        IReadOnlyDictionary<string, ExternalWorkflowCapabilityRef> capabilities) =>
        capabilities.Values
            .OrderBy(WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey, StringComparer.Ordinal)
            .ToArray();

    private void EnsureSourcesAreFresh(WorkflowCapabilityAdmissionPlan plan)
    {
        if (plan.ExternalCapabilities.Count == 0)
            return;

        var now = _timeProvider.GetUtcNow();
        var stale = plan.SourceStamps.FirstOrDefault(source =>
            source.FreshUntil is null || source.FreshUntil.ToDateTimeOffset() <= now);
        if (stale is null)
            return;

        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = plan.ExecutionMode,
            Status = ExternalCapabilityReadinessStatus.SourceStale,
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readiness.Status,
            Code = "ADMISSION_SOURCE_STALE",
            SafeMessage = "External capability admission evidence is stale.",
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.RefreshSource,
            Label = "Refresh capability readiness",
        });
        throw new WorkflowExternalCapabilityAdmissionException(readiness);
    }

    private sealed record ParsedAdmissionDefinition(
        string WorkflowYaml,
        IReadOnlyDictionary<string, string> InlineWorkflowYamls,
        IReadOnlyList<ExternalWorkflowCapabilityRef> Capabilities);
}
