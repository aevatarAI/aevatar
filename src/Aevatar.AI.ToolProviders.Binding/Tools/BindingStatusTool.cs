using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Binding.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// Query health status of a specific service binding.
/// Scope ID is resolved from AgentToolRequestContext.
/// </summary>
public sealed class BindingStatusTool : IAgentTool
{
    private readonly IScopeBindingQueryAdapter _queryAdapter;

    public BindingStatusTool(IScopeBindingQueryAdapter queryAdapter)
    {
        _queryAdapter = queryAdapter;
    }

    public string Name => "binding_status";

    public string Description =>
        "Query the health status of a specific service binding. " +
        "Shows whether the bound service is active, its actor IDs, and any errors. " +
        "Requires a service_id (use binding_list to discover available services).";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "service_id": {
              "type": "string",
              "description": "The service ID to check status for"
            }
          },
          "required": ["service_id"]
        }
        """;

    public bool IsReadOnly => true;

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

            var status = await _queryAdapter.GetStatusAsync(scopeId, serviceId, ct);
            if (status == null)
                return JsonSerializer.Serialize(new
                {
                    error = $"No binding found for service '{serviceId}' in scope '{scopeId}'"
                });

            return JsonSerializer.Serialize(new
            {
                scope_id = scopeId,
                service_id = status.ServiceId,
                display_name = status.DisplayName,
                implementation_kind = status.ImplementationKind,
                status = status.Status,
                expected_actor_id = status.ExpectedActorId,
                active_actor_id = status.ActiveActorId,
                error_message = status.ErrorMessage,
                last_checked = status.LastChecked?.ToString("O"),
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Status query failed: {ex.GetType().Name}");
        }
    }
}
