using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
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
    public BindingBindTool(IScopeBindingCommandPort commandPort)
    {
        _commandPort = commandPort;
    }

    public string Name => "binding_bind";

    public string Description =>
        "Bind a service to the current scope. " +
        "Supports three implementation kinds: 'workflow' (requires workflow_yamls), " +
        "'scripting' (requires script_id), and 'gagent' (requires agent_kind). " +
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
            "workflow_yamls": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Inline workflow YAML definitions (required for 'workflow' kind)"
            },
            "script_id": {
              "type": "string",
              "description": "Script ID to bind (required for 'scripting' kind)"
            },
            "script_revision": {
              "type": "string",
              "description": "Optional script revision (for 'scripting' kind)"
            },
            "agent_kind": {
              "type": "string",
              "description": "Canonical GAgent kind (required for 'gagent' kind)"
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

            var scopeId = ToolOwnerScopeResolver.Resolve();
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error(ToolOwnerScopeResolver.MissingMessage);

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
                    var wfReq = BuildWorkflowRequest(scopeId, serviceId, args);
                    if (wfReq == null)
                        return JsonDefaults.Error("'workflow_yamls' is required for 'workflow' kind");
                    request = wfReq;
                    break;
                case "scripting":
                    var scReq = BuildScriptingRequest(scopeId, serviceId, args);
                    if (scReq == null)
                        return JsonDefaults.Error("'script_id' is required for 'scripting' kind");
                    request = scReq;
                    break;
                case "gagent":
                    if (!string.IsNullOrWhiteSpace(args.Str("gagent_type")))
                        return JsonDefaults.Error("'gagent_type' is not accepted. Use 'agent_kind'.");

                    var gaReq = BuildGAgentRequest(scopeId, serviceId, args);
                    if (gaReq == null)
                        return JsonDefaults.Error("'agent_kind' is required for 'gagent' kind");
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

    private static ScopeBindingUpsertRequest? BuildWorkflowRequest(
        string scopeId, string? serviceId, ToolArgs args)
    {
        var yamls = args.StrArray("workflow_yamls");
        if (yamls.Length == 0)
            return null;

        serviceId ??= $"wf-{Guid.NewGuid():N}"[..16];

        return new ScopeBindingUpsertRequest(
            ScopeId: scopeId,
            ImplementationKind: ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec(yamls),
            DisplayName: args.Str("display_name"),
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
        var agentKind = args.Str("agent_kind");
        if (string.IsNullOrWhiteSpace(agentKind))
            return null;

        var normalizedKind = agentKind.Trim();
        var shortKind = normalizedKind.Contains('.')
            ? normalizedKind[(normalizedKind.LastIndexOf('.') + 1)..]
            : normalizedKind;

        return new ScopeBindingUpsertRequest(
            ScopeId: scopeId,
            ImplementationKind: ScopeBindingImplementationKind.GAgent,
            GAgent: new ScopeBindingGAgentSpec(
                AgentKind: normalizedKind,
                Endpoints: []),
            DisplayName: args.Str("display_name") ?? shortKind,
            ServiceId: serviceId ?? $"agent-{shortKind.ToLowerInvariant()}");
    }
}
