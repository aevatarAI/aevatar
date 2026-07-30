using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Runs the Codex CLI installed on the SSH host behind a NyxID-bound service.
/// NyxID owns routing to the node; the target host owns Codex authentication and configuration.
/// </summary>
public sealed class NyxIdCodexExecTool : IAgentTool
{
    private const int MaxPromptUtf8Bytes = 6_000;

    private readonly INyxIdSshCommandExecutor _executor;
    public NyxIdCodexExecTool(
        NyxIdApiClient client,
        NyxIdToolOptions? options = null,
        ILogger? logger = null)
        : this(new NyxIdSshCommandExecutor(client, logger), options ?? new NyxIdToolOptions())
    {
    }

    internal NyxIdCodexExecTool(
        INyxIdSshCommandExecutor executor,
        NyxIdToolOptions options)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        ArgumentNullException.ThrowIfNull(options);
    }

    public string Name => "codex_exec";

    public string Description =>
        "Run one non-interactive Codex CLI task on the host behind a NyxID-bound SSH service. " +
        "The service must route through a NyxID node to the machine where Codex is already " +
        "installed and authenticated. The command uses 'codex exec -' and leaves model, auth, " +
        "sandbox, and other Codex behavior to the target host's local configuration.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

    public bool IsDestructive => true;

    public string ParametersSchema => """
        {
          "type": "object",
          "required": ["service", "principal", "prompt"],
          "properties": {
            "service": {
              "type": "string",
              "description": "NyxID SSH service slug or UUID bound to the node/host where Codex is installed."
            },
            "principal": {
              "type": "string",
              "description": "Unix username whose home and local Codex configuration will be used."
            },
            "prompt": {
              "type": "string",
              "maxLength": 6000,
              "description": "Task prompt for codex exec. Maximum 6000 UTF-8 bytes."
            },
            "timeout_secs": {
              "type": "integer",
              "minimum": 1,
              "maximum": 300,
              "description": "NyxID SSH execution timeout. Defaults to 30 seconds and is capped at 300."
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return $"{{\"error\":\"Failed to parse tool arguments\",\"detail\":{JsonSerializer.Serialize(args.ParseError)}}}";

        var service = args.Str("service")?.Trim();
        var principal = args.Str("principal")?.Trim();
        var prompt = args.Str("prompt");
        if (string.IsNullOrWhiteSpace(service) ||
            string.IsNullOrWhiteSpace(principal) ||
            string.IsNullOrWhiteSpace(prompt))
        {
            return "{\"error\":\"'service', 'principal', and 'prompt' are required.\"}";
        }

        var promptBytes = Encoding.UTF8.GetBytes(prompt);
        if (promptBytes.Length > MaxPromptUtf8Bytes)
        {
            return JsonSerializer.Serialize(new
            {
                error = "prompt_too_large",
                detail = $"Prompt must not exceed {MaxPromptUtf8Bytes} UTF-8 bytes.",
                max_prompt_bytes = MaxPromptUtf8Bytes,
                actual_prompt_bytes = promptBytes.Length,
            });
        }

        return await _executor.ExecuteAsync(
            new NyxIdSshCommandRequest(
                service,
                principal,
                BuildCommand(promptBytes),
                args.Int("timeout_secs")),
            ct).ConfigureAwait(false);
    }

    private static string BuildCommand(ReadOnlySpan<byte> promptBytes)
    {
        var encodedPrompt = Convert.ToBase64String(promptBytes);
        return $"p='{encodedPrompt}'; {{ printf '%s' \"$p\" | base64 --decode 2>/dev/null || printf '%s' \"$p\" | base64 -D; }} | codex exec -";
    }
}
