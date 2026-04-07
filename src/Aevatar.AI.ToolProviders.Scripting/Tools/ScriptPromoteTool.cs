using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Scripting.Ports;

namespace Aevatar.AI.ToolProviders.Scripting.Tools;

/// <summary>
/// Promote a compiled script revision to active in the catalog.
/// This is the persistent path: the promoted script becomes the active version
/// and can receive commands, process events, and maintain state long-term.
/// </summary>
public sealed class ScriptPromoteTool : IAgentTool
{
    private readonly IScriptToolCatalogCommandAdapter _catalogCommandPort;

    public ScriptPromoteTool(IScriptToolCatalogCommandAdapter catalogCommandPort)
    {
        _catalogCommandPort = catalogCommandPort;
    }

    public string Name => "script_promote";

    public string Description =>
        "Promote a compiled script revision to active in the catalog. " +
        "The promoted revision becomes the live version that handles commands and events. " +
        "Use script_compile first to create a revision, then promote it. " +
        "This is the persistent path — for one-off execution, use script_execute instead.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "script_id": {
              "type": "string",
              "description": "The script ID to promote"
            },
            "revision": {
              "type": "string",
              "description": "The compiled revision to promote to active"
            }
          },
          "required": ["script_id", "revision"]
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

            var scriptId = args.Str("script_id");
            var revision = args.Str("revision");

            if (string.IsNullOrWhiteSpace(scriptId))
                return JsonDefaults.Error("'script_id' is required");
            if (string.IsNullOrWhiteSpace(revision))
                return JsonDefaults.Error("'revision' is required");

            var result = await _catalogCommandPort.PromoteAsync(scriptId, revision, ct);

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                script_id = scriptId,
                active_revision = result.ActiveRevision,
                previous_revision = result.PreviousRevision,
                error = result.Error,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Promote failed: {ex.GetType().Name}");
        }
    }
}
