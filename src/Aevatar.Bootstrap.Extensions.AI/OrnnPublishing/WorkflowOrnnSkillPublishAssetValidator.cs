using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;

namespace Aevatar.Bootstrap.Extensions.AI.OrnnPublishing;

public sealed class WorkflowOrnnSkillPublishAssetValidator : IOrnnSkillPublishAssetValidator
{
    private readonly IAgentKindRegistry? _agentKindRegistry;
    private readonly WorkflowParser _parser = new();

    public WorkflowOrnnSkillPublishAssetValidator(IAgentKindRegistry? agentKindRegistry = null)
    {
        _agentKindRegistry = agentKindRegistry;
    }

    public string AssetKind => OrnnSkillPublishValidationPipeline.WorkflowYamlAssetKind;

    public Task<IReadOnlyList<OrnnSkillPublishDiagnostic>> ValidateAsync(
        OrnnSkillPublishRequest request,
        CancellationToken ct = default)
    {
        var diagnostics = new List<OrnnSkillPublishDiagnostic>();
        foreach (var workflow in request.WorkflowYamls)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"workflow_yamls/{workflow.WorkflowId}.yaml";
            try
            {
                var definition = _parser.Parse(workflow.Content);
                var errors = WorkflowValidator.Validate(definition);
                diagnostics.AddRange(errors.Select(error => new OrnnSkillPublishDiagnostic("invalid_workflow_yaml", error, path)));
                ValidateAgentKinds(definition, path, diagnostics);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new OrnnSkillPublishDiagnostic("invalid_workflow_yaml", ex.Message, path));
            }
        }

        return Task.FromResult<IReadOnlyList<OrnnSkillPublishDiagnostic>>(diagnostics);
    }

    private void ValidateAgentKinds(
        WorkflowDefinition definition,
        string path,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (_agentKindRegistry == null)
            return;

        foreach (var role in definition.Roles)
        {
            var agentKind = string.IsNullOrWhiteSpace(role.AgentKind)
                ? WorkflowRoleConventions.DefaultAgentKind
                : role.AgentKind.Trim();
            if (!_agentKindRegistry.TryResolve(agentKind, out _))
            {
                diagnostics.Add(new OrnnSkillPublishDiagnostic(
                    "unresolved_agent_kind",
                    $"Role '{role.Id}' declares unknown agent_kind '{agentKind}'.",
                    path));
            }
        }
    }
}
