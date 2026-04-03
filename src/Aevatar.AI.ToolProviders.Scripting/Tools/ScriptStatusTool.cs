using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Scripting.Ports;

namespace Aevatar.AI.ToolProviders.Scripting.Tools;

/// <summary>
/// Query the status of a script: catalog entry, definition info, contract metadata.
/// </summary>
public sealed class ScriptStatusTool : IAgentTool
{
    private readonly IScriptToolCatalogQueryAdapter _queryPort;

    public ScriptStatusTool(IScriptToolCatalogQueryAdapter queryPort)
    {
        _queryPort = queryPort;
    }

    public string Name => "script_status";

    public string Description =>
        "Query the status of a script. Returns catalog entry (active/previous revisions), " +
        "and optionally detailed definition info (state/readmodel types, commands, events). " +
        "Use script_list to discover scripts first.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "script_id": {
              "type": "string",
              "description": "The script ID to query"
            },
            "revision": {
              "type": "string",
              "description": "Optional: query a specific revision's definition info. If omitted, shows the active revision."
            },
            "include_definition": {
              "type": "boolean",
              "description": "Include detailed definition info (types, commands, events). Default true."
            }
          },
          "required": ["script_id"]
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

            var scriptId = args.Str("script_id");
            if (string.IsNullOrWhiteSpace(scriptId))
                return JsonDefaults.Error("'script_id' is required");

            var entry = await _queryPort.GetAsync(scriptId, ct);
            if (entry == null)
                return JsonDefaults.Error($"Script '{scriptId}' not found in catalog");

            var includeDefinition = args.Bool("include_definition", true);
            ScriptDefinitionInfo? definition = null;

            if (includeDefinition)
            {
                var revision = args.Str("revision") ?? entry.ActiveRevision;
                if (!string.IsNullOrWhiteSpace(revision))
                    definition = await _queryPort.GetDefinitionAsync(scriptId, revision, ct);
            }

            return JsonSerializer.Serialize(new
            {
                script_id = entry.ScriptId,
                display_name = entry.DisplayName,
                description = entry.Description,
                active_revision = entry.ActiveRevision,
                revision_history = entry.RevisionHistory,
                last_updated = entry.LastUpdated?.ToString("O"),
                definition = definition != null
                    ? new
                    {
                        revision = definition.Revision,
                        source_hash = definition.SourceHash,
                        state_type_url = definition.StateTypeUrl,
                        read_model_type_url = definition.ReadModelTypeUrl,
                        command_type_urls = definition.CommandTypeUrls,
                        domain_event_type_urls = definition.DomainEventTypeUrls,
                        signal_type_urls = definition.SignalTypeUrls,
                        compiled_at = definition.CompiledAt?.ToString("O"),
                    }
                    : null,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Status query failed: {ex.GetType().Name}");
        }
    }
}
