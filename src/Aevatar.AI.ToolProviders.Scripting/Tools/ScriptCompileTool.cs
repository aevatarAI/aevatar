using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Scripting.Ports;

namespace Aevatar.AI.ToolProviders.Scripting.Tools;

/// <summary>
/// Compiles C# source code into a script behavior artifact via Roslyn.
/// Returns compilation diagnostics, sandbox violations, and discovered contract metadata.
/// </summary>
public sealed class ScriptCompileTool : IAgentTool
{
    private readonly IScriptToolCompilationAdapter _compilationPort;
    private readonly ScriptingToolOptions _options;

    public ScriptCompileTool(IScriptToolCompilationAdapter compilationPort, ScriptingToolOptions options)
    {
        _compilationPort = compilationPort;
        _options = options;
    }

    public string Name => "script_compile";

    public string Description =>
        "Compile C# source code into a script behavior. " +
        "Provide source files as a dictionary of filename->content. " +
        "Returns compilation result with diagnostics, discovered commands, events, and state/readmodel type URLs. " +
        "The compiled revision can then be executed (script_execute) or promoted (script_promote). " +
        "Scripts must implement IScriptBehaviorBridge via the ScriptBehavior<TState,TReadModel> base class.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "script_id": {
              "type": "string",
              "description": "Unique script identifier. Reuse an existing ID to create a new revision."
            },
            "source_files": {
              "type": "object",
              "description": "C# source files keyed by filename (e.g. {\"MyBehavior.cs\": \"using ...;\"})",
              "additionalProperties": { "type": "string" }
            },
            "proto_files": {
              "type": "object",
              "description": "Optional protobuf files keyed by filename",
              "additionalProperties": { "type": "string" }
            }
          },
          "required": ["script_id", "source_files"]
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
            var sourceFiles = args.StrDict("source_files");

            if (string.IsNullOrWhiteSpace(scriptId))
                return JsonDefaults.Error("'script_id' is required");
            if (sourceFiles == null || sourceFiles.Count == 0)
                return JsonDefaults.Error("'source_files' must contain at least one C# file");

            var totalChars = sourceFiles.Values.Sum(v => v.Length);
            if (totalChars > _options.MaxSourceSizeChars)
                return JsonDefaults.Error(
                    $"Total source size ({totalChars} chars) exceeds limit ({_options.MaxSourceSizeChars} chars)");

            var request = new ScriptCompilationRequest
            {
                ScriptId = scriptId,
                SourceFiles = sourceFiles,
                ProtoFiles = args.StrDict("proto_files"),
            };

            var result = await _compilationPort.CompileAsync(request, ct);

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                script_id = result.ScriptId,
                revision = result.Revision,
                diagnostics = result.Diagnostics,
                sandbox_violations = result.SandboxViolations,
                state_type_url = result.StateTypeUrl,
                read_model_type_url = result.ReadModelTypeUrl,
                command_type_urls = result.CommandTypeUrls,
                domain_event_type_urls = result.DomainEventTypeUrls,
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return JsonDefaults.Error($"Compilation failed: {ex.GetType().Name}");
        }
    }
}
