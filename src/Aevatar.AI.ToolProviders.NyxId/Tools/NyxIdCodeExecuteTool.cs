using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Executes caller-provided source through the runtime-neutral code execution boundary.
/// </summary>
public sealed class NyxIdCodeExecuteTool(ICodeExecutionPort executionPort) : INyxIdBuiltInTool
{
    private static readonly HashSet<string> CompletedFailureCodes = new(StringComparer.Ordinal)
    {
        "code_execution_failed",
        "DEPENDENCY_INSTALL_FAILED",
        "EXECUTION_FAILED",
    };

    private static readonly HashSet<string> PreExecutionFailureCodes = new(StringComparer.Ordinal)
    {
        "code_execution_credential_unavailable",
        "code_execution_outcome_invalid",
        "code_execution_request_invalid",
        "code_execution_response_invalid",
        "code_execution_response_too_large",
        "code_execution_route_resolution_failed",
        "code_execution_route_unavailable",
        "code_execution_timed_out",
        "code_execution_transport_unavailable",
        "INTERNAL_ERROR",
        "INVALID_REQUEST",
        "NYXID_PROXY_FORBIDDEN",
        "NYXID_PROXY_HTTP_404",
        "NYXID_PROXY_HTTP_429",
        "NYXID_PROXY_HTTP_502",
        "NYXID_PROXY_UNAUTHORIZED",
        "SANDBOX_CREATION_FAILED",
        "SANDBOX_TIMEOUT",
        "SANDBOX_UNREACHABLE",
    };

    private readonly ICodeExecutionPort _executionPort =
        executionPort ?? throw new ArgumentNullException(nameof(executionPort));

    public string Name => "code_execute";

    public string Description =>
        "Execute caller-provided exact source code in a one-shot remote code runtime. " +
        "Supports Python, JavaScript, TypeScript, and Bash. " +
        "Returns stdout, stderr, and exit code. " +
        "Use it for an explicit program; delegate a natural-language task to codex_exec instead.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public bool IsReadOnly => true;

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "language": {
              "type": "string",
              "enum": ["python", "javascript", "typescript", "bash"],
              "description": "Programming language to execute"
            },
            "code": {
              "type": "string",
              "description": "Code to execute"
            }
          },
          "required": ["language", "code"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        (await ExecuteCoreAsync(string.Empty, Name, argumentsJson, ct).ConfigureAwait(false)).ResultJson;

    public Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default) =>
        ExecuteCoreAsync(callId, toolName, argumentsJson, ct);

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
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (success.GetBoolean())
            {
                return TryReadResult(root, out var result) && result.ExitCode == 0
                    ? SuccessReceipt(callId, toolName, resultJson, userServiceId: null)
                    : null;
            }

            if (!TryReadNonEmptyString(root, "error", out var error) ||
                !TryReadNonEmptyString(root, "code", out var code) ||
                !TryReadNonEmptyString(root, "message", out var message) ||
                !string.Equals(error, code, StringComparison.Ordinal))
            {
                return null;
            }

            if (CompletedFailureCodes.Contains(code))
            {
                if (!TryReadResult(root, out var failedResult) || failedResult.ExitCode == 0)
                    return null;
            }
            else if (!PreExecutionFailureCodes.Contains(code) || root.TryGetProperty("output", out _))
            {
                return null;
            }

            return FailureReceipt(callId, toolName, resultJson, code, message, userServiceId: null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<AgentToolTerminalOutcome> ExecuteCoreAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        var args = ToolArgs.Parse(argumentsJson);
        var language = args.Str("language");
        var source = args.Str("code");
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(source))
        {
            return TerminalFailure(
                callId,
                toolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_request_invalid",
                    "Both 'language' and 'code' are required."));
        }

        if (!TryParseLanguage(language, out var codeLanguage))
        {
            return TerminalFailure(
                callId,
                toolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_request_invalid",
                    "Language must be one of: python, javascript, typescript, bash."));
        }

        var bearerToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return TerminalFailure(
                callId,
                toolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_credential_unavailable",
                    "A source-readable NyxID credential is required for code execution."));
        }

        var outcome = await _executionPort.ExecuteAsync(
                new CodeExecutionRequest(
                    codeLanguage,
                    source,
                    new CodeExecutionRouteIdentity(
                        CodeExecutionContract.ServiceSlug,
                        UserServiceId: null,
                        CodeExecutionRouteIdentitySource.CodeExecutionContract),
                    new CodeExecutionCallerContext(bearerToken)),
                ct)
            .ConfigureAwait(false);
        return Terminal(callId, toolName, outcome);
    }

    private static bool TryParseLanguage(string language, out CodeExecutionLanguage result)
    {
        result = language.Trim() switch
        {
            "python" => CodeExecutionLanguage.Python,
            "javascript" => CodeExecutionLanguage.JavaScript,
            "typescript" => CodeExecutionLanguage.TypeScript,
            "bash" => CodeExecutionLanguage.Bash,
            _ => CodeExecutionLanguage.Unspecified,
        };
        return result != CodeExecutionLanguage.Unspecified;
    }

    private static AgentToolTerminalOutcome Terminal(
        string callId,
        string toolName,
        CodeExecutionOutcome outcome)
    {
        if (!IsValidOutcome(outcome))
        {
            return TerminalFailure(
                callId,
                toolName,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.MalformedOutput,
                    "code_execution_outcome_invalid",
                    "Code execution returned an invalid outcome."));
        }

        var resultJson = SerializeOutcome(outcome);
        var userServiceId = outcome.ResolvedRoute?.UserServiceId;
        var receipt = outcome.Failure is null
            ? SuccessReceipt(callId, toolName, resultJson, userServiceId)
            : FailureReceipt(
                callId,
                toolName,
                resultJson,
                outcome.Failure.Code,
                outcome.Failure.Message,
                userServiceId);
        return new AgentToolTerminalOutcome(resultJson, receipt);
    }

    private static AgentToolTerminalOutcome TerminalFailure(
        string callId,
        string toolName,
        CodeExecutionFailure failure) =>
        Terminal(callId, toolName, CodeExecutionOutcome.Failed(failure));

    private static bool IsValidOutcome(CodeExecutionOutcome outcome)
    {
        if (outcome.Result is null)
            return outcome.Failure is not null;
        if (outcome.Failure is null)
            return outcome.Result.ExitCode == 0 && outcome.ResolvedRoute is not null;

        return outcome.Result.ExitCode != 0 &&
               outcome.Failure.Kind == CodeExecutionFailureKind.ExecutionFailed &&
               outcome.ResolvedRoute is not null;
    }

    private static AgentToolReceipt SuccessReceipt(
        string callId,
        string toolName,
        string resultJson,
        string? userServiceId) =>
        NyxIdProxyReceiptFactory.CreateSuccess(callId, toolName, userServiceId, resultJson) ??
        new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? "code_execute" : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ResultJson = resultJson,
        };

    private static AgentToolReceipt FailureReceipt(
        string callId,
        string toolName,
        string resultJson,
        string errorCode,
        string errorMessage,
        string? userServiceId) =>
        NyxIdProxyReceiptFactory.CreateError(
            callId,
            string.IsNullOrWhiteSpace(toolName) ? "code_execute" : toolName,
            userServiceId,
            errorCode,
            errorMessage,
            resultJson);

    private static string SerializeOutcome(CodeExecutionOutcome outcome)
    {
        var result = outcome.Result;
        var failure = outcome.Failure;
        if (failure is null && result is not null)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                output = Output(result),
            });
        }

        if (result is not null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                output = Output(result),
                error = failure!.Code,
                code = failure.Code,
                message = failure.Message,
                diagnostic_id = failure.DiagnosticId,
            });
        }

        return JsonSerializer.Serialize(new
        {
            success = false,
            error = failure!.Code,
            code = failure.Code,
            message = failure.Message,
            diagnostic_id = failure.DiagnosticId,
        });
    }

    private static object Output(CodeExecutionResult result) => new
    {
        stdout = result.Stdout,
        stderr = result.Stderr,
        exit_code = result.ExitCode,
        diagnostic_id = result.DiagnosticId,
        execution_time_ms = result.ElapsedMilliseconds,
    };

    private static bool TryReadResult(JsonElement root, out CodeExecutionResult result)
    {
        result = new CodeExecutionResult(string.Empty, string.Empty, 0);
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Object ||
            !TryReadString(output, "stdout", out var stdout) ||
            !TryReadString(output, "stderr", out var stderr) ||
            !output.TryGetProperty("exit_code", out var exitCode) ||
            !exitCode.TryGetInt32(out var exitCodeValue))
        {
            return false;
        }

        result = new CodeExecutionResult(stdout, stderr, exitCodeValue);
        return true;
    }

    private static bool TryReadString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadNonEmptyString(JsonElement owner, string name, out string value) =>
        TryReadString(owner, name, out value) && !string.IsNullOrWhiteSpace(value);
}
