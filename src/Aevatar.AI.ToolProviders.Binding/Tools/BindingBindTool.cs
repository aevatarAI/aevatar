using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Ports;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// Bind a service (workflow, script, or GAgent) to the current scope.
/// Delegates to IScopeBindingCommandPort.UpsertAsync.
/// </summary>
public sealed class BindingBindTool : IAgentTool
{
    private readonly IScopeBindingCommandPort _commandPort;
    private readonly IWorkflowDefinitionCommandAdapter? _definitionAdapter;

    public BindingBindTool(
        IScopeBindingCommandPort commandPort,
        IWorkflowDefinitionCommandAdapter? definitionAdapter = null)
    {
        _commandPort = commandPort;
        _definitionAdapter = definitionAdapter;
    }

    public string Name => "binding_bind";

    public string Description =>
        "Bind a service to the current scope. " +
        "Supports three implementation kinds: 'workflow' (requires workflow_name or workflow_yamls), " +
        "'scripting' (requires script_id), and 'gagent' (requires gagent_type). " +
        "Use binding_list to see existing bindings before creating new ones.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "kind": {
              "type": "string",
              "enum": ["workflow", "scripting", "gagent"],
              "description": "Implementation kind to bind"
            },
            "workflow_name": {
              "type": "string",
              "description": "Name of a locally saved workflow definition to bind (for 'workflow' kind)"
            },
            "workflow_yamls": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Inline workflow YAML definitions (for 'workflow' kind, alternative to workflow_name)"
            },
            "script_id": {
              "type": "string",
              "description": "Script ID to bind (required for 'scripting' kind)"
            },
            "script_revision": {
              "type": "string",
              "description": "Optional script revision (for 'scripting' kind)"
            },
            "gagent_type": {
              "type": "string",
              "description": "GAgent actor type name (required for 'gagent' kind)"
            },
            "display_name": {
              "type": "string",
              "description": "Optional display name for the binding"
            },
            "service_id": {
              "type": "string",
              "description": "Optional explicit service ID (auto-generated if omitted)"
            }
          },
          "required": ["kind"]
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

            var scopeId = AgentToolRequestContext.TryGet("scope_id");
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error("scope_id not available in request context");

            var kindStr = args.Str("kind");
            if (string.IsNullOrWhiteSpace(kindStr))
                return JsonDefaults.Error("'kind' is required (workflow, scripting, gagent)");

            // Generate a stable service ID if not explicitly provided to avoid silent aliasing to "default"
            var explicitServiceId = args.Str("service_id")?.Trim();
            var serviceId = !string.IsNullOrWhiteSpace(explicitServiceId)
                ? explicitServiceId
                : null; // Let each builder derive a meaningful ID from the binding source

            ScopeBindingUpsertRequest request;
            switch (kindStr.ToLowerInvariant())
            {
                case "workflow":
                    var wfReq = await BuildWorkflowRequestAsync(scopeId, serviceId, args, ct);
                    if (wfReq == null)
                        return JsonDefaults.Error("'workflow_name' or 'workflow_yamls' is required for 'workflow' kind");
                    request = wfReq;
                    break;
                case "scripting":
                    var scReq = BuildScriptingRequest(scopeId, serviceId, args);
                    if (scReq == null)
                        return JsonDefaults.Error("'script_id' is required for 'scripting' kind");
                    request = scReq;
                    break;
                case "gagent":
                    var gaReq = BuildGAgentRequest(scopeId, serviceId, args);
                    if (gaReq == null)
                        return JsonDefaults.Error("'gagent_type' is required for 'gagent' kind");
                    request = gaReq;
                    break;
                default:
                    return JsonDefaults.Error($"Unknown kind '{kindStr}'. Must be: workflow, scripting, gagent");
            }

            var result = await _commandPort.UpsertAsync(request, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                scope_id = result.ScopeId,
                service_id = result.ServiceId,
                display_name = result.DisplayName,
                revision_id = result.RevisionId,
                implementation_kind = result.ImplementationKind.ToString().ToLowerInvariant(),
                expected_actor_id = result.ExpectedActorId,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Bind failed: {ex.GetType().Name}");
        }
    }

    private async Task<ScopeBindingUpsertRequest?> BuildWorkflowRequestAsync(
        string scopeId, string? serviceId, ToolArgs args, CancellationToken ct)
    {
        // Option 1: inline YAML
        var yamls = args.StrArray("workflow_yamls");
        string? workflowName = null;

        // Option 2: read from local definition by name
        if (yamls.Length == 0 && _definitionAdapter is not null)
        {
            workflowName = args.Str("workflow_name");
            if (!string.IsNullOrWhiteSpace(workflowName))
            {
                var snapshot = await _definitionAdapter.GetDefinitionAsync(workflowName, ct);
                if (snapshot is not null)
                    yamls = [snapshot.Yaml];
            }
        }

        if (yamls.Length == 0)
            return null;

        // Derive service ID from workflow name if not explicitly provided
        serviceId ??= !string.IsNullOrWhiteSpace(workflowName)
            ? $"wf-{workflowName}"
            : $"wf-{Guid.NewGuid():N}"[..16];

        return new ScopeBindingUpsertRequest(
            ScopeId: scopeId,
            ImplementationKind: ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec(yamls),
            DisplayName: args.Str("display_name") ?? workflowName,
            ServiceId: serviceId);
    }

    private static ScopeBindingUpsertRequest? BuildScriptingRequest(
        string scopeId, string? serviceId, ToolArgs args)
    {
        var scriptId = args.Str("script_id");
        if (string.IsNullOrWhiteSpace(scriptId))
            return null;

        return new ScopeBindingUpsertRequest(
            ScopeId: scopeId,
            ImplementationKind: ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec(scriptId, args.Str("script_revision")),
            DisplayName: args.Str("display_name") ?? scriptId,
            ServiceId: serviceId ?? $"script-{scriptId}");
    }

    private static ScopeBindingUpsertRequest? BuildGAgentRequest(
        string scopeId, string? serviceId, ToolArgs args)
    {
        var gagentType = args.Str("gagent_type");
        if (string.IsNullOrWhiteSpace(gagentType))
            return null;

        // Use short type name for service ID (e.g., "MyAgent" from "Namespace.MyAgent")
        var shortType = gagentType.Contains('.')
            ? gagentType[(gagentType.LastIndexOf('.') + 1)..]
            : gagentType;

        return new ScopeBindingUpsertRequest(
            ScopeId: scopeId,
            ImplementationKind: ScopeBindingImplementationKind.GAgent,
            GAgent: new ScopeBindingGAgentSpec(gagentType, []),
            DisplayName: args.Str("display_name") ?? shortType,
            ServiceId: serviceId ?? $"agent-{shortType.ToLowerInvariant()}");
    }
}
