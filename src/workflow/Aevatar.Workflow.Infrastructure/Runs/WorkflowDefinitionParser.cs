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

            var workflowName = string.IsNullOrWhiteSpace(workflow.Name)
                ? string.Empty
                : workflow.Name.Trim();
            if (string.IsNullOrWhiteSpace(workflowName))
                return Task.FromResult(WorkflowYamlParseResult.Invalid("Workflow name is required."));

            return Task.FromResult(WorkflowYamlParseResult.Success(
                workflowName,
                WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(WorkflowYamlParseResult.Invalid(ex.Message));
        }
    }
}
