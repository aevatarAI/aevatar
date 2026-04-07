using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Scripting.Ports;

namespace Aevatar.AI.ToolProviders.Scripting.Tools;

/// <summary>
/// Read the source code of a script revision.
/// Enables iterative development: read existing source, modify, recompile.
/// </summary>
public sealed class ScriptSourceTool : IAgentTool
{
    private readonly IScriptToolCatalogQueryAdapter _queryPort;

    public ScriptSourceTool(IScriptToolCatalogQueryAdapter queryPort)
    {
        _queryPort = queryPort;
    }

    public string Name => "script_source";

    public string Description =>
        "Read the source code of a script revision. " +
        "If revision is omitted, reads the active revision. " +
        "Use this to understand what an existing script does, or to retrieve source for modification and recompilation. " +
        "Returns C# source files and optional proto files.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "script_id": {
              "type": "string",
              "description": "The script ID"
            },
            "revision": {
              "type": "string",
              "description": "Optional: the revision to read. If omitted, uses the active revision."
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

            var revision = args.Str("revision");

            // If no revision specified, look up the active one
            if (string.IsNullOrWhiteSpace(revision))
            {
                var entry = await _queryPort.GetAsync(scriptId, ct);
                if (entry == null)
                    return JsonDefaults.Error($"Script '{scriptId}' not found in catalog");
                revision = entry.ActiveRevision;
                if (string.IsNullOrWhiteSpace(revision))
                    return JsonDefaults.Error($"Script '{scriptId}' has no active revision");
            }

            var snapshot = await _queryPort.GetSourceAsync(scriptId, revision, ct);
            if (snapshot == null)
                return JsonDefaults.Error(
                    $"Source not found for script '{scriptId}' revision '{revision}'");

            return JsonSerializer.Serialize(new
            {
                script_id = snapshot.ScriptId,
                revision = snapshot.Revision,
                source_files = snapshot.SourceFiles,
                proto_files = snapshot.ProtoFiles,
                file_count = snapshot.SourceFiles.Count + (snapshot.ProtoFiles?.Count ?? 0),
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Source read failed: {ex.GetType().Name}");
        }
    }
}
