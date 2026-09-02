using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Routes a strongly typed Codex request to either a user-owned private SSH host or the
/// operator-managed chrono-sandbox service. Target-specific infrastructure stays behind
/// <see cref="ICodexExecutionPort"/>.
/// </summary>
public sealed class NyxIdCodexExecTool : INyxIdBuiltInTool
{
    private const int MaxPromptUtf8Bytes = 6_000;
    private const int DefaultPrivateSshTimeoutSeconds = 30;
    private const int MaxPrivateSshTimeoutSeconds = 300;
    private const int DefaultManagedTimeoutSeconds = 180;
    private const int MaxManagedTimeoutSeconds = 180;
    private const string GenericFailureErrorCode = "codex_exec_failed";

    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IReadOnlyList<ICodexExecutionPort> _ports;
    private readonly NyxIdToolOptions _options;
    public NyxIdCodexExecTool(
        NyxIdApiClient client,
        NyxIdToolOptions? options = null,
        ILogger? logger = null)
        : this(
            [new PrivateSshCodexExecutionAdapter(new NyxIdSshCommandExecutor(client, logger))],
            options ?? new NyxIdToolOptions())
    {
    }

    internal NyxIdCodexExecTool(
        INyxIdSshCommandExecutor executor,
        NyxIdToolOptions options)
        : this([new PrivateSshCodexExecutionAdapter(executor)], options)
    {
    }

    internal NyxIdCodexExecTool(
        IEnumerable<ICodexExecutionPort> ports,
        NyxIdToolOptions options)
    {
        _ports = ports?.ToArray() ?? throw new ArgumentNullException(nameof(ports));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_ports.Count == 0)
            throw new ArgumentException("At least one Codex execution port is required.", nameof(ports));
        if (_ports.GroupBy(static port => port.TargetKind).Any(static group => group.Count() != 1))
            throw new ArgumentException("Only one Codex execution port may be registered per target kind.", nameof(ports));
    }

    public string Name => "codex_exec";

    public string Description =>
        "Run one non-interactive Codex CLI task using either a private NyxID-backed SSH host " +
        "or the operator-managed isolated sandbox. Private SSH uses the host's local Codex " +
        "configuration. Managed sandbox accepts only an empty Git workspace and fixed runtime policy.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

    public bool? RequiresApproval(string argumentsJson)
    {
        var args = ToolArgs.Parse(argumentsJson);
        var target = args.Element("target");
        if (!TryReadString(target, "kind", out var kind))
            return true;

        return kind switch
        {
            "managed_sandbox" => false,
            "private_ssh" => true,
            _ => true,
        };
    }

    // The receipt may claim only terminal facts already verified in the result payload:
    // managed status=="succeeded", or a private SSH exit_code==0 that did not time out.
    // Anything ambiguous stays null so the shared receipt boundary keeps it Unknown.
    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var errorValue = error.GetString() ?? string.Empty;
                var detail = root.TryGetProperty("detail", out var detailElement) &&
                             detailElement.ValueKind == JsonValueKind.String
                    ? detailElement.GetString()
                    : null;
                return ErrorReceipt(
                    callId,
                    toolName,
                    IsStableErrorCode(errorValue) ? errorValue : GenericFailureErrorCode,
                    string.IsNullOrWhiteSpace(detail) ? errorValue : detail);
            }

            if (root.TryGetProperty("status", out var status))
            {
                return status.ValueKind == JsonValueKind.String &&
                       string.Equals(status.GetString(), "succeeded", StringComparison.Ordinal)
                    ? SuccessReceipt(callId, toolName)
                    : null;
            }

            if (!root.TryGetProperty("timed_out", out var timedOut) ||
                timedOut.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (timedOut.ValueKind == JsonValueKind.True)
            {
                return ErrorReceipt(
                    callId,
                    toolName,
                    "codex_exec_timed_out",
                    "Codex execution timed out on the SSH host.");
            }

            if (root.TryGetProperty("exit_code", out var exitCode) &&
                exitCode.ValueKind == JsonValueKind.Number &&
                exitCode.TryGetInt32(out var exitCodeValue))
            {
                return exitCodeValue == 0
                    ? SuccessReceipt(callId, toolName)
                    : ErrorReceipt(
                        callId,
                        toolName,
                        "codex_exec_nonzero_exit",
                        "Codex execution exited unsuccessfully on the SSH host.");
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public string ParametersSchema => """
        {
          "type": "object",
          "required": ["target", "prompt"],
          "additionalProperties": false,
          "properties": {
            "target": {
              "type": "object",
              "required": ["kind"],
              "additionalProperties": false,
              "properties": {
                "kind": {
                  "type": "string",
                  "enum": ["private_ssh", "managed_sandbox"]
                },
                "private_ssh": {
                  "type": "object",
                  "required": ["service", "principal"],
                  "additionalProperties": false,
                  "properties": {
                    "service": { "type": "string" },
                    "principal": { "type": "string" }
                  }
                }
              }
            },
            "workspace": {
              "type": "object",
              "required": ["kind"],
              "additionalProperties": false,
              "properties": {
                "kind": { "type": "string", "enum": ["empty_git"] }
              }
            },
            "prompt": {
              "type": "string",
              "maxLength": 6000
            },
            "timeout_secs": {
              "type": "integer",
              "minimum": 1,
              "maximum": 300
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var parsed = ParseRequest(argumentsJson);
        if (parsed.ErrorJson != null)
            return parsed.ErrorJson;

        var request = parsed.Request!;
        var port = _ports.SingleOrDefault(candidate => candidate.TargetKind == request.Target.TargetCase);
        if (port == null)
        {
            return ErrorJson(
                "target_not_configured",
                $"The '{TargetName(request.Target.TargetCase)}' codex_exec target is not enabled on this host.");
        }

        CodexExecutionResult? result = null;
        CodexExecutionFailure? failure = null;
        var terminalSeen = false;
        await foreach (var executionEvent in port.ExecuteAsync(request, ct).WithCancellation(ct))
        {
            if (terminalSeen)
                throw new InvalidOperationException("Codex execution port emitted an event after its terminal event.");

            switch (executionEvent.Kind)
            {
                case CodexExecutionEventKind.Started:
                case CodexExecutionEventKind.Output:
                    break;
                case CodexExecutionEventKind.Completed when executionEvent.Result != null:
                    result = executionEvent.Result;
                    terminalSeen = true;
                    break;
                case CodexExecutionEventKind.Failed when executionEvent.Failure != null:
                    failure = executionEvent.Failure;
                    terminalSeen = true;
                    break;
                default:
                    throw new InvalidOperationException("Codex execution port emitted an invalid event.");
            }
        }

        if (failure != null)
            throw new CodexExecutionException(failure);
        if (result == null)
            throw new InvalidOperationException("Codex execution port completed without a terminal result.");

        if (request.Target.TargetCase == CodexExecutionTarget.TargetOneofCase.PrivateSsh)
            return result.Output;

        return JsonSerializer.Serialize(new
        {
            status = "succeeded",
            target = "managed_sandbox",
            output = result.Output,
            exit_code = result.ExitCode,
            diagnostic_id = result.DiagnosticId,
            elapsed_ms = result.ElapsedMilliseconds,
        }, ResultJsonOptions);
    }

    private static ParsedCodexRequest ParseRequest(string argumentsJson)
    {
        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
        {
            return ParsedCodexRequest.Failure(
                ErrorJson("invalid_arguments", args.ParseError ?? "Failed to parse tool arguments."));
        }

        var prompt = args.Str("prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ParsedCodexRequest.Failure(ErrorJson("invalid_arguments", "'prompt' is required."));

        var promptBytes = Encoding.UTF8.GetByteCount(prompt);
        if (promptBytes > MaxPromptUtf8Bytes)
        {
            return ParsedCodexRequest.Failure(JsonSerializer.Serialize(new
            {
                error = "prompt_too_large",
                detail = $"Prompt must not exceed {MaxPromptUtf8Bytes} UTF-8 bytes.",
                max_prompt_bytes = MaxPromptUtf8Bytes,
                actual_prompt_bytes = promptBytes,
            }));
        }

        var targetElement = args.Element("target");
        if (!TryReadString(targetElement, "kind", out var kind))
            return ParsedCodexRequest.Failure(ErrorJson("invalid_target", "'target.kind' is required."));

        var timeout = args.Int("timeout_secs");
        if (args.Has("timeout_secs") && timeout == null)
            return ParsedCodexRequest.Failure(ErrorJson("invalid_timeout", "'timeout_secs' must be an integer."));

        return kind switch
        {
            "private_ssh" => ParsePrivateSshRequest(args, targetElement, prompt, timeout),
            "managed_sandbox" => ParseManagedSandboxRequest(args, targetElement, prompt, timeout),
            _ => ParsedCodexRequest.Failure(ErrorJson(
                "invalid_target",
                "'target.kind' must be 'private_ssh' or 'managed_sandbox'.")),
        };
    }

    private static ParsedCodexRequest ParsePrivateSshRequest(
        ToolArgs args,
        JsonElement? targetElement,
        string prompt,
        int? timeout)
    {
        if (args.Has("service") || args.Has("principal") || args.Has("workspace"))
        {
            return ParsedCodexRequest.Failure(ErrorJson(
                "mixed_target",
                "private_ssh fields must appear only under 'target.private_ssh', and workspace is managed-only."));
        }

        if (!TryReadObject(targetElement, "private_ssh", out var privateSsh) ||
            !TryReadString(privateSsh, "service", out var service) ||
            !TryReadString(privateSsh, "principal", out var principal))
        {
            return ParsedCodexRequest.Failure(ErrorJson(
                "invalid_target",
                "'target.private_ssh.service' and 'target.private_ssh.principal' are required."));
        }

        var timeoutSeconds = timeout ?? DefaultPrivateSshTimeoutSeconds;
        if (timeoutSeconds is < 1 or > MaxPrivateSshTimeoutSeconds)
        {
            return ParsedCodexRequest.Failure(ErrorJson(
                "invalid_timeout",
                "Private SSH timeout must be between 1 and 300 seconds."));
        }

        return Success(
            new CodexExecutionTarget
            {
                PrivateSsh = new CodexPrivateSshTarget
                {
                    Service = service,
                    Principal = principal,
                },
            },
            null,
            prompt,
            timeoutSeconds);
    }

    private static ParsedCodexRequest ParseManagedSandboxRequest(
        ToolArgs args,
        JsonElement? targetElement,
        string prompt,
        int? timeout)
    {
        if (TryGetProperty(targetElement, "private_ssh", out _))
            return ParsedCodexRequest.Failure(ErrorJson("mixed_target", "managed_sandbox cannot include private_ssh fields."));

        var workspaceElement = args.Element("workspace");
        if (!TryReadString(workspaceElement, "kind", out var workspaceKind) ||
            !string.Equals(workspaceKind, "empty_git", StringComparison.Ordinal))
        {
            return ParsedCodexRequest.Failure(ErrorJson(
                "invalid_workspace",
                "managed_sandbox requires workspace.kind='empty_git'."));
        }

        var timeoutSeconds = timeout ?? DefaultManagedTimeoutSeconds;
        if (timeoutSeconds is < 1 or > MaxManagedTimeoutSeconds)
        {
            return ParsedCodexRequest.Failure(ErrorJson(
                "invalid_timeout",
                "Managed sandbox timeout must be between 1 and 180 seconds."));
        }

        return Success(
            new CodexExecutionTarget { ManagedSandbox = new CodexManagedSandboxTarget() },
            new CodexExecutionWorkspace { EmptyGit = new CodexEmptyGitWorkspace() },
            prompt,
            timeoutSeconds);
    }

    private static ParsedCodexRequest Success(
        CodexExecutionTarget target,
        CodexExecutionWorkspace? workspace,
        string prompt,
        int timeoutSeconds)
    {
        var context = AgentToolRequestContext.Current;
        return ParsedCodexRequest.Success(new CodexExecutionRequest(
            target,
            workspace,
            prompt,
            timeoutSeconds,
            new CodexExecutionCallerContext(
                AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context?.Credentials),
                context?.NyxIdAuthority.IsComplete == true
                    ? new CodexExecutionNyxIdAuthority(
                        context.NyxIdAuthority.Platform!,
                        context.NyxIdAuthority.Tenant ?? string.Empty,
                        context.NyxIdAuthority.ExternalUserId!)
                    : null,
                context?.Caller.ScopeId,
                context?.WorkflowRuntime.RootRunId,
                context?.WorkflowRuntime.ParentStepId,
                context?.Request.CallId)));
    }

    private static bool TryReadObject(JsonElement? parent, string name, out JsonElement value)
    {
        if (TryGetProperty(parent, name, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static bool TryReadString(JsonElement? parent, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(parent, name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetProperty(JsonElement? parent, string name, out JsonElement value)
    {
        if (parent is { ValueKind: JsonValueKind.Object })
        {
            foreach (var property in parent.Value.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ErrorJson(string code, string detail) =>
        JsonSerializer.Serialize(new { error = code, detail });

    private static bool IsStableErrorCode(string value) =>
        value.Length is > 0 and <= 64 &&
        value.All(static ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private AgentToolReceipt SuccessReceipt(string callId, string toolName) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.Auto,
        };

    private AgentToolReceipt ErrorReceipt(
        string callId,
        string toolName,
        string errorCode,
        string errorMessage) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.Auto,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ResultJson = JsonSerializer.Serialize(new { error = errorCode, message = errorMessage }),
        };

    private static string TargetName(CodexExecutionTarget.TargetOneofCase target) =>
        target switch
        {
            CodexExecutionTarget.TargetOneofCase.PrivateSsh => "private_ssh",
            CodexExecutionTarget.TargetOneofCase.ManagedSandbox => "managed_sandbox",
            _ => "unspecified",
        };

    private sealed record ParsedCodexRequest(CodexExecutionRequest? Request, string? ErrorJson)
    {
        public static ParsedCodexRequest Success(CodexExecutionRequest request) => new(request, null);
        public static ParsedCodexRequest Failure(string errorJson) => new(null, errorJson);
    }
}
