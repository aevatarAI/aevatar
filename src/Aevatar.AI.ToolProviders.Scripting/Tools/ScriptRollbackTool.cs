using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Scripting.Ports;

namespace Aevatar.AI.ToolProviders.Scripting.Tools;

/// <summary>
/// Rollback the active script revision to a previous one.
/// </summary>
public sealed class ScriptRollbackTool : IAgentTool
{
    private readonly IScriptToolCatalogCommandAdapter _catalogCommandPort;

    public ScriptRollbackTool(IScriptToolCatalogCommandAdapter catalogCommandPort)
    {
        _catalogCommandPort = catalogCommandPort;
    }

    public string Name => "script_rollback";

    public string Description =>
        "Rollback the active script revision. " +
        "If target_revision is omitted, rolls back to the previous revision. " +
        "Use script_status to check current and previous revisions before rolling back.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "script_id": {
              "type": "string",
              "description": "The script ID to rollback"
            },
            "target_revision": {
              "type": "string",
              "description": "Optional: specific revision to rollback to. Defaults to previous revision."
            },
            "expected_current_revision": {
              "type": "string",
              "description": "Optional: expected current active revision for optimistic concurrency check"
            }
          },
          "required": ["script_id"]
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

            var scriptId = args.Str("script_id");
            if (string.IsNullOrWhiteSpace(scriptId))
                return JsonDefaults.Error("'script_id' is required");

            var result = await _catalogCommandPort.RollbackAsync(
                scriptId,
                args.Str("target_revision"),
                args.Str("expected_current_revision"),
                ct);

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
            return JsonDefaults.Error($"Rollback failed: {ex.GetType().Name}");
        }
    }
}
