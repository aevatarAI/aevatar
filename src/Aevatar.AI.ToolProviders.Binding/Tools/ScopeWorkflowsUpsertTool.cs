using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// Upsert a scope workflow from an Ornn skill recipe.
/// Delegates to IScopeWorkflowCommandPort.UpsertAsync.
/// </summary>
public sealed class ScopeWorkflowsUpsertTool : IAgentTool
{
    private readonly IScopeWorkflowCommandPort _commandPort;

    public ScopeWorkflowsUpsertTool(IScopeWorkflowCommandPort commandPort)
    {
        _commandPort = commandPort;
    }

    public string Name => "scope_workflows_upsert";

    public string Description =>
        "Create or update a workflow in the current scope. " +
        "Use this after an Ornn skill has produced the workflow recipe YAML; this tool only persists it.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "workflow_id": {
              "type": "string",
              "description": "Stable workflow ID inside the current scope"
            },
            "workflow_yaml": {
              "type": "string",
              "description": "Workflow YAML produced by an Ornn skill recipe"
            },
            "workflow_name": {
              "type": "string",
              "description": "Optional workflow name recorded with the workflow"
            },
            "display_name": {
              "type": "string",
              "description": "Optional display name for humans"
            },
            "inline_workflow_yamls": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "Optional named inline workflow YAMLs referenced by workflow_yaml"
            },
            "revision_id": {
              "type": "string",
              "description": "Optional caller-supplied revision ID"
            }
          },
          "required": ["workflow_id", "workflow_yaml"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            if (args.ParseError != null)
                return JsonDefaults.Error(args.ParseError);

            var scopeId = ToolOwnerScopeResolver.Resolve();
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error(ToolOwnerScopeResolver.MissingMessage);

            var workflowId = args.Str("workflow_id");
            if (string.IsNullOrWhiteSpace(workflowId))
                return JsonDefaults.Error("'workflow_id' is required");

            var workflowYaml = args.Str("workflow_yaml");
            if (string.IsNullOrWhiteSpace(workflowYaml))
                return JsonDefaults.Error("'workflow_yaml' is required");

            var inlineWorkflowYamls = args.StrDictionary("inline_workflow_yamls");
            if (inlineWorkflowYamls is null && args.Has("inline_workflow_yamls"))
                return JsonDefaults.Error("'inline_workflow_yamls' must be an object whose values are strings");
            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var accessError))
                return JsonDefaults.Error(accessError ?? "verified caller context is required");

            // Refactor (iter97/cluster-598): Old/New
            //   Old pattern: Ornn-facing workflow recipes had no direct LLM adapter over the scope workflow ports.
            //   New principle: recipes stay owned by Ornn skills; this tool is a thin typed adapter over IScopeWorkflowCommandPort.
            var result = await _commandPort.UpsertAsync(new ScopeWorkflowUpsertRequest(
                scopeId.Trim(),
                workflowId.Trim(),
                workflowYaml,
                args.Str("workflow_name"),
                args.Str("display_name"),
                inlineWorkflowYamls,
                args.Str("revision_id"))
            {
                CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                    access!.CallerId,
                    access.NyxIdCallerCredential,
                    access.NyxIdOrganizationBearerToken),
            }, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                accepted = true,
                scope_id = result.ScopeId,
                workflow_id = result.WorkflowId,
                service_key = result.ServiceKey,
                revision_id = result.RevisionId,
                definition_actor_id_prefix = result.DefinitionActorIdPrefix,
                expected_actor_id = result.ExpectedActorId,
                expected_deployment_id = result.ExpectedDeploymentId,
                acceptance_stage = result.AcceptanceStage,
                propagation_stage = result.PropagationStage,
                read_model_url = result.ReadModelUrl,
                command_handles = result.CommandHandles,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Workflow upsert failed: {ex.GetType().Name}");
        }
    }
}
