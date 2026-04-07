using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Scripting.Ports;

namespace Aevatar.AI.ToolProviders.Scripting.Tools;

/// <summary>
/// Execute a compiled script in a restricted sandbox (fast-path).
/// Creates a short-lived actor, dispatches input, returns results, then destroys the actor.
/// The sandbox provides restricted runtime capabilities — no cluster publish/send.
/// </summary>
public sealed class ScriptExecuteTool : IAgentTool
{
    private readonly IScriptToolSandboxExecutionAdapter _executionPort;
    private readonly ScriptingToolOptions _options;

    public ScriptExecuteTool(IScriptToolSandboxExecutionAdapter executionPort, ScriptingToolOptions options)
    {
        _executionPort = executionPort;
        _options = options;
    }

    public string Name => "script_execute";

    public string Description =>
        "Execute a compiled script in a restricted sandbox. " +
        "This is the fast-path: compile -> execute -> get result -> actor destroyed. " +
        "Use for exploration, debugging, and one-off computation. " +
        "The sandbox restricts cluster access (no publish/send to other actors). " +
        "For persistent execution, use script_promote instead. " +
        "Returns emitted domain events, final state, and read model as JSON.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "script_id": {
              "type": "string",
              "description": "Script ID (must have been compiled via script_compile)"
            },
            "revision": {
              "type": "string",
              "description": "Revision to execute. If omitted, uses the latest compiled revision."
            },
            "input": {
              "type": "object",
              "description": "Input payload dispatched as the first command to the script behavior"
            },
            "timeout_seconds": {
              "type": "integer",
              "description": "Execution timeout in seconds (1-60, default 30)"
            }
          },
          "required": ["script_id"]
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
            if (string.IsNullOrWhiteSpace(scriptId))
                return JsonDefaults.Error("'script_id' is required");

            var timeoutSeconds = args.Int("timeout_seconds", _options.DefaultExecutionTimeoutSeconds);
            timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 60);

            var request = new ScriptSandboxExecutionRequest
            {
                ScriptId = scriptId,
                Revision = args.Str("revision"),
                InputJson = args.RawOrStr("input"),
                TimeoutSeconds = timeoutSeconds,
            };

            var result = await _executionPort.ExecuteAsync(request, ct);

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                execution_id = result.ExecutionId,
                emitted_events = DeserializeOrNull(result.EmittedEventsJson),
                final_state = DeserializeOrNull(result.FinalStateJson),
                read_model = DeserializeOrNull(result.ReadModelJson),
                error = result.Error,
                duration_ms = result.DurationMs,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Execution failed: {ex.GetType().Name}");
        }
    }

    private static object? DeserializeOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return json; }
    }
}
