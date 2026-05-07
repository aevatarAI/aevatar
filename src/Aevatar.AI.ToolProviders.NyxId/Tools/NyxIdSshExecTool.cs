using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Execute shell commands on remote SSH-typed NyxID services. The HTTP proxy
/// (<see cref="NyxIdProxyTool"/>) cannot reach SSH endpoints — those services
/// are registered as <c>ssh://host:port</c> and require this dedicated tool.
/// </summary>
public sealed class NyxIdSshExecTool : IAgentTool
{
    private const int DefaultTimeoutSecs = 30;
    private const int MaxTimeoutSecs = 300;

    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public NyxIdSshExecTool(NyxIdApiClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name => "ssh_exec";

    public string Description =>
        "Execute a shell command on a remote SSH host via a NyxID-bound SSH service. " +
        "The target service must be SSH-typed (its endpoint starts with 'ssh://'); " +
        "HTTP services use 'nyxid_proxy' instead. Use 'nyxid_proxy' (no slug) or " +
        "'nyxid_services' to discover services and read their endpoint scheme. " +
        "NyxID enforces an 8 KiB command length, a 1 MiB stdout/stderr cap, a 300s " +
        "timeout, and blocks dangerous commands (rm -rf /, mkfs, dd if=, fork bombs).";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    /// <summary>SSH execution can mutate the remote host arbitrarily; always request approval.</summary>
    public bool? RequiresApproval(string argumentsJson) => true;

    public string ParametersSchema => """
        {
          "type": "object",
          "required": ["service", "command", "principal"],
          "properties": {
            "service": {
              "type": "string",
              "description": "NyxID service slug or UUID. Must be SSH-typed (endpoint scheme 'ssh://')."
            },
            "command": {
              "type": "string",
              "description": "Shell command to run on the remote host. Max 8192 chars."
            },
            "principal": {
              "type": "string",
              "description": "Unix username on the remote host (e.g. 'ubuntu', 'root')."
            },
            "timeout_secs": {
              "type": "integer",
              "minimum": 1,
              "maximum": 300,
              "description": "Max execution time in seconds. Defaults to 30, capped at 300."
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return $"{{\"error\":\"Failed to parse tool arguments\",\"detail\":{JsonSerializer.Serialize(args.ParseError)}}}";

        var service = args.Str("service") ?? args.Str("slug");
        var command = args.Str("command");
        var principal = args.Str("principal");
        var timeoutSecs = ParseTimeoutSecs(args.Str("timeout_secs"));

        if (string.IsNullOrWhiteSpace(service) ||
            string.IsNullOrWhiteSpace(command) ||
            string.IsNullOrWhiteSpace(principal))
        {
            return """{"error":"'service', 'command', and 'principal' are required."}""";
        }

        _logger.LogInformation(
            "[ssh_exec] service={Service} principal={Principal} timeoutSecs={Timeout}",
            service, principal, timeoutSecs);

        var body = JsonSerializer.Serialize(new
        {
            command,
            principal,
            timeout_secs = timeoutSecs,
        });

        return await _client.SshExecAsync(token, service, body, ct);
    }

    private static int ParseTimeoutSecs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultTimeoutSecs;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            return DefaultTimeoutSecs;
        }
        return Math.Clamp(v, 1, MaxTimeoutSecs);
    }
}
