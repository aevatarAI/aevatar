using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Binding.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// Remove a service binding from the current scope.
/// This is a destructive operation that always requires approval.
/// </summary>
public sealed class BindingUnbindTool : IAgentTool
{
    private readonly IScopeBindingUnbindAdapter _unbindAdapter;

    public BindingUnbindTool(IScopeBindingUnbindAdapter unbindAdapter)
    {
        _unbindAdapter = unbindAdapter;
    }

    public string Name => "binding_unbind";

    public string Description =>
        "Remove a service binding from the current scope. " +
        "This is a destructive operation — the bound service will be deactivated. " +
        "Use binding_list to verify the service_id before unbinding.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "service_id": {
              "type": "string",
              "description": "The service ID to unbind"
            }
          },
          "required": ["service_id"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;
    public bool IsDestructive => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            if (args.ParseError != null)
                return JsonDefaults.Error(args.ParseError);

            var serviceId = args.Str("service_id");
            if (string.IsNullOrWhiteSpace(serviceId))
                return JsonDefaults.Error("'service_id' is required");

            var scopeId = AgentToolRequestContext.TryGet("scope_id");
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error("scope_id not available in request context");

            var result = await _unbindAdapter.UnbindAsync(scopeId, serviceId, ct);

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                scope_id = scopeId,
                service_id = result.ServiceId,
                error = result.Error,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Unbind failed: {ex.GetType().Name}");
        }
    }
}
