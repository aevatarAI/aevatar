using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Execute shell commands on remote SSH-typed NyxID services. The HTTP proxy
/// (<see cref="NyxIdProxyTool"/>) cannot reach SSH endpoints; those services
/// are registered as <c>ssh://host:port</c> and require this dedicated tool.
/// </summary>
public sealed class NyxIdSshExecTool : INyxIdBuiltInTool
{
    private readonly INyxIdSshCommandExecutor _executor;
    public NyxIdSshExecTool(
        NyxIdApiClient client,
        NyxIdToolOptions? options = null,
        ILogger? logger = null)
        : this(new NyxIdSshCommandExecutor(client, logger), options ?? new NyxIdToolOptions())
    {
    }

    internal NyxIdSshExecTool(
        INyxIdSshCommandExecutor executor,
        NyxIdToolOptions options)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        ArgumentNullException.ThrowIfNull(options);
    }

    public string Name => "ssh_exec";

    public string Description =>
        "Execute a shell command on a remote SSH host via a NyxID-bound SSH service. " +
        "The target service must be SSH-typed (its endpoint starts with 'ssh://'); " +
        "HTTP services use 'nyxid_proxy' instead. Use 'nyxid_services' to discover " +
        "services and read their endpoint scheme; nyxid_proxy is invocation-only. " +
        "NyxID enforces an 8 KiB command length, a 1 MiB stdout/stderr cap, a 300s " +
        "timeout, and blocks dangerous commands (rm -rf /, mkfs, dd if=, fork bombs).";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

    public bool IsDestructive => true;

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
        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return $"{{\"error\":\"Failed to parse tool arguments\",\"detail\":{JsonSerializer.Serialize(args.ParseError)}}}";

        var service = args.Str("service") ?? args.Str("slug");
        var command = args.Str("command");
        var principal = args.Str("principal");
        if (string.IsNullOrWhiteSpace(service) ||
            string.IsNullOrWhiteSpace(command) ||
            string.IsNullOrWhiteSpace(principal))
        {
            return "{\"error\":\"'service', 'command', and 'principal' are required.\"}";
        }

        return await _executor.ExecuteAsync(
            new NyxIdSshCommandRequest(
                service,
                principal,
                command,
                args.Int("timeout_secs")),
            ct);
    }
}
