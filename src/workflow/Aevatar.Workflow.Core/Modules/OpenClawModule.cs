using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Executes OpenClaw CLI commands directly from workflow steps (without connector config).
/// Step type: openclaw_call (alias: openclaw).
/// </summary>
public sealed class OpenClawModule : IEventModule
{
    internal const string OpenClawCliPathEnv = "AEVATAR_OPENCLAW_CLI_PATH";
    private const string DefaultOpenClawCli = "openclaw";
    private static readonly Regex MediaTokenRegex =
        new(@"MEDIA:(?<path>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BrowserProfileNotFoundRegex =
        new(@"profile\s+[""']?(?<name>[^""'\r\n]+)[""']?\s+not\s+found", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly CommandExecutionOutcome NoRecoveryOutcome =
        CommandExecutionOutcome.NoRecovery(new CommandExecutionResult(-1, string.Empty, string.Empty));

    public string Name => "openclaw_call";
    public int Priority => 9;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (!string.Equals(request.StepType, "openclaw_call", StringComparison.OrdinalIgnoreCase))
            return;
        var cli = DefaultOpenClawCli;
        IReadOnlyList<string> args = [];
        var timeoutMs = 30_000;
        var continueOnError = false;
        var sw = Stopwatch.StartNew();
        try
        {
            cli = ResolveOpenClawCli(request.Parameters);

            var argsRaw = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "args",
                "arguments");
            args = NormalizeLegacyBrowserOpenArgs(ParseArguments(argsRaw));

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

            var saveMediaTo = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "save_media_to",
                "save_to",
                "save_dir").Trim();

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
            var stdIn = ResolveStdIn(stdinMode, request.Input);

            var commandOutcome = await ExecuteCommandWithProfileRecoveryAsync(
                cli,
                args,
                stdIn,
                ExpandPath(workingDirectory),
                timeoutMs,
                ct);
            var command = commandOutcome.Result;
            sw.Stop();

            if (command.ExitCode != 0)
            {
                var message = BuildCommandFailureMessage(command, commandOutcome);
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
                        commandOutcome,
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
                    commandOutcome,
                    ct);
                return;
            }

            var output = string.IsNullOrWhiteSpace(command.StdOut) ? request.Input ?? string.Empty : command.StdOut.Trim();
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
                command.StdErr,
                commandOutcome);

            if (!string.IsNullOrWhiteSpace(saveMediaTo))
            {
                if (!TryExtractMediaPath(command.StdOut, out var sourceMediaPath))
                {
                    await PublishFailureAsync(
                        ctx,
                        request,
                        cli,
                        args,
                        timeoutMs,
                        sw.Elapsed.TotalMilliseconds,
                        "openclaw_call did not return a media path; cannot persist screenshot.",
                        commandOutcome,
                        ct);
                    return;
                }

                var destinationDirectory = ExpandPath(saveMediaTo);
                var savedPath = CopyMediaToDirectory(sourceMediaPath, destinationDirectory);
                completed.Output = savedPath;
                completed.Metadata["openclaw.media_source_path"] = sourceMediaPath;
                completed.Metadata["openclaw.saved_path"] = savedPath;
                completed.Metadata["openclaw.save_media_to"] = destinationDirectory;
            }

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
                    NoRecoveryOutcome,
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
                NoRecoveryOutcome,
                ct);
        }
    }

    private static async Task<CommandExecutionOutcome> ExecuteCommandWithProfileRecoveryAsync(
        string cli,
        IReadOnlyList<string> args,
        string? stdIn,
        string? workingDirectory,
        int timeoutMs,
        CancellationToken ct)
    {
        var initial = await ExecuteCommandAsync(cli, args, stdIn, workingDirectory, timeoutMs, ct);
        if (initial.ExitCode == 0)
            return CommandExecutionOutcome.NoRecovery(initial);

        if (!TryGetBrowserProfileFromArgs(args, out var browserProfile))
            return CommandExecutionOutcome.NoRecovery(initial);

        if (!ContainsProfileNotFoundSignal(initial, browserProfile))
            return CommandExecutionOutcome.NoRecovery(initial);

        var createArgs = BuildCreateProfileArgs(browserProfile);
        var createTimeoutMs = Math.Min(timeoutMs, 20_000);
        var create = await ExecuteCommandAsync(cli, createArgs, stdIn: null, workingDirectory, createTimeoutMs, ct);
        if (create.ExitCode != 0 && !ContainsProfileAlreadyExistsSignal(create))
        {
            var detail = BuildCommandFailureMessage(create, recovery: null);
            return CommandExecutionOutcome.RecoveryAttempted(
                initial,
                browserProfile,
                profileRecoverySucceeded: false,
                detail);
        }

        var retried = await ExecuteCommandAsync(cli, args, stdIn, workingDirectory, timeoutMs, ct);
        if (retried.ExitCode == 0)
            return CommandExecutionOutcome.RecoveryAttempted(
                retried,
                browserProfile,
                profileRecoverySucceeded: true,
                recoveryDetail: "browser profile auto-created and command retried successfully");

        return CommandExecutionOutcome.RecoveryAttempted(
            retried,
            browserProfile,
            profileRecoverySucceeded: false,
            recoveryDetail: "browser profile auto-created, but retried command still failed");
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
            throw new TimeoutException($"openclaw_call timed out after {timeoutMs}ms");
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

    private static string ResolveOpenClawCli(IReadOnlyDictionary<string, string> parameters)
    {
        var requestedCli = WorkflowParameterValueParser.GetString(
            parameters,
            string.Empty,
            "openclaw",
            "cli",
            "command").Trim();
        ValidateOpenClawExecutableIdentity(
            requestedCli,
            "Step parameters 'openclaw/cli/command'",
            allowEmpty: true);

        var overriddenCli = Environment.GetEnvironmentVariable(OpenClawCliPathEnv);
        if (!string.IsNullOrWhiteSpace(overriddenCli))
        {
            return ValidateOpenClawExecutableIdentity(
                overriddenCli,
                $"Environment variable '{OpenClawCliPathEnv}'");
        }

        return DefaultOpenClawCli;
    }

    private static string ValidateOpenClawExecutableIdentity(
        string? rawValue,
        string source,
        bool allowEmpty = false)
    {
        var normalized = rawValue?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            if (allowEmpty)
                return string.Empty;

            throw new InvalidOperationException(
                $"{source} must reference the OpenClaw CLI executable.");
        }

        if (IsOpenClawExecutable(normalized))
            return normalized;

        throw new InvalidOperationException(
            $"{source} must reference the OpenClaw CLI executable. Use '{DefaultOpenClawCli}' or a path whose executable name is '{DefaultOpenClawCli}'.");
    }

    private static bool IsOpenClawExecutable(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
            return false;

        if (string.Equals(normalized, DefaultOpenClawCli, StringComparison.OrdinalIgnoreCase))
            return true;

        var fileName = Path.GetFileNameWithoutExtension(normalized);
        return string.Equals(fileName, DefaultOpenClawCli, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetBrowserProfileFromArgs(IReadOnlyList<string> args, out string browserProfile)
    {
        browserProfile = string.Empty;
        if (args.Count < 2)
            return false;

        if (!string.Equals(args[0], "browser", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(args[1], "create-profile", StringComparison.OrdinalIgnoreCase))
            return false;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (string.Equals(token, "--browser-profile", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                    break;

                browserProfile = TrimProfileToken(args[i + 1]);
                break;
            }

            if (token.StartsWith("--browser-profile=", StringComparison.OrdinalIgnoreCase))
            {
                browserProfile = TrimProfileToken(token["--browser-profile=".Length..]);
                break;
            }
        }

        return !string.IsNullOrWhiteSpace(browserProfile);
    }

    private static string TrimProfileToken(string token) =>
        token.Trim().Trim('"', '\'');

    private static bool ContainsProfileNotFoundSignal(CommandExecutionResult command, string browserProfile)
    {
        var detail = $"{command.StdErr}\n{command.StdOut}";
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        // OpenClaw versions may prepend repeated "Error: " wrappers; keep detection tolerant.
        if (detail.Contains("profile", StringComparison.OrdinalIgnoreCase) &&
            detail.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
            detail.Contains(browserProfile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var match = BrowserProfileNotFoundRegex.Match(detail);
        if (!match.Success)
            return false;

        var matchedProfile = TrimProfileToken(match.Groups["name"].Value);
        return string.IsNullOrWhiteSpace(matchedProfile) ||
               string.Equals(matchedProfile, browserProfile, StringComparison.OrdinalIgnoreCase) ||
               detail.Contains(browserProfile, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsProfileAlreadyExistsSignal(CommandExecutionResult command)
    {
        var detail = $"{command.StdErr}\n{command.StdOut}";
        return detail.Contains("profile", StringComparison.OrdinalIgnoreCase) &&
               detail.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> BuildCreateProfileArgs(string browserProfile) =>
    [
        "browser",
        "create-profile",
        "--name",
        browserProfile,
        "--json",
    ];

    private static List<string> NormalizeLegacyBrowserOpenArgs(IReadOnlyList<string> args)
    {
        if (args.Count < 3 ||
            !string.Equals(args[0], "browser", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args[1], "open", StringComparison.OrdinalIgnoreCase))
        {
            return [.. args];
        }

        var hasLegacyOptions = false;
        string? browserProfile = null;
        var includeJson = false;
        string? url = null;
        for (var i = 2; i < args.Count; i++)
        {
            var token = args[i];
            if (string.Equals(token, "--browser-profile", StringComparison.OrdinalIgnoreCase))
            {
                hasLegacyOptions = true;
                if (i + 1 < args.Count)
                {
                    browserProfile = TrimProfileToken(args[i + 1]);
                    i += 1;
                }

                continue;
            }

            if (token.StartsWith("--browser-profile=", StringComparison.OrdinalIgnoreCase))
            {
                hasLegacyOptions = true;
                browserProfile = TrimProfileToken(token["--browser-profile=".Length..]);
                continue;
            }

            if (string.Equals(token, "--json", StringComparison.OrdinalIgnoreCase))
            {
                hasLegacyOptions = true;
                includeJson = true;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
                continue;

            if (url == null)
                url = token;
        }

        if (!hasLegacyOptions || string.IsNullOrWhiteSpace(url))
            return [.. args];

        return
        [
            "browser",
            .. BuildBrowserOpenPrefix(browserProfile, includeJson),
            "open",
            url,
        ];
    }

    private static List<string> BuildBrowserOpenPrefix(string? browserProfile, bool includeJson)
    {
        var prefix = new List<string>();
        if (!string.IsNullOrWhiteSpace(browserProfile))
        {
            prefix.Add("--browser-profile");
            prefix.Add(browserProfile);
        }

        if (includeJson)
            prefix.Add("--json");

        return prefix;
    }

    private static string BuildCommandFailureMessage(
        CommandExecutionResult command,
        CommandExecutionOutcome? recovery)
    {
        var stderr = string.IsNullOrWhiteSpace(command.StdErr) ? string.Empty : command.StdErr.Trim();
        var stdout = string.IsNullOrWhiteSpace(command.StdOut) ? string.Empty : command.StdOut.Trim();
        var baseMessage =
            !string.IsNullOrWhiteSpace(stderr)
                ? $"openclaw_call failed (exit_code={command.ExitCode}): {stderr}"
                : !string.IsNullOrWhiteSpace(stdout)
                    ? $"openclaw_call failed (exit_code={command.ExitCode}): {stdout}"
                    : $"openclaw_call failed (exit_code={command.ExitCode})";

        if (recovery is not { ProfileRecoveryAttempted: true })
            return baseMessage;

        if (recovery.ProfileRecoverySucceeded)
            return $"{baseMessage} (after profile auto-recovery for \"{recovery.BrowserProfile}\")";

        var detail = string.IsNullOrWhiteSpace(recovery.RecoveryDetail)
            ? "profile auto-recovery attempt failed"
            : recovery.RecoveryDetail;
        return $"{baseMessage} ({detail} for \"{recovery.BrowserProfile}\")";
    }

    private static async Task PublishContinueAsync(
        IEventHandlerContext ctx,
        StepRequestEvent request,
        string cli,
        IReadOnlyList<string> args,
        int timeoutMs,
        double durationMs,
        string error,
        CommandExecutionOutcome recovery,
        CancellationToken ct)
    {
        var continued = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = request.Input ?? string.Empty,
        };
        AppendBaseMetadata(continued, cli, args, timeoutMs, -1, durationMs, error, recovery);
        continued.Metadata["openclaw.continued_on_error"] = "true";
        continued.Metadata["openclaw.error"] = error;
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
        CommandExecutionOutcome recovery,
        CancellationToken ct)
    {
        var failed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = error,
        };
        AppendBaseMetadata(failed, cli, args, timeoutMs, -1, durationMs, string.Empty, recovery);
        await ctx.PublishAsync(failed, EventDirection.Self, ct);
    }

    private static void AppendBaseMetadata(
        StepCompletedEvent evt,
        string cli,
        IReadOnlyList<string> args,
        int timeoutMs,
        int exitCode,
        double durationMs,
        string stderr,
        CommandExecutionOutcome? recovery)
    {
        evt.Metadata["openclaw.cli"] = cli;
        evt.Metadata["openclaw.args"] = string.Join(" ", args);
        evt.Metadata["openclaw.timeout_ms"] = timeoutMs.ToString();
        evt.Metadata["openclaw.exit_code"] = exitCode.ToString();
        evt.Metadata["openclaw.duration_ms"] = durationMs.ToString("F2");
        if (!string.IsNullOrWhiteSpace(stderr))
            evt.Metadata["openclaw.stderr"] = stderr.Trim();

        if (recovery is not { ProfileRecoveryAttempted: true })
            return;

        evt.Metadata["openclaw.profile_recovery_attempted"] = "true";
        evt.Metadata["openclaw.browser_profile"] = recovery.BrowserProfile;
        evt.Metadata["openclaw.profile_recovery_succeeded"] =
            recovery.ProfileRecoverySucceeded ? "true" : "false";

        if (!string.IsNullOrWhiteSpace(recovery.RecoveryDetail))
            evt.Metadata["openclaw.profile_recovery_detail"] = recovery.RecoveryDetail;
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

    private static string? ResolveStdIn(string mode, string? input)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return null;

        return string.Equals(mode, "input", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "pass_input", StringComparison.OrdinalIgnoreCase)
            ? input ?? string.Empty
            : null;
    }

    private static bool TryExtractMediaPath(string output, out string sourcePath)
    {
        sourcePath = string.Empty;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        if (TryExtractPathFromJson(output, out var jsonPath))
        {
            sourcePath = jsonPath;
            return true;
        }

        var mediaMatch = MediaTokenRegex.Match(output);
        if (mediaMatch.Success)
        {
            sourcePath = ExpandPath(mediaMatch.Groups["path"].Value.Trim());
            return !string.IsNullOrWhiteSpace(sourcePath);
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = ExpandPath(line);
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractPathFromJson(string output, out string path)
    {
        path = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!doc.RootElement.TryGetProperty("path", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var rawPath = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            path = ExpandPath(rawPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CopyMediaToDirectory(string sourcePath, string destinationDirectory)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"openclaw media file not found: {sourcePath}", sourcePath);

        Directory.CreateDirectory(destinationDirectory);

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".bin";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt + 1}";
            var fileName = $"shot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}{suffix}{extension}";
            var destination = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destination))
                continue;

            File.Copy(sourcePath, destination, overwrite: false);
            return destination;
        }

        throw new IOException("failed to allocate destination filename for openclaw media artifact");
    }

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

    private sealed record CommandExecutionOutcome(
        CommandExecutionResult Result,
        bool ProfileRecoveryAttempted,
        bool ProfileRecoverySucceeded,
        string BrowserProfile,
        string RecoveryDetail)
    {
        public static CommandExecutionOutcome NoRecovery(CommandExecutionResult result) =>
            new(result, false, false, string.Empty, string.Empty);

        public static CommandExecutionOutcome RecoveryAttempted(
            CommandExecutionResult result,
            string browserProfile,
            bool profileRecoverySucceeded,
            string recoveryDetail) =>
            new(
                result,
                ProfileRecoveryAttempted: true,
                ProfileRecoverySucceeded: profileRecoverySucceeded,
                BrowserProfile: browserProfile,
                RecoveryDetail: recoveryDetail);
    }
}
