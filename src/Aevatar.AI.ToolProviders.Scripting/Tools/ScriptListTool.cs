using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Scripting.Ports;

namespace Aevatar.AI.ToolProviders.Scripting.Tools;

/// <summary>
/// List and search scripts in the catalog.
/// Prevents redundant code generation by discovering existing scripts.
/// </summary>
public sealed class ScriptListTool : IAgentTool
{
    private readonly IScriptToolCatalogQueryAdapter _queryPort;
    private readonly ScriptingToolOptions _options;

    public ScriptListTool(IScriptToolCatalogQueryAdapter queryPort, ScriptingToolOptions options)
    {
        _queryPort = queryPort;
        _options = options;
    }

    public string Name => "script_list";

    public string Description =>
        "List and search scripts in the catalog. " +
        "Use this before writing new scripts to check if a suitable one already exists. " +
        "Returns script IDs, active revisions, and descriptions.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "filter": {
              "type": "string",
              "description": "Optional keyword filter to match against script IDs, names, or descriptions"
            },
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

            var filter = args.Str("filter");
            var maxResults = args.Int("max_results", _options.MaxListResults);
            maxResults = Math.Clamp(maxResults, 1, _options.MaxListResults);

            var entries = await _queryPort.ListAsync(filter, maxResults, ct);

            var scripts = entries.Select(e => new
            {
                script_id = e.ScriptId,
                display_name = e.DisplayName,
                description = e.Description,
                active_revision = e.ActiveRevision,
                revision_count = e.RevisionHistory.Count,
                last_updated = e.LastUpdated?.ToString("O"),
            }).ToArray();

            return JsonSerializer.Serialize(new
            {
                count = scripts.Length,
                scripts,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"List query failed: {ex.GetType().Name}");
        }
    }
}
