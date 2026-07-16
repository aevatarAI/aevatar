using System.Text;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Core;
using OpenSandbox.Models;
using SandboxClient = OpenSandbox.Sandbox;

namespace Aevatar.AI.Infrastructure.OpenSandbox;

internal sealed record OpenSandboxCodexCreateRequest(
    string Domain,
    string ApiKey,
    string Protocol,
    bool UseServerProxy,
    string RunnerImage,
    string RunnerArchitecture,
    Uri GatewayUri,
    int TimeoutSeconds,
    int ReadyTimeoutSeconds,
    string DiagnosticId);

internal sealed record OpenSandboxCodexFile(
    string Path,
    string Contents,
    int Mode);

internal sealed record OpenSandboxCodexCommandResult(
    int? ExitCode,
    string Stdout,
    int StderrBytes,
    string? ExecutionId);

internal interface IOpenSandboxCodexClient
{
    Task<IOpenSandboxCodexSession> CreateAsync(
        OpenSandboxCodexCreateRequest request,
        CancellationToken ct = default);
}

internal interface IOpenSandboxCodexSession : IAsyncDisposable
{
    Task BindCredentialAsync(
        string accessToken,
        Uri gatewayUri,
        string gatewayPathPattern,
        CancellationToken ct = default);

    Task PrepareWorkspaceAsync(
        IReadOnlyList<OpenSandboxCodexFile> files,
        CancellationToken ct = default);

    Task<OpenSandboxCodexCommandResult> RunCommandAsync(
        string command,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string>? environment,
        int maxOutputBytes,
        CancellationToken ct = default);

    Task KillAsync(CancellationToken ct = default);
    Task VerifyAbsentAsync(CancellationToken ct = default);
}

internal sealed class SdkOpenSandboxCodexClient : IOpenSandboxCodexClient
{
    internal const string FakeRunnerToken = "credential-vault-placeholder";

    public async Task<IOpenSandboxCodexSession> CreateAsync(
        OpenSandboxCodexCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = new ConnectionConfig(new ConnectionConfigOptions
        {
            Domain = request.Domain,
            ApiKey = request.ApiKey,
            Protocol = request.Protocol == "https" ? ConnectionProtocol.Https : ConnectionProtocol.Http,
            RequestTimeoutSeconds = 60,
            UseServerProxy = request.UseServerProxy,
        });

        var sandbox = await SandboxClient.CreateAsync(new SandboxCreateOptions
        {
            ConnectionConfig = connection,
            Image = request.RunnerImage,
            Entrypoint = ["/usr/bin/tini", "--", "sleep", "infinity"],
            Env = new Dictionary<string, string>
            {
                ["NYXID_LLM_TOKEN"] = FakeRunnerToken,
            },
            Metadata = new Dictionary<string, string>
            {
                ["aevatar.component"] = "managed-codex-exec",
                ["aevatar.diagnostic_id"] = request.DiagnosticId,
            },
            NetworkPolicy = new NetworkPolicy
            {
                DefaultAction = NetworkRuleAction.Deny,
                Egress =
                [
                    new NetworkRule
                    {
                        Action = NetworkRuleAction.Allow,
                        Target = request.GatewayUri.DnsSafeHost,
                    },
                ],
            },
            CredentialProxy = new CredentialProxyConfig { Enabled = true },
            Platform = new PlatformSpec { Os = "linux", Arch = request.RunnerArchitecture },
            Resource = new Dictionary<string, string>
            {
                ["cpu"] = "1",
                ["memory"] = "2Gi",
                ["ephemeral-storage"] = "2Gi",
            },
            ResourceRequests = new Dictionary<string, string>
            {
                ["cpu"] = "250m",
                ["memory"] = "512Mi",
            },
            TimeoutSeconds = request.TimeoutSeconds + 60,
            ReadyTimeoutSeconds = request.ReadyTimeoutSeconds,
        }, ct).ConfigureAwait(false);

        return new SdkOpenSandboxCodexSession(sandbox);
    }
}

internal sealed class SdkOpenSandboxCodexSession(SandboxClient sandbox) : IOpenSandboxCodexSession
{
    private const string VaultCredentialName = "nyxid-llm-delegation";
    private const string VaultBindingName = "nyxid-llm-gateway";

    private readonly SandboxClient _sandbox =
        sandbox ?? throw new ArgumentNullException(nameof(sandbox));

    public async Task BindCredentialAsync(
        string accessToken,
        Uri gatewayUri,
        string gatewayPathPattern,
        CancellationToken ct = default)
    {
        var vault = await _sandbox.CreateCredentialVaultAsync(
        [
            new Credential
            {
                Name = VaultCredentialName,
                Source = new InlineCredentialSource { Value = accessToken },
            },
        ],
        [
            new CredentialBinding
            {
                Name = VaultBindingName,
                Match = new CredentialMatch
                {
                    Schemes = ["https"],
                    Hosts = [gatewayUri.DnsSafeHost],
                    Methods = ["POST"],
                    Paths = [gatewayPathPattern],
                },
                Auth = new CredentialAuth
                {
                    Type = "bearer",
                    Credential = VaultCredentialName,
                },
            },
        ], ct).ConfigureAwait(false);

        var serialized = System.Text.Json.JsonSerializer.Serialize(vault);
        if (vault.Credentials.SingleOrDefault()?.Name != VaultCredentialName ||
            vault.Bindings.SingleOrDefault()?.Name != VaultBindingName ||
            serialized.Contains(accessToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Credential Vault did not return the expected sanitized binding.");
        }
    }

    public async Task PrepareWorkspaceAsync(
        IReadOnlyList<OpenSandboxCodexFile> files,
        CancellationToken ct = default)
    {
        await _sandbox.Files.CreateDirectoriesAsync(
        [
            new CreateDirectoryEntry
            {
                Path = "/workspace/.aevatar",
                Mode = 750,
                Owner = "codex",
                Group = "codex",
            },
        ], ct).ConfigureAwait(false);

        await _sandbox.Files.WriteFilesAsync(files.Select(file => new WriteEntry
        {
            Path = file.Path,
            Data = file.Contents,
            Mode = file.Mode,
            Owner = "codex",
            Group = "codex",
        }).ToArray(), ct).ConfigureAwait(false);
    }

    public async Task<OpenSandboxCodexCommandResult> RunCommandAsync(
        string command,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string>? environment,
        int maxOutputBytes,
        CancellationToken ct = default)
    {
        var collector = new BoundedCommandOutput(maxOutputBytes);
        string? executionId = null;
        var execution = await _sandbox.Commands.RunAsync(
            command,
            new RunCommandOptions
            {
                WorkingDirectory = "/workspace",
                TimeoutSeconds = timeoutSeconds,
                Uid = 10001,
                Gid = 10001,
                Envs = environment == null
                    ? null
                    : new Dictionary<string, string>(environment, StringComparer.Ordinal),
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
                OnStderr = collector.AppendStderrAsync,
            },
            ct).ConfigureAwait(false);

        return new OpenSandboxCodexCommandResult(
            execution.ExitCode,
            collector.Stdout,
            collector.StderrBytes,
            executionId ?? execution.Id);
    }

    public async Task KillAsync(CancellationToken ct = default)
    {
        try
        {
            await _sandbox.KillAsync(ct).ConfigureAwait(false);
        }
        catch (SandboxApiException exception) when (exception.StatusCode == 404)
        {
        }
    }

    public async Task VerifyAbsentAsync(CancellationToken ct = default)
    {
        while (true)
        {
            try
            {
                await _sandbox.GetInfoAsync(ct).ConfigureAwait(false);
            }
            catch (SandboxApiException exception) when (exception.StatusCode == 404)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _sandbox.DisposeAsync();

    private sealed class BoundedCommandOutput(int maxBytes)
    {
        private readonly StringBuilder _stdout = new();
        private int _totalBytes;

        public string Stdout => _stdout.ToString();
        public int StderrBytes { get; private set; }

        public Task AppendStdoutAsync(OutputMessage message)
        {
            Append(message.Text, true);
            return Task.CompletedTask;
        }

        public Task AppendStderrAsync(OutputMessage message)
        {
            Append(message.Text, false);
            return Task.CompletedTask;
        }

        private void Append(string text, bool stdout)
        {
            var bytes = Encoding.UTF8.GetByteCount(text) + 1;
            if (_totalBytes > maxBytes - bytes)
                throw new InvalidOperationException($"Combined command output exceeded {maxBytes} bytes.");
            _totalBytes += bytes;
            if (stdout)
                _stdout.AppendLine(text);
            else
                StderrBytes += bytes;
        }
    }
}
