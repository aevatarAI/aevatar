using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.AI.Abstractions.CodexExecution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.OpenSandbox;

internal sealed class OpenSandboxCodexExecutionAdapter : ICodexExecutionPort, IDisposable
{
    internal const string FixedCodexCommand =
        "codex --ask-for-approval never exec --ephemeral --json " +
        "- < /workspace/.aevatar/prompt.txt";

    internal const string GitInitializationCommand =
        "git init --quiet && " +
        "git config user.name 'Aevatar Codex Runner' && " +
        "git config user.email 'codex-runner@invalid' && " +
        "git add README.md && " +
        "git commit --quiet --message 'Initialize managed workspace'";

    internal const string SandboxPreflightCommand =
        "test \"${NYXID_LLM_TOKEN:-}\" = 'credential-vault-placeholder' && " +
        "rm -f /opt/aevatar-sandbox-probe/escape " +
        "/workspace/.aevatar/inner-sandbox-ready /workspace/.git/aevatar-landlock-probe && " +
        "codex sandbox --permission-profile aevatar-landlock " +
        "-c use_legacy_landlock=true -C /workspace -- /bin/sh -c '" +
        "printf ready > /workspace/.aevatar/inner-sandbox-ready; " +
        "printf metadata-write > /workspace/.git/aevatar-landlock-probe; " +
        "if printf escaped > /opt/aevatar-sandbox-probe/escape 2>/dev/null; then exit 91; fi' && " +
        "test \"$(cat /workspace/.aevatar/inner-sandbox-ready)\" = ready && " +
        "test \"$(cat /workspace/.git/aevatar-landlock-probe)\" = metadata-write && " +
        "rm -f /workspace/.aevatar/inner-sandbox-ready /workspace/.git/aevatar-landlock-probe && " +
        "test ! -e /opt/aevatar-sandbox-probe/escape";

    private readonly OpenSandboxCodexOptions _options;
    private readonly IManagedCodexCredentialProvider _credentialProvider;
    private readonly IOpenSandboxCodexClient _client;
    private readonly ILogger<OpenSandboxCodexExecutionAdapter> _logger;
    private readonly SemaphoreSlim _capacity;

    public OpenSandboxCodexExecutionAdapter(
        IOptions<OpenSandboxCodexOptions> options,
        IManagedCodexCredentialProvider credentialProvider,
        IOpenSandboxCodexClient client,
        ILogger<OpenSandboxCodexExecutionAdapter> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capacity = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentExecutions));
    }

    public CodexExecutionTarget.TargetOneofCase TargetKind =>
        CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

    public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
        CodexExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CodexExecutionEvent.Started();
        var outcome = await RunLifecycleAsync(request, ct).ConfigureAwait(false);
        yield return outcome.Failure == null
            ? CodexExecutionEvent.Completed(outcome.Result!)
            : CodexExecutionEvent.Failed(outcome.Failure);
    }

    private async Task<ExecutionOutcome> RunLifecycleAsync(
        CodexExecutionRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var admissionFailure = ValidateAdmission(request);
        if (admissionFailure != null)
            return ExecutionOutcome.Failed(admissionFailure);
        if (!_capacity.Wait(0))
        {
            return ExecutionOutcome.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.CapacityUnavailable,
                "managed_capacity_unavailable",
                "Managed Codex capacity is currently unavailable; retry later."));
        }

        var diagnosticId = $"codex-{Guid.NewGuid():N}";
        var started = Stopwatch.StartNew();
        IOpenSandboxCodexSession? session = null;
        ExecutionOutcome? outcome = null;
        try
        {
            var authority = request.Caller.NyxIdAuthority!;
            var credential = await _credentialProvider
                .IssueAsync(authority, request.TimeoutSeconds, ct)
                .ConfigureAwait(false);

            var gateway = new Uri(_options.NyxIdGatewayUrl, UriKind.Absolute);
            try
            {
                session = await _client.CreateAsync(new OpenSandboxCodexCreateRequest(
                    _options.Domain,
                    _options.ApiKey,
                    _options.Protocol,
                    _options.UseServerProxy,
                    _options.RunnerImage,
                    _options.RunnerArchitecture,
                    gateway,
                    request.TimeoutSeconds,
                    _options.ReadyTimeoutSeconds,
                    diagnosticId), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new StageFailureException(ExecutionStage.Provisioning, null, exception);
            }

            outcome = ExecutionOutcome.Succeeded(await ExecuteInSessionAsync(
                session,
                request,
                credential,
                gateway,
                diagnosticId,
                started,
                ct).ConfigureAwait(false));
        }
        catch (ManagedCodexCredentialException exception)
        {
            outcome = ExecutionOutcome.Failed(exception.Failure with { DiagnosticId = diagnosticId });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = ExecutionOutcome.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.Cancelled,
                "managed_execution_cancelled",
                "Managed Codex execution was cancelled.",
                diagnosticId));
        }
        catch (OperationCanceledException)
        {
            outcome = ExecutionOutcome.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.TimedOut,
                "managed_execution_timed_out",
                "Managed Codex execution exceeded its timeout.",
                diagnosticId));
        }
        catch (Exception exception)
        {
            var stage = exception is StageFailureException stageFailure
                ? stageFailure.Stage
                : ExecutionStage.Credential;
            var failure = MapFailure(stage, exception, diagnosticId);
            _logger.LogWarning(
                "Managed codex_exec failed. diagnosticId={DiagnosticId} stage={Stage} exceptionType={ExceptionType}",
                diagnosticId,
                stage,
                exception.GetType().Name);
            outcome = ExecutionOutcome.Failed(failure);
        }
        finally
        {
            if (session != null)
            {
                Exception? cleanupFailure = null;
                using var cleanupTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(_options.CleanupTimeoutSeconds));
                try
                {
                    await session.KillAsync(cleanupTimeout.Token).ConfigureAwait(false);
                    await session.VerifyAbsentAsync(cleanupTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }

                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }

                if (cleanupFailure != null)
                {
                    _logger.LogError(
                        "Managed codex_exec cleanup failed. diagnosticId={DiagnosticId} exceptionType={ExceptionType}",
                        diagnosticId,
                        cleanupFailure.GetType().Name);
                    outcome = ExecutionOutcome.Failed(new CodexExecutionFailure(
                        CodexExecutionFailureKind.CleanupFailed,
                        "sandbox_cleanup_failed",
                        "The managed sandbox could not be verified as destroyed.",
                        diagnosticId));
                }
            }

            _capacity.Release();
        }

        return outcome ?? ExecutionOutcome.Failed(new CodexExecutionFailure(
            CodexExecutionFailureKind.TerminalFailure,
            "managed_execution_incomplete",
            "Managed Codex execution ended without a terminal outcome.",
            diagnosticId));
    }

    private async Task<CodexExecutionResult> ExecuteInSessionAsync(
        IOpenSandboxCodexSession session,
        CodexExecutionRequest request,
        ManagedCodexCredential credential,
        Uri gateway,
        string diagnosticId,
        Stopwatch started,
        CancellationToken ct)
    {
        try
        {
            await session.BindCredentialAsync(
                credential.AccessToken,
                gateway,
                $"{gateway.AbsolutePath.TrimEnd('/')}/*",
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StageFailureException(ExecutionStage.CredentialBinding, null, exception);
        }

        try
        {
            await session.PrepareWorkspaceAsync(
            [
                new OpenSandboxCodexFile(
                    "/home/codex/.codex/config.toml",
                    BuildCodexConfig(gateway, request.TimeoutSeconds),
                    600),
                new OpenSandboxCodexFile(
                    "/workspace/.aevatar/prompt.txt",
                    request.Prompt + "\n",
                    600),
                new OpenSandboxCodexFile(
                    "/workspace/README.md",
                    "# Managed codex_exec workspace\n",
                    644),
            ], ct).ConfigureAwait(false);

            var git = await session.RunCommandAsync(
                GitInitializationCommand,
                30,
                null,
                _options.MaxOutputBytes,
                ct).ConfigureAwait(false);
            if (git.ExitCode != 0)
            {
                throw new StageFailureException(
                    ExecutionStage.Workspace,
                    "workspace_git_initialization_failed");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StageFailureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StageFailureException(ExecutionStage.Workspace, null, exception);
        }

        try
        {
            var preflight = await session.RunCommandAsync(
                SandboxPreflightCommand,
                30,
                null,
                _options.MaxOutputBytes,
                ct).ConfigureAwait(false);
            if (preflight.ExitCode != 0)
            {
                throw new StageFailureException(
                    ExecutionStage.Isolation,
                    "landlock_preflight_failed");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StageFailureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StageFailureException(ExecutionStage.Isolation, null, exception);
        }

        try
        {
            var execution = await session.RunCommandAsync(
                FixedCodexCommand,
                request.TimeoutSeconds,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NYXID_LLM_TOKEN"] = SdkOpenSandboxCodexClient.FakeRunnerToken,
                    ["RUST_LOG"] = "error",
                },
                _options.MaxOutputBytes,
                ct).ConfigureAwait(false);
            if (execution.ExitCode != 0)
                throw new StageFailureException(ExecutionStage.Execution, "codex_nonzero_exit");

            var parsed = CodexJsonlResult.Parse(execution.Stdout);
            if (!parsed.TurnCompleted || string.IsNullOrWhiteSpace(parsed.FinalAgentMessage))
            {
                throw new StageFailureException(
                    ExecutionStage.Execution,
                    "codex_terminal_event_missing");
            }

            return new CodexExecutionResult(
                parsed.FinalAgentMessage,
                execution.ExitCode,
                diagnosticId,
                started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StageFailureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StageFailureException(ExecutionStage.Execution, null, exception);
        }
    }

    private CodexExecutionFailure? ValidateAdmission(CodexExecutionRequest request)
    {
        if (!_options.Enabled)
        {
            return new CodexExecutionFailure(
                CodexExecutionFailureKind.TargetNotConfigured,
                "managed_sandbox_disabled",
                "Managed Codex execution is not enabled on this deployment.");
        }
        if (request.Target.TargetCase != TargetKind ||
            request.Workspace?.WorkspaceCase != CodexExecutionWorkspace.WorkspaceOneofCase.EmptyGit)
        {
            return new CodexExecutionFailure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_request_invalid",
                "Managed Codex execution accepts only the empty_git workspace.");
        }

        var authority = request.Caller.NyxIdAuthority;
        if (authority == null ||
            string.IsNullOrWhiteSpace(authority.Platform) ||
            string.IsNullOrWhiteSpace(authority.ExternalUserId))
        {
            return new CodexExecutionFailure(
                CodexExecutionFailureKind.AdmissionDenied,
                "nyxid_identity_required",
                "Managed Codex execution requires an authenticated NyxID identity.");
        }
        if (!_options.AllowedNyxIdUserIds.Contains(
                authority.ExternalUserId,
                StringComparer.Ordinal))
        {
            return new CodexExecutionFailure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_feature_not_enabled",
                "Managed Codex execution is not enabled for this NyxID account.");
        }

        return null;
    }

    private string BuildCodexConfig(Uri gateway, int timeoutSeconds)
    {
        var baseUrl = gateway.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return $$"""
            model = {{TomlString(_options.Model)}}
            model_provider = "nyxid"
            approval_policy = "never"
            default_permissions = "aevatar-landlock"

            [features]
            use_legacy_landlock = true

            [permissions.aevatar-landlock.filesystem]
            ":root" = "read"
            ":workspace_roots" = { "." = "write", ".git" = "write", ".agents" = "write", ".codex" = "write" }

            [permissions.aevatar-landlock.network]
            enabled = true

            [model_providers.nyxid]
            name = "NyxID LLM Gateway"
            base_url = {{TomlString(baseUrl)}}
            env_key = "NYXID_LLM_TOKEN"
            wire_api = "responses"
            supports_websockets = false
            request_max_retries = 1
            stream_max_retries = 1
            stream_idle_timeout_ms = {{Math.Min(timeoutSeconds * 1000, 150_000)}}
            """;
    }

    private static string TomlString(string value) => JsonSerializer.Serialize(value);

    private static CodexExecutionFailure MapFailure(
        ExecutionStage stage,
        Exception exception,
        string diagnosticId)
    {
        var stageFailure = exception as StageFailureException;
        return stage switch
        {
            ExecutionStage.Provisioning => new CodexExecutionFailure(
                CodexExecutionFailureKind.ProvisioningFailed,
                "sandbox_provisioning_failed",
                "The managed sandbox could not be provisioned.",
                diagnosticId),
            ExecutionStage.CredentialBinding => new CodexExecutionFailure(
                CodexExecutionFailureKind.ProvisioningFailed,
                "credential_vault_binding_failed",
                "The managed sandbox credential binding failed.",
                diagnosticId),
            ExecutionStage.Workspace => new CodexExecutionFailure(
                CodexExecutionFailureKind.ReadinessFailed,
                stageFailure?.Code ?? "workspace_preparation_failed",
                "The managed Codex workspace could not be prepared.",
                diagnosticId),
            ExecutionStage.Isolation => new CodexExecutionFailure(
                CodexExecutionFailureKind.IsolationUnavailable,
                stageFailure?.Code ?? "landlock_unavailable",
                "The required Landlock boundary is unavailable.",
                diagnosticId),
            ExecutionStage.Execution => new CodexExecutionFailure(
                stageFailure?.Code is "codex_terminal_event_missing" or "codex_jsonl_malformed" or "codex_jsonl_type_missing"
                    ? CodexExecutionFailureKind.MalformedOutput
                    : CodexExecutionFailureKind.TerminalFailure,
                stageFailure?.Code ?? "codex_terminal_failure",
                "Codex did not complete successfully.",
                diagnosticId),
            _ => new CodexExecutionFailure(
                CodexExecutionFailureKind.TerminalFailure,
                "managed_execution_failed",
                "Managed Codex execution failed.",
                diagnosticId),
        };
    }

    public void Dispose() => _capacity.Dispose();

    private enum ExecutionStage
    {
        Credential,
        Provisioning,
        CredentialBinding,
        Workspace,
        Isolation,
        Execution,
    }

    private sealed class StageFailureException(
        ExecutionStage stage,
        string? code,
        Exception? innerException = null)
        : Exception(code ?? "stage_failed", innerException)
    {
        public ExecutionStage Stage { get; } = stage;
        public string? Code { get; } = code;
    }

    private sealed record ExecutionOutcome(
        CodexExecutionResult? Result,
        CodexExecutionFailure? Failure)
    {
        public static ExecutionOutcome Succeeded(CodexExecutionResult result) => new(result, null);
        public static ExecutionOutcome Failed(CodexExecutionFailure failure) => new(null, failure);
    }

    private sealed record CodexJsonlResult(bool TurnCompleted, string? FinalAgentMessage)
    {
        public static CodexJsonlResult Parse(string jsonl)
        {
            var completed = false;
            string? finalMessage = null;
            foreach (var line in jsonl.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException exception)
                {
                    throw new StageFailureException(
                        ExecutionStage.Execution,
                        "codex_jsonl_malformed",
                        exception);
                }

                using (document)
                {
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                    throw new StageFailureException(
                        ExecutionStage.Execution,
                        "codex_jsonl_type_missing");
                var type = typeElement.GetString();
                if (type == "turn.failed")
                    throw new StageFailureException(
                        ExecutionStage.Execution,
                        "codex_turn_failed");
                if (type == "turn.completed")
                    completed = true;
                if (type == "item.completed" &&
                    root.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("type", out var itemType) &&
                    itemType.GetString() == "agent_message" &&
                    item.TryGetProperty("text", out var text))
                {
                    finalMessage = text.GetString();
                }
                }
            }

            return new CodexJsonlResult(completed, finalMessage);
        }
    }
}
