using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal sealed class WorkflowDefinitionParser : IWorkflowDefinitionParser
{
    private readonly ISet<string> _knownStepTypes;
    private readonly IAgentKindRegistry? _agentKindRegistry;
    private readonly WorkflowParser _workflowParser = new();

    public WorkflowDefinitionParser(
        IEnumerable<IWorkflowModulePack> modulePacks,
        IAgentKindRegistry? agentKindRegistry = null)
    {
        var packs = modulePacks?.ToList()
            ?? throw new ArgumentNullException(nameof(modulePacks));
        if (packs.Count == 0)
            packs.Add(new WorkflowCoreModulePack());

        _knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            packs.SelectMany(static pack => pack.Modules).SelectMany(static module => module.Names));
        _agentKindRegistry = agentKindRegistry;
    }

    public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
        string workflowYaml,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workflowYaml))
            return Task.FromResult(WorkflowYamlParseResult.Invalid("Workflow YAML is required."));

        try
        {
            var workflow = _workflowParser.Parse(workflowYaml);
            var errors = WorkflowValidator.Validate(
                workflow,
                new WorkflowValidator.WorkflowValidationOptions
                {
                    RequireKnownStepTypes = true,
                    KnownStepTypes = _knownStepTypes,
                },
                availableWorkflowNames: null);
            if (errors.Count > 0)
                return Task.FromResult(WorkflowYamlParseResult.Invalid(string.Join("; ", errors)));

            if (_agentKindRegistry != null)
            {
                foreach (var role in workflow.Roles)
                {
                    var agentKind = role.AgentKind?.Trim() ?? string.Empty;
                    // The canonical default is a workflow protocol kind; the local registry only validates extensions.
                    if (string.Equals(
                            agentKind,
                            WorkflowRoleConventions.DefaultAgentKind,
                            StringComparison.Ordinal) ||
                        _agentKindRegistry.TryResolve(agentKind, out _))
                    {
                        continue;
                    }

                    return Task.FromResult(WorkflowYamlParseResult.Invalid(
                        $"Role '{role.Id}' declares unknown agent_kind '{agentKind}'. " +
                        $"Register an agent for that kind or use the default '{WorkflowRoleConventions.DefaultAgentKind}'."));
                }
            }

            var workflowName = NormalizeWorkflowName(workflow.Name);
            if (string.IsNullOrWhiteSpace(workflowName))
                return Task.FromResult(WorkflowYamlParseResult.Invalid("Workflow name is required."));

            return Task.FromResult(WorkflowYamlParseResult.Success(
                workflowName,
                WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow)));
        }
        catch (WorkflowYamlResourceLimitException ex)
        {
            return Task.FromResult(WorkflowYamlParseResult.Invalid(
                ex.Message,
                errorCode: WorkflowYamlParseErrorCode.ResourceLimit));
        }
        catch (WorkflowExternalCapabilityValidationException ex)
        {
            return Task.FromResult(WorkflowYamlParseResult.Invalid(ex.Message, ex.Readiness));
        }
        catch (Exception ex)
        {
            return Task.FromResult(WorkflowYamlParseResult.Invalid(ex.Message));
        }
    }

    public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
        IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
        CancellationToken ct = default)
    {
        if (inlineWorkflowDocuments.Count == 0)
            return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

        var workflowByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string entryWorkflowName = string.Empty;
        string entryWorkflowYaml = string.Empty;

        for (var i = 0; i < inlineWorkflowDocuments.Count; i++)
        {
            var document = inlineWorkflowDocuments[i];
            var yaml = document.Yaml;
            if (string.IsNullOrWhiteSpace(yaml))
                return WorkflowInlineYamlBundleParseResult.Invalid($"workflowYamls[{i}] is required.");

            var parseResult = await ParseWorkflowYamlAsync(yaml, ct).ConfigureAwait(false);
            if (!parseResult.Succeeded)
                return WorkflowInlineYamlBundleParseResult.Invalid(
                    parseResult.Error,
                    parseResult.ExternalCapabilityReadiness,
                    parseResult.ErrorCode);

            var workflowName = NormalizeWorkflowName(parseResult.WorkflowName);
            if (string.IsNullOrWhiteSpace(workflowName))
                return WorkflowInlineYamlBundleParseResult.Invalid($"workflowYamls[{i}] workflow name is required.");

            var documentName = NormalizeWorkflowName(document.Name);
            if (!string.IsNullOrWhiteSpace(documentName) &&
                !string.Equals(documentName, workflowName, StringComparison.OrdinalIgnoreCase))
            {
                return WorkflowInlineYamlBundleParseResult.Invalid(
                    $"workflowYamls[{i}] document name '{documentName}' does not match workflow name '{workflowName}'.");
            }

            if (!workflowByName.TryAdd(workflowName, yaml))
                return WorkflowInlineYamlBundleParseResult.Invalid(
                    $"Duplicate workflow name '{workflowName}' in workflowYamls.");

            if (i == 0)
            {
                entryWorkflowName = workflowName;
                entryWorkflowYaml = yaml;
            }
        }

        if (string.IsNullOrWhiteSpace(entryWorkflowName) || string.IsNullOrWhiteSpace(entryWorkflowYaml))
            return WorkflowInlineYamlBundleParseResult.Invalid("Workflow YAML is invalid.");

        return WorkflowInlineYamlBundleParseResult.Success(
            entryWorkflowName,
            entryWorkflowYaml,
            workflowByName);
    }

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();
}
