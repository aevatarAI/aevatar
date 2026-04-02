using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Binding.Ports;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

/// <summary>
/// List all service bindings for the current scope.
/// Scope ID is resolved from AgentToolRequestContext.
/// </summary>
public sealed class BindingListTool : IAgentTool
{
    private readonly IScopeBindingQueryAdapter _queryAdapter;
    private readonly BindingToolOptions _options;

    public BindingListTool(IScopeBindingQueryAdapter queryAdapter, BindingToolOptions options)
    {
        _queryAdapter = queryAdapter;
        _options = options;
    }

    public string Name => "binding_list";

    public string Description =>
        "List all service bindings in the current scope. " +
        "Shows bound services (workflows, scripts, GAgents) with their IDs, display names, " +
        "implementation kinds, and revision info. Use this to discover what is deployed.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "max_results": {
              "type": "integer",
              "description": "Maximum results to return (default 100)"
            }
          }
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

            var scopeId = AgentToolRequestContext.TryGet("scope_id");
            if (string.IsNullOrWhiteSpace(scopeId))
                return JsonDefaults.Error("scope_id not available in request context");

            var entries = await _queryAdapter.ListAsync(scopeId, ct);

            var maxResults = args.Int("max_results", _options.MaxListResults);
            maxResults = Math.Clamp(maxResults, 1, _options.MaxListResults);

            var bindings = entries
                .Take(maxResults)
                .Select(e => new
                {
                    service_id = e.ServiceId,
                    display_name = e.DisplayName,
                    implementation_kind = e.ImplementationKind,
                    revision_id = e.RevisionId,
                    expected_actor_id = e.ExpectedActorId,
                    last_updated = e.LastUpdated?.ToString("O"),
                })
                .ToArray();

            return JsonSerializer.Serialize(new
            {
                scope_id = scopeId,
                count = bindings.Length,
                total = entries.Count,
                bindings,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"List query failed: {ex.GetType().Name}");
        }
    }
}
