using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Core;
using OpenSandbox.Models;
using SandboxClient = global::OpenSandbox.Sandbox;

namespace Aevatar.OpenSandbox.CodexRunner.Smoke;

internal static class Program
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private const string ExpectedOutput = "CODEX_EXEC_READY";
    private const string FakeRunnerToken = "credential-vault-placeholder";
    private const string VaultCredentialName = "nyxid-llm-delegation";
    private const string VaultBindingName = "nyxid-llm-gateway";
    private const string FixedCodexCommand =
        "codex --ask-for-approval never exec --ephemeral --json " +
        "--sandbox workspace-write - < /workspace/.aevatar/prompt.txt";
    private const string SandboxPreflightCommand =
        "test \"${NYXID_LLM_TOKEN:-}\" = 'credential-vault-placeholder' && " +
        "rm -f /opt/aevatar-sandbox-probe/escape /workspace/.aevatar/inner-sandbox-ready && " +
        "codex sandbox -C /workspace -- /bin/sh -c '" +
        "printf ready > /workspace/.aevatar/inner-sandbox-ready; " +
        "if printf escaped > /opt/aevatar-sandbox-probe/escape 2>/dev/null; then exit 91; fi' && " +
        "test \"$(cat /workspace/.aevatar/inner-sandbox-ready)\" = ready && " +
        "test ! -e /opt/aevatar-sandbox-probe/escape";
    private const string GitInitializationCommand =
        "git init --quiet && " +
        "git config user.name 'Aevatar Codex Runner' && " +
        "git config user.email 'codex-runner@invalid' && " +
        "git add README.md && " +
        "git commit --quiet --message 'Initialize managed workspace'";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            PrintUsage();
            return 0;
        }

        if (args.Contains("--self-test", StringComparer.Ordinal))
        {
            RunSelfTest();
            return 0;
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("Only --help and --self-test are accepted. Live configuration is environment-only.");
            return 2;
        }

        SmokeSettings? settings = null;
        try
        {
            settings = SmokeSettings.Load();
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var evidence = await RunLiveAsync(settings, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(evidence, EvidenceJsonOptions));
            return 0;
        }
        catch (SmokeConfigurationException exception)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return 2;
        }
        catch (SandboxException exception)
        {
            var message = settings?.Redact(exception.Error.Message ?? "OpenSandbox request failed")
                ?? "OpenSandbox request failed";
            Console.Error.WriteLine($"OpenSandbox error: code={exception.Error.Code}, request_id={exception.RequestId}, message={message}");
            return 3;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Smoke run cancelled; cleanup was attempted.");
            return 4;
        }
        catch (Exception exception)
        {
            var message = settings?.Redact(exception.Message) ?? exception.Message;
            Console.Error.WriteLine($"Smoke run failed: {message}");
            return 5;
        }
    }

    private static async Task<SmokeEvidence> RunLiveAsync(
        SmokeSettings settings,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.StartNew();
        var connection = new ConnectionConfig(new ConnectionConfigOptions
        {
            Domain = settings.OpenSandboxDomain,
            ApiKey = settings.OpenSandboxApiKey,
            Protocol = settings.OpenSandboxProtocol,
            RequestTimeoutSeconds = 60,
            UseServerProxy = settings.UseServerProxy
        });

        SandboxClient? sandbox = null;
        SmokeEvidence? evidence = null;
        Exception? runFailure = null;
        Exception? cleanupFailure = null;
        var cleanup = "not_started";

        try
        {
            sandbox = await SandboxClient.CreateAsync(new SandboxCreateOptions
            {
                ConnectionConfig = connection,
                Image = settings.RunnerImage,
                Entrypoint = new[] { "/usr/bin/tini", "--", "sleep", "infinity" },
                Env = new Dictionary<string, string>
                {
                    ["NYXID_LLM_TOKEN"] = FakeRunnerToken
                },
                Metadata = new Dictionary<string, string>
                {
                    ["aevatar.component"] = "codex-runner-smoke",
                    ["aevatar.probe_id"] = Guid.NewGuid().ToString("N")
                },
                NetworkPolicy = new NetworkPolicy
                {
                    DefaultAction = NetworkRuleAction.Deny,
                    Egress = new List<NetworkRule>
                    {
                        new() { Action = NetworkRuleAction.Allow, Target = settings.GatewayUri.DnsSafeHost }
                    }
                },
                CredentialProxy = new CredentialProxyConfig { Enabled = true },
                Platform = new PlatformSpec { Os = "linux", Arch = settings.RunnerArchitecture },
                Resource = new Dictionary<string, string>
                {
                    ["cpu"] = "1",
                    ["memory"] = "2Gi",
                    ["ephemeral-storage"] = "2Gi"
                },
                ResourceRequests = new Dictionary<string, string>
                {
                    ["cpu"] = "250m",
                    ["memory"] = "512Mi"
                },
                TimeoutSeconds = settings.TimeoutSeconds + 60,
                ReadyTimeoutSeconds = settings.ReadyTimeoutSeconds
            }, cancellationToken);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "sandbox_ready",
                sandbox_id = sandbox.Id
            }));

            var vault = await sandbox.CreateCredentialVaultAsync(
                new[]
                {
                    new Credential
                    {
                        Name = VaultCredentialName,
                        Source = new InlineCredentialSource { Value = settings.NyxIdDelegationToken }
                    }
                },
                new[]
                {
                    new CredentialBinding
                    {
                        Name = VaultBindingName,
                        Match = new CredentialMatch
                        {
                            Schemes = new[] { "https" },
                            Hosts = new[] { settings.GatewayUri.DnsSafeHost },
                            Methods = new[] { "POST" },
                            Paths = new[] { settings.GatewayPathPattern }
                        },
                        Auth = new CredentialAuth
                        {
                            Type = "bearer",
                            Credential = VaultCredentialName
                        }
                    }
                },
                cancellationToken);
            RequireExpectedCredentialVault(vault, settings);

            await PrepareWorkspaceAsync(sandbox, settings, cancellationToken);
            await RequireSuccessfulCommandAsync(
                sandbox,
                SandboxPreflightCommand,
                timeoutSeconds: 30,
                cancellationToken);

            var collector = new BoundedOutputCollector(settings.MaxOutputBytes);
            string? executionId = null;
            var execution = await sandbox.Commands.RunAsync(
                FixedCodexCommand,
                new RunCommandOptions
                {
                    WorkingDirectory = "/workspace",
                    TimeoutSeconds = settings.TimeoutSeconds,
                    Uid = 10001,
                    Gid = 10001,
                    Envs = new Dictionary<string, string>
                    {
                        ["NYXID_LLM_TOKEN"] = FakeRunnerToken,
                        ["RUST_LOG"] = "error"
                    }
                },
                new ExecutionHandlers
                {
                    SkipAccumulation = true,
                    OnInit = init =>
                    {
                        executionId = init.Id;
                        return Task.CompletedTask;
                    },
                    OnStdout = collector.AppendStdoutAsync,
                    OnStderr = collector.AppendStderrAsync
                },
                cancellationToken);

            var result = CodexJsonlResult.Parse(collector.Stdout);
            if (execution.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Codex exited with code {execution.ExitCode?.ToString() ?? "unknown"}; stderr_bytes={collector.StderrBytes}.");
            }

            if (!result.TurnCompleted || result.FinalAgentMessage != ExpectedOutput)
            {
                throw new InvalidOperationException(
                    $"Codex readiness mismatch: turn_completed={result.TurnCompleted}, final_message={JsonSerializer.Serialize(result.FinalAgentMessage)}.");
            }

            evidence = new SmokeEvidence(
                Status: "ready",
                SandboxId: sandbox.Id,
                ExecutionId: executionId ?? execution.Id,
                Output: result.FinalAgentMessage,
                RunnerImage: settings.RunnerImage,
                RunnerArchitecture: settings.RunnerArchitecture,
                ElapsedMilliseconds: startedAt.ElapsedMilliseconds,
                Cleanup: "pending");
        }
        catch (Exception exception)
        {
            runFailure = exception;
        }
        finally
        {
            if (sandbox is not null)
            {
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        await sandbox.KillAsync(cleanupTimeout.Token);
                    }
                    catch (SandboxApiException exception) when (exception.StatusCode == 404)
                    {
                        // The server TTL or an operator may have completed deletion first.
                    }

                    await RequireSandboxAbsentAsync(sandbox, cleanupTimeout.Token);
                    cleanup = "sandbox_absent";
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
                finally
                {
                    try
                    {
                        await sandbox.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure = cleanupFailure is null
                            ? exception
                            : new AggregateException("Kill and SDK disposal both failed.", cleanupFailure, exception);
                    }
                }
            }
        }

        if (runFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException("Run and cleanup both failed.", runFailure, cleanupFailure);
        }

        if (runFailure is not null)
        {
            ExceptionDispatchInfo.Capture(runFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        return evidence is null
            ? throw new InvalidOperationException("Smoke run completed without evidence.")
            : evidence with { Cleanup = cleanup };
    }

    private static void RequireExpectedCredentialVault(
        CredentialVaultState vault,
        SmokeSettings settings)
    {
        var credential = vault.Credentials.SingleOrDefault();
        var binding = vault.Bindings.SingleOrDefault();
        var serializedState = JsonSerializer.Serialize(vault);
        if (credential?.Name != VaultCredentialName ||
            binding?.Name != VaultBindingName ||
            binding.Auth?.Type != "bearer" ||
            binding.Match?.Schemes?.SequenceEqual(new[] { "https" }) != true ||
            binding.Match.Hosts?.SequenceEqual(new[] { settings.GatewayUri.DnsSafeHost }) != true ||
            binding.Match.Methods?.SequenceEqual(new[] { "POST" }) != true ||
            binding.Match.Paths?.SequenceEqual(new[] { settings.GatewayPathPattern }) != true ||
            serializedState.Contains(settings.NyxIdDelegationToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Credential Vault did not return the expected sanitized binding.");
        }
    }

    private static async Task RequireSandboxAbsentAsync(
        SandboxClient sandbox,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await sandbox.GetInfoAsync(cancellationToken);
            }
            catch (SandboxApiException exception) when (exception.StatusCode == 404)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static async Task PrepareWorkspaceAsync(
        SandboxClient sandbox,
        SmokeSettings settings,
        CancellationToken cancellationToken)
    {
        await sandbox.Files.CreateDirectoriesAsync(new[]
        {
            new CreateDirectoryEntry
            {
                Path = "/workspace/.aevatar",
                Mode = 750,
                Owner = "codex",
                Group = "codex"
            }
        }, cancellationToken);

        await sandbox.Files.WriteFilesAsync(new[]
        {
            new WriteEntry
            {
                Path = "/home/codex/.codex/config.toml",
                Data = BuildCodexConfig(settings),
                Mode = 600,
                Owner = "codex",
                Group = "codex"
            },
            new WriteEntry
            {
                Path = "/workspace/.aevatar/prompt.txt",
                Data = $"Reply with exactly {ExpectedOutput}\n",
                Mode = 600,
                Owner = "codex",
                Group = "codex"
            },
            new WriteEntry
            {
                Path = "/workspace/README.md",
                Data = "# Managed codex_exec workspace\n",
                Mode = 644,
                Owner = "codex",
                Group = "codex"
            }
        }, cancellationToken);

        await RequireSuccessfulCommandAsync(
            sandbox,
            GitInitializationCommand,
            timeoutSeconds: 30,
            cancellationToken);
    }

    private static async Task RequireSuccessfulCommandAsync(
        SandboxClient sandbox,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var execution = await sandbox.Commands.RunAsync(
            command,
            new RunCommandOptions
            {
                WorkingDirectory = "/workspace",
                TimeoutSeconds = timeoutSeconds,
                Uid = 10001,
                Gid = 10001
            },
            cancellationToken: cancellationToken);

        if (execution.ExitCode != 0)
        {
            var stderrBytes = execution.Logs.Stderr.Sum(message => Encoding.UTF8.GetByteCount(message.Text));
            throw new InvalidOperationException(
                $"Fixed sandbox command failed with exit code {execution.ExitCode?.ToString() ?? "unknown"}; stderr_bytes={stderrBytes}.");
        }
    }

    private static string BuildCodexConfig(SmokeSettings settings)
    {
        var baseUrl = settings.GatewayUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return $$"""
            model = {{TomlString(settings.Model)}}
            model_provider = "nyxid"
            approval_policy = "never"
            sandbox_mode = "workspace-write"

            [model_providers.nyxid]
            name = "NyxID LLM Gateway"
            base_url = {{TomlString(baseUrl)}}
            env_key = "NYXID_LLM_TOKEN"
            wire_api = "responses"
            supports_websockets = false
            request_max_retries = 1
            stream_max_retries = 1
            stream_idle_timeout_ms = {{Math.Min(settings.TimeoutSeconds * 1000, 150_000)}}
            """;
    }

    private static string TomlString(string value) => JsonSerializer.Serialize(value);

    private static void RunSelfTest()
    {
        SmokeSettings.ValidateImageDigest(
            "ghcr.io/aevatarai/codex-runner@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        RequireThrows<SmokeConfigurationException>(() =>
            SmokeSettings.ValidateImageDigest("ghcr.io/aevatarai/codex-runner:latest"));

        var parsed = CodexJsonlResult.Parse(
            "{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}\n" +
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"CODEX_EXEC_READY\"}}\n" +
            "{\"type\":\"turn.completed\"}\n");
        if (!parsed.TurnCompleted || parsed.FinalAgentMessage != ExpectedOutput)
        {
            throw new InvalidOperationException("JSONL self-test failed.");
        }

        RequireThrows<InvalidOperationException>(() =>
            CodexJsonlResult.Parse("{\"type\":\"turn.failed\"}\n"));

        var settings = new SmokeSettings(
            "opensandbox.example.internal",
            "opensandbox-control-secret",
            ConnectionProtocol.Https,
            false,
            "ghcr.io/aevatarai/codex-runner@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "amd64",
            new Uri("https://nyx.example.com/api/v1/llm/gateway/v1"),
            "delegated-token-must-not-enter-config",
            "gpt-5.4",
            180,
            60,
            1_048_576);
        var config = BuildCodexConfig(settings);
        if (config.Contains(settings.NyxIdDelegationToken, StringComparison.Ordinal) ||
            !config.Contains("env_key = \"NYXID_LLM_TOKEN\"", StringComparison.Ordinal) ||
            !config.Contains("wire_api = \"responses\"", StringComparison.Ordinal) ||
            !SandboxPreflightCommand.Contains(FakeRunnerToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex provider configuration self-test failed.");
        }

        Console.WriteLine("OpenSandbox Codex smoke self-test passed.");
    }

    private static void RequireThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Run with --self-test for an offline parser/configuration test.");
        Console.WriteLine("Live execution accepts configuration only through the documented environment variables.");
        Console.WriteLine("See tools/opensandbox-codex-smoke/README.md.");
    }
}

internal sealed record SmokeEvidence(
    string Status,
    string SandboxId,
    string? ExecutionId,
    string Output,
    string RunnerImage,
    string RunnerArchitecture,
    long ElapsedMilliseconds,
    string Cleanup);

internal sealed record CodexJsonlResult(bool TurnCompleted, string? FinalAgentMessage)
{
    public static CodexJsonlResult Parse(string jsonl)
    {
        var turnCompleted = false;
        string? finalAgentMessage = null;
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                throw new InvalidOperationException("Codex JSONL event is missing type.");
            }

            var type = typeElement.GetString();
            if (type == "turn.failed")
            {
                throw new InvalidOperationException("Codex emitted turn.failed.");
            }

            if (type == "turn.completed")
            {
                turnCompleted = true;
            }

            if (type == "item.completed" &&
                root.TryGetProperty("item", out var item) &&
                item.TryGetProperty("type", out var itemType) &&
                itemType.GetString() == "agent_message" &&
                item.TryGetProperty("text", out var text))
            {
                finalAgentMessage = text.GetString();
            }
        }

        return new CodexJsonlResult(turnCompleted, finalAgentMessage);
    }
}

internal sealed class BoundedOutputCollector(int maxBytes)
{
    private readonly StringBuilder _stdout = new();
    private int _totalBytes;

    public string Stdout => _stdout.ToString();
    public int StderrBytes { get; private set; }

    public Task AppendStdoutAsync(OutputMessage message)
    {
        Append(message.Text, isStdout: true);
        return Task.CompletedTask;
    }

    public Task AppendStderrAsync(OutputMessage message)
    {
        Append(message.Text, isStdout: false);
        return Task.CompletedTask;
    }

    private void Append(string text, bool isStdout)
    {
        var bytes = Encoding.UTF8.GetByteCount(text) + 1;
        if (_totalBytes > maxBytes - bytes)
        {
            throw new InvalidOperationException($"Combined command output exceeded {maxBytes} bytes.");
        }

        _totalBytes += bytes;
        if (isStdout)
        {
            _stdout.AppendLine(text);
        }
        else
        {
            StderrBytes += bytes;
        }
    }
}

internal sealed record SmokeSettings(
    string OpenSandboxDomain,
    string OpenSandboxApiKey,
    ConnectionProtocol OpenSandboxProtocol,
    bool UseServerProxy,
    string RunnerImage,
    string RunnerArchitecture,
    Uri GatewayUri,
    string NyxIdDelegationToken,
    string Model,
    int TimeoutSeconds,
    int ReadyTimeoutSeconds,
    int MaxOutputBytes)
{
    private static readonly Regex ModelPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$", RegexOptions.Compiled);

    public string GatewayPathPattern => $"{GatewayUri.AbsolutePath.TrimEnd('/')}/*";

    public static SmokeSettings Load()
    {
        var domain = RequireEnvironment("OPEN_SANDBOX_DOMAIN");
        if (domain.Contains("://", StringComparison.Ordinal))
        {
            throw new SmokeConfigurationException("OPEN_SANDBOX_DOMAIN must be host[:port] without a scheme.");
        }

        var protocol = OptionalEnvironment("OPEN_SANDBOX_PROTOCOL", "http") switch
        {
            "http" => ConnectionProtocol.Http,
            "https" => ConnectionProtocol.Https,
            _ => throw new SmokeConfigurationException("OPEN_SANDBOX_PROTOCOL must be http or https.")
        };
        var image = RequireEnvironment("CODEX_RUNNER_IMAGE");
        ValidateImageDigest(image);

        if (!Uri.TryCreate(RequireEnvironment("NYXID_LLM_GATEWAY_URL"), UriKind.Absolute, out var gateway) ||
            gateway.Scheme != Uri.UriSchemeHttps ||
            !gateway.IsDefaultPort ||
            !string.IsNullOrEmpty(gateway.Query) ||
            !string.IsNullOrEmpty(gateway.Fragment))
        {
            throw new SmokeConfigurationException(
                "NYXID_LLM_GATEWAY_URL must be an HTTPS URL on port 443 without query or fragment.");
        }

        var model = OptionalEnvironment("CODEX_MODEL", "gpt-5.4");
        if (!ModelPattern.IsMatch(model))
        {
            throw new SmokeConfigurationException("CODEX_MODEL contains unsupported characters.");
        }

        var architecture = OptionalEnvironment("CODEX_RUNNER_ARCH", "amd64");
        if (architecture is not ("amd64" or "arm64"))
        {
            throw new SmokeConfigurationException("CODEX_RUNNER_ARCH must be amd64 or arm64.");
        }

        return new SmokeSettings(
            domain,
            RequireEnvironment("OPEN_SANDBOX_API_KEY"),
            protocol,
            ParseBoolean("OPEN_SANDBOX_USE_SERVER_PROXY", defaultValue: false),
            image,
            architecture,
            gateway,
            RequireEnvironment("NYXID_LLM_DELEGATION_TOKEN"),
            model,
            ParseInteger("CODEX_TIMEOUT_SECONDS", defaultValue: 180, minimum: 1, maximum: 180),
            ParseInteger("OPEN_SANDBOX_READY_TIMEOUT_SECONDS", defaultValue: 60, minimum: 1, maximum: 120),
            ParseInteger("CODEX_OUTPUT_MAX_BYTES", defaultValue: 1_048_576, minimum: 1024, maximum: 4_194_304));
    }

    public static void ValidateImageDigest(string image)
    {
        const string marker = "@sha256:";
        var markerIndex = image.LastIndexOf(marker, StringComparison.Ordinal);
        var digest = markerIndex < 0 ? string.Empty : image[(markerIndex + marker.Length)..];
        if (markerIndex <= 0 || digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new SmokeConfigurationException("CODEX_RUNNER_IMAGE must be pinned with @sha256:<64 hex characters>.");
        }
    }

    public string Redact(string message) => message
        .Replace(OpenSandboxApiKey, "[REDACTED]", StringComparison.Ordinal)
        .Replace(NyxIdDelegationToken, "[REDACTED]", StringComparison.Ordinal);

    private static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new SmokeConfigurationException($"{name} is required.")
            : value.Trim();
    }

    private static string OptionalEnvironment(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static bool ParseBoolean(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : bool.TryParse(value, out var parsed)
                ? parsed
                : throw new SmokeConfigurationException($"{name} must be true or false.");
    }

    private static int ParseInteger(string name, int defaultValue, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new SmokeConfigurationException($"{name} must be between {minimum} and {maximum}.");
        }

        return parsed;
    }
}

internal sealed class SmokeConfigurationException(string message) : Exception(message);
