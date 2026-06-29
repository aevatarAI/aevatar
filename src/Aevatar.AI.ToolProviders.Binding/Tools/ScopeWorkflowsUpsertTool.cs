using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// Upsert a runnable scope workflow.
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
        "Create or update a runnable, page-visible Aevatar Scope Workflow in the current scope. " +
        "Use this as the default write path when the user asks to create a workflow, run it later, reuse it in the current app, or make it visible in the workflow list. " +
        "If an Ornn skill produced workflow YAML, mount/import that template here before starting or claiming the workflow is runnable. " +
        "If the user also asks to publish a skill/template/share package, call this first as the primary runnable store, then optionally export with Ornn.";

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
              "description": "Workflow YAML to store as the runnable Scope Workflow revision"
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

            var scopeId = AgentToolRequestContext.ScopeId;
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error("scope_id not available in request context");

            var workflowId = args.Str("workflow_id");
            if (string.IsNullOrWhiteSpace(workflowId))
                return JsonDefaults.Error("'workflow_id' is required");

            var workflowYaml = args.Str("workflow_yaml");
            if (string.IsNullOrWhiteSpace(workflowYaml))
                return JsonDefaults.Error("'workflow_yaml' is required");

            var inlineWorkflowYamls = args.StrDictionary("inline_workflow_yamls");
            if (inlineWorkflowYamls is null && args.Has("inline_workflow_yamls"))
                return JsonDefaults.Error("'inline_workflow_yamls' must be an object whose values are strings");

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
                args.Str("revision_id")), ct);

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
