using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Executes aevatar CLI commands directly from workflow steps.
/// Step type: aevatar_call (alias: aevatar).
/// </summary>
public sealed partial class AevatarCallModule : IEventModule
{
    private const string DefaultAevatarCli = "aevatar";

    public string Name => "aevatar_call";
    public int Priority => 9;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(SecureValueCapturedEvent.Descriptor) ||
                payload.Is(WorkflowCompletedEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(SecureValueCapturedEvent.Descriptor))
        {
            var captured = envelope.Payload.Unpack<SecureValueCapturedEvent>();
            if (!string.IsNullOrWhiteSpace(captured.Variable) && !string.IsNullOrEmpty(captured.Value))
                SecureValueRuntimeStore.Set(ctx.AgentId, captured.RunId, captured.Variable, captured.Value);
            return;
        }

        if (envelope.Payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
            SecureValueRuntimeStore.RemoveRun(ctx.AgentId, completed.RunId);
            return;
        }

        var request = envelope.Payload.Unpack<StepRequestEvent>();
        var canonicalStepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType);
        var isSecureStep = string.Equals(canonicalStepType, "secure_aevatar_call", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(canonicalStepType, Name, StringComparison.OrdinalIgnoreCase) && !isSecureStep)
            return;

        var cli = DefaultAevatarCli;
        IReadOnlyList<string> args = [];
        var timeoutMs = 30_000;
        var continueOnError = false;
        var sw = Stopwatch.StartNew();
        try
        {
            var argsRaw = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "args",
                "arguments");
            args = ParseArguments(argsRaw);

            timeoutMs = WorkflowParameterValueParser.GetBoundedInt(
                request.Parameters,
                30_000,
                100,
                300_000,
                "timeout_ms");

            var onError = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "fail",
                "on_error");
            continueOnError = string.Equals(onError, "continue", StringComparison.OrdinalIgnoreCase);

            var workingDirectory = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "working_directory",
                "cwd").Trim();

            var stdinMode = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "none",
                "stdin_mode",
                "stdin").Trim();
            var stdIn = ResolveStdIn(request, stdinMode, isSecureStep, ctx.AgentId);

            var command = await ExecuteCommandAsync(
                cli,
                args,
                stdIn,
                ExpandPath(workingDirectory),
                timeoutMs,
                ct);
            sw.Stop();

            if (command.ExitCode != 0)
            {
                var message = BuildCommandFailureMessage(command);
                if (continueOnError)
                {
                    await PublishContinueAsync(
                        ctx,
                        request,
                        cli,
                        args,
                        timeoutMs,
                        sw.Elapsed.TotalMilliseconds,
                        message,
                        ct);
                    return;
                }

                await PublishFailureAsync(
                    ctx,
                    request,
                    cli,
                    args,
                    timeoutMs,
                    sw.Elapsed.TotalMilliseconds,
                    message,
                    ct);
                return;
            }

            var output = string.IsNullOrWhiteSpace(command.StdOut)
                ? request.Input ?? string.Empty
                : command.StdOut.Trim();
            var completed = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = true,
                Output = output,
            };
            AppendBaseMetadata(
                completed,
                cli,
                args,
                timeoutMs,
                command.ExitCode,
                sw.Elapsed.TotalMilliseconds,
                command.StdErr);

            await ctx.PublishAsync(completed, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (continueOnError)
            {
                await PublishContinueAsync(
                    ctx,
                    request,
                    cli,
                    args,
                    timeoutMs,
                    sw.Elapsed.TotalMilliseconds,
                    ex.Message,
                    ct);
                return;
            }

            await PublishFailureAsync(
                ctx,
                request,
                cli,
                args,
                timeoutMs,
                sw.Elapsed.TotalMilliseconds,
                ex.Message,
                ct);
        }
    }

    private static async Task<CommandExecutionResult> ExecuteCommandAsync(
        string cli,
        IReadOnlyList<string> args,
        string? stdIn,
        string? workingDirectory,
        int timeoutMs,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cli,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdIn != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start command: {cli}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (stdIn != null)
        {
            await process.StandardInput.WriteAsync(stdIn);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw new TimeoutException($"aevatar_call timed out after {timeoutMs}ms");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new CommandExecutionResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static string BuildCommandFailureMessage(CommandExecutionResult command)
    {
        var stderr = string.IsNullOrWhiteSpace(command.StdErr) ? string.Empty : command.StdErr.Trim();
        var stdout = string.IsNullOrWhiteSpace(command.StdOut) ? string.Empty : command.StdOut.Trim();
        return !string.IsNullOrWhiteSpace(stderr)
            ? $"aevatar_call failed (exit_code={command.ExitCode}): {stderr}"
            : !string.IsNullOrWhiteSpace(stdout)
                ? $"aevatar_call failed (exit_code={command.ExitCode}): {stdout}"
                : $"aevatar_call failed (exit_code={command.ExitCode})";
    }

    private static async Task PublishContinueAsync(
        IEventHandlerContext ctx,
        StepRequestEvent request,
        string cli,
        IReadOnlyList<string> args,
        int timeoutMs,
        double durationMs,
        string error,
        CancellationToken ct)
    {
        var continued = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = request.Input ?? string.Empty,
        };
        AppendBaseMetadata(continued, cli, args, timeoutMs, -1, durationMs, error);
        continued.Metadata["aevatar.continued_on_error"] = "true";
        continued.Metadata["aevatar.error"] = error;
        await ctx.PublishAsync(continued, EventDirection.Self, ct);
    }

    private static async Task PublishFailureAsync(
        IEventHandlerContext ctx,
        StepRequestEvent request,
        string cli,
        IReadOnlyList<string> args,
        int timeoutMs,
        double durationMs,
        string error,
        CancellationToken ct)
    {
        var failed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = error,
        };
        AppendBaseMetadata(failed, cli, args, timeoutMs, -1, durationMs, string.Empty);
        await ctx.PublishAsync(failed, EventDirection.Self, ct);
    }

    private static void AppendBaseMetadata(
        StepCompletedEvent evt,
        string cli,
        IReadOnlyList<string> args,
        int timeoutMs,
        int exitCode,
        double durationMs,
        string stderr)
    {
        evt.Metadata["aevatar.cli"] = cli;
        evt.Metadata["aevatar.args"] = string.Join(" ", args);
        evt.Metadata["aevatar.timeout_ms"] = timeoutMs.ToString();
        evt.Metadata["aevatar.exit_code"] = exitCode.ToString();
        evt.Metadata["aevatar.duration_ms"] = durationMs.ToString("F2");
        if (!string.IsNullOrWhiteSpace(stderr))
            evt.Metadata["aevatar.stderr"] = stderr.Trim();
    }

    private static List<string> ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
            trimmed.EndsWith("]", StringComparison.Ordinal) &&
            TryParseJsonArray(trimmed, out var parsed))
        {
            return parsed;
        }

        return ParseQuotedTokens(trimmed);
    }

    private static bool TryParseJsonArray(string json, out List<string> args)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                args = [];
                return false;
            }

            args = [];
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null)
                    continue;

                var text = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.GetRawText();
                if (!string.IsNullOrWhiteSpace(text))
                    args.Add(text.Trim());
            }

            return true;
        }
        catch
        {
            args = [];
            return false;
        }
    }

    private static List<string> ParseQuotedTokens(string raw)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var escaped = false;

        foreach (var ch in raw)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && !inSingle)
            {
                escaped = true;
                continue;
            }

            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inSingle && !inDouble)
            {
                FlushToken(current, result);
                continue;
            }

            current.Append(ch);
        }

        FlushToken(current, result);
        return result;
    }

    private static void FlushToken(StringBuilder current, List<string> result)
    {
        if (current.Length == 0)
            return;

        var token = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(token))
            result.Add(token);
        current.Clear();
    }

    private string? ResolveStdIn(
        StepRequestEvent request,
        string mode,
        bool isSecureStep,
        string? agentId)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return null;

        if (string.Equals(mode, "input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "pass_input", StringComparison.OrdinalIgnoreCase))
        {
            return request.Input ?? string.Empty;
        }

        if (!isSecureStep)
            return null;

        if (string.Equals(mode, "secure_variable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secret_variable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secure_input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secret_input", StringComparison.OrdinalIgnoreCase))
        {
            var variable = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "stdin_secret_variable",
                "secret_variable",
                "secure_variable",
                "variable");
            return ResolveSecureVariable(agentId, request.RunId, variable);
        }

        if (string.Equals(mode, "template", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secure_template", StringComparison.OrdinalIgnoreCase))
        {
            var template = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "stdin_template",
                "stdin_value");
            return ResolveSecureTemplate(agentId, request.RunId, template);
        }

        return null;
    }

    private static string ResolveSecureVariable(string? agentId, string? runId, string variable)
    {
        var normalizedVariable = NormalizeSecureVariableName(variable);
        if (string.IsNullOrWhiteSpace(normalizedVariable))
            throw new InvalidOperationException("secure_aevatar_call requires 'stdin_secret_variable'.");

        if (SecureValueRuntimeStore.TryGet(agentId, runId, normalizedVariable, out var value))
            return value;

        throw new InvalidOperationException(
            $"secure_aevatar_call is missing captured secure value '{normalizedVariable}' for run '{WorkflowRunIdNormalizer.Normalize(runId)}'.");
    }

    private static string ResolveSecureTemplate(string? agentId, string? runId, string template)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        var withJsonEscapedSecureValues = SecureJsonPlaceholderPattern().Replace(template, match =>
        {
            var variable = match.Groups[1].Value;
            var value = ResolveSecureVariable(agentId, runId, variable);
            return JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
        });

        return SecurePlaceholderPattern().Replace(withJsonEscapedSecureValues, match =>
        {
            var variable = match.Groups[1].Value;
            return ResolveSecureVariable(agentId, runId, variable);
        });
    }

    private static string NormalizeSecureVariableName(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();

    [GeneratedRegex(@"\[\[secure:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecurePlaceholderPattern();

    [GeneratedRegex(@"\[\[secure_json:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecureJsonPlaceholderPattern();

    private static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('~'))
            return Environment.ExpandEnvironmentVariables(trimmed);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (trimmed.Length == 1)
            return home;

        var relative = trimmed[1] switch
        {
            '/' or '\\' => trimmed[2..],
            _ => trimmed[1..],
        };
        return Path.Combine(home, relative);
    }

    private sealed record CommandExecutionResult(
        int ExitCode,
        string StdOut,
        string StdErr);
}
