using System.Text.Json;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal sealed class NyxIdManagedCodexChronoTransport(
    IOptions<ManagedCodexOptions> options,
    INyxIdApiClientFactory clientFactory,
    ISecretVault secretVault,
    TimeProvider timeProvider,
    ILogger<NyxIdManagedCodexChronoTransport>? logger = null) : IManagedCodexChronoTransport
{
    private readonly ManagedCodexOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly INyxIdApiClientFactory _clientFactory =
        clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly ISecretVault _secretVault =
        secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger _logger = logger ?? NullLogger<NyxIdManagedCodexChronoTransport>.Instance;
    private static readonly HashSet<string> AllowedUpstreamFailureCodes =
        new(StringComparer.Ordinal)
        {
            "CODEX_AGENT_MESSAGE_MISSING",
            "CODEX_CALLER_CREDENTIAL_FORWARDED",
            "CODEX_CAPACITY_UNAVAILABLE",
            "CODEX_CLEANUP_UNCONFIRMED",
            "CODEX_COMMAND_FAILED",
            "CODEX_COMMAND_TIMEOUT",
            "CODEX_CONFIG_INVALID",
            "CODEX_DELEGATION_ACTOR_INVALID",
            "CODEX_DELEGATION_AUDIENCE_INVALID",
            "CODEX_DELEGATION_DUPLICATED",
            "CODEX_DELEGATION_EXPIRED",
            "CODEX_DELEGATION_INVALID",
            "CODEX_DELEGATION_ISSUER_INVALID",
            "CODEX_DELEGATION_MARKER_INVALID",
            "CODEX_DELEGATION_MISSING",
            "CODEX_DELEGATION_NOT_YET_VALID",
            "CODEX_DELEGATION_SCOPE_INVALID",
            "CODEX_DELEGATION_SUBJECT_INVALID",
            "CODEX_DELEGATION_TYPE_INVALID",
            "CODEX_DELEGATION_VERIFIER_UNAVAILABLE",
            "CODEX_EXECD_TERMINAL_MISSING",
            "CODEX_EXECUTION_TIMEOUT",
            "CODEX_FEATURE_DISABLED",
            "CODEX_OPENSANDBOX_TIMEOUT",
            "CODEX_OPENSANDBOX_UNAVAILABLE",
            "CODEX_OUTPUT_INVALID",
            "CODEX_OUTPUT_TOO_LARGE",
            "CODEX_PROMPT_INVALID",
            "CODEX_PROMPT_TOO_LARGE",
            "CODEX_REQUEST_INVALID",
            "CODEX_SANDBOX_CREATION_FAILED",
            "CODEX_SANDBOX_READY_TIMEOUT",
            "CODEX_TIMEOUT_INVALID",
            "CODEX_TURN_FAILED",
            "CODEX_TURN_TERMINAL_MISSING",
            "CODEX_WORKSPACE_INVALID",
            "CODEX_WORKSPACE_PREPARATION_FAILED",
        };

    public async Task<CodexExecutionResult> ExecuteAsync(
        CodexExecutionRequest request,
        ManagedCodexCredentialDescriptor credential,
        CancellationToken ct = default)
    {
        var owner = ValidateRequest(request);
        credential = ValidateCredential(credential, owner);
        var reference = credential.SecretReference;

        ResolveSecretResult resolved;
        try
        {
            resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    reference.Ref,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    reference.OwnerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    "managed-codex-execute"),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw Failure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_credential_unavailable",
                "Managed Codex credential is unavailable.");
        }

        if (!resolved.Resolved ||
            resolved.Reference is null ||
            string.IsNullOrWhiteSpace(resolved.Secret) ||
            !ReferenceMatches(resolved.Reference, reference))
        {
            throw Failure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_credential_unavailable",
                "Managed Codex credential is unavailable.");
        }

        var secret = new ManagedCodexOpaqueSecret(resolved.Secret);
        var body = JsonSerializer.Serialize(new
        {
            prompt = request.Prompt,
            timeout_secs = request.TimeoutSeconds,
            workspace = "empty_git",
        });
        using var lifecycleTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(
                request.TimeoutSeconds + _options.ExecutionLifecycleGraceSeconds),
            _timeProvider);
        using var requestDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(ct, lifecycleTimeout.Token);
        var response = await secret.UseAsync(rawKey => _clientFactory.CreateClient().ProxyRequestBoundedWithApiKeyAsync(
                rawKey,
                ManagedCodexOptions.ChronoSandboxServiceSlug,
                credential.ChronoSandboxUserServiceId,
                ManagedCodexOptions.ChronoExecutionPath,
                HttpMethod.Post.Method,
                body,
                _options.MaxResponseBytes,
                requestDeadline.Token))
            .ConfigureAwait(false);
        if (!response.Succeeded)
        {
            if (response.Detail is
                "content_length_exceeds_max_bytes" or
                "content_exceeds_max_bytes")
            {
                throw Failure(
                    CodexExecutionFailureKind.MalformedOutput,
                    "managed_response_too_large",
                    "Managed Codex returned an oversized response.");
            }

            if (response.HttpStatus > 0)
                throw ProxyFailure(response.HttpStatus, response.Content);

            throw Failure(
                CodexExecutionFailureKind.TerminalFailure,
                "managed_proxy_unavailable",
                "Managed Codex proxy is temporarily unavailable.");
        }

        return secret.Use(rawKey => ParseResponse(response.Content, rawKey));
    }

    private ExternalSubjectRef ValidateRequest(CodexExecutionRequest? request)
    {
        if (!_options.Enabled)
        {
            throw Failure(
                CodexExecutionFailureKind.TargetNotConfigured,
                "managed_target_disabled",
                "Managed Codex execution is disabled.");
        }
        if (request is null ||
            request.Target?.TargetCase != CodexExecutionTarget.TargetOneofCase.ManagedSandbox ||
            request.Workspace?.WorkspaceCase != CodexExecutionWorkspace.WorkspaceOneofCase.EmptyGit ||
            string.IsNullOrWhiteSpace(request.Prompt) ||
            request.TimeoutSeconds <= 0)
        {
            throw Failure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_request_invalid",
                "Managed Codex execution request is invalid.");
        }

        var authority = request.Caller?.NyxIdAuthority;
        if (authority is null ||
            !string.Equals(authority.Platform?.Trim(), OwnerScope.NyxIdPlatform, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(authority.Tenant) ||
            string.IsNullOrWhiteSpace(authority.ExternalUserId))
        {
            throw Failure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_identity_unavailable",
                "A native NyxID user identity is required for managed Codex execution.");
        }

        return new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = authority.ExternalUserId.Trim(),
        };
    }

    private ManagedCodexCredentialDescriptor ValidateCredential(
        ManagedCodexCredentialDescriptor? credential,
        ExternalSubjectRef owner)
    {
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        try
        {
            if (credential?.Owner is null ||
                !string.Equals(
                    ManagedCodexCredentialActorIdentity.From(credential.Owner),
                    actorId,
                    StringComparison.Ordinal) ||
                credential.Status != ManagedCodexCredentialStatus.Active ||
                credential.ExpiresAt is null ||
                credential.ExpiresAt.ToDateTimeOffset() <= _timeProvider.GetUtcNow() ||
                string.IsNullOrWhiteSpace(credential.ApiKeyId) ||
                !string.Equals(
                    credential.ChronoSandboxServiceSlug,
                    ManagedCodexOptions.ChronoSandboxServiceSlug,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(credential.ChronoSandboxUserServiceId) ||
                string.IsNullOrWhiteSpace(credential.ChronoLlmUserServiceId) ||
                string.Equals(
                    credential.ChronoSandboxUserServiceId,
                    credential.ChronoLlmUserServiceId,
                    StringComparison.Ordinal) ||
                credential.SecretReference is null ||
                string.IsNullOrWhiteSpace(credential.SecretReference.Ref) ||
                !string.Equals(
                    credential.SecretReference.Purpose,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    StringComparison.Ordinal) ||
                !string.Equals(credential.SecretReference.OwnerScopeKey, actorId, StringComparison.Ordinal) ||
                credential.SecretReference.Version <= 0 ||
                string.IsNullOrWhiteSpace(credential.SecretReference.Fingerprint))
            {
                throw new InvalidOperationException();
            }
        }
        catch (ArgumentException)
        {
            throw Failure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_credential_invalid",
                "Managed Codex credential is invalid.");
        }
        catch (InvalidOperationException)
        {
            throw Failure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_credential_invalid",
                "Managed Codex credential is invalid.");
        }

        return credential;
    }

    private static bool ReferenceMatches(SecretReference actual, SecretReference expected) =>
        string.Equals(actual.Ref, expected.Ref, StringComparison.Ordinal) &&
        string.Equals(actual.Purpose, expected.Purpose, StringComparison.Ordinal) &&
        string.Equals(actual.OwnerScopeKey, expected.OwnerScopeKey, StringComparison.Ordinal) &&
        actual.Version == expected.Version &&
        string.Equals(actual.Fingerprint, expected.Fingerprint, StringComparison.Ordinal);

    private CodexExecutionResult ParseResponse(string response, string rawKey)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException();

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
            {
                var status = root.TryGetProperty("status", out var statusElement) &&
                             statusElement.TryGetInt32(out var parsedStatus)
                    ? parsedStatus
                    : 0;
                throw ProxyFailure(status, response);
            }

            if (!root.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Object ||
                !output.TryGetProperty("text", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String)
            {
                throw Failure(
                    CodexExecutionFailureKind.MalformedOutput,
                    "managed_response_invalid",
                    "Managed Codex returned an invalid response.");
            }

            var text = Redact(textElement.GetString() ?? string.Empty, rawKey) ?? string.Empty;
            if (!output.TryGetProperty("exit_code", out var exitCodeElement) ||
                !exitCodeElement.TryGetInt32(out var exitCode))
            {
                throw Failure(
                    CodexExecutionFailureKind.MalformedOutput,
                    "managed_response_invalid",
                    "Managed Codex returned an invalid response.");
            }
            var elapsed = output.TryGetProperty("execution_time_ms", out var elapsedElement) &&
                          elapsedElement.TryGetInt64(out var parsedElapsed)
                ? parsedElapsed
                : (long?)null;
            var diagnosticId = root.TryGetProperty("diagnostic_id", out var diagnosticElement) &&
                               diagnosticElement.ValueKind == JsonValueKind.String
                ? Redact(diagnosticElement.GetString(), rawKey)
                : null;
            return new CodexExecutionResult(text, exitCode, diagnosticId, elapsed);
        }
        catch (ManagedCodexTransportException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Failure(
                CodexExecutionFailureKind.MalformedOutput,
                "managed_response_invalid",
                "Managed Codex returned an invalid response.");
        }
    }

    private ManagedCodexTransportException ProxyFailure(int status, string? response = null)
    {
        var inspection = ChronoProxyFailureInspector.Inspect(response, AllowedUpstreamFailureCodes);
        var upstreamCode = inspection.UpstreamCode is null
            ? null
            : $"managed_upstream_{inspection.UpstreamCode.ToLowerInvariant()}";
        // Bounded shape diagnostics only. Without them a proxy failure carrying no recognizable
        // upstream code is indistinguishable from one whose body we simply failed to parse, and
        // triage cannot tell a gateway-level fault from a chrono-reported one. Never log the body,
        // upstream message, credential, service id or run id.
        _logger.LogWarning(
            "Managed Codex proxy failure. status={Status} bodyBytes={BodyBytes} bodyShape={BodyShape} upstreamCodeResolved={Resolved} diagnosticId={DiagnosticId}",
            status,
            inspection.BodyBytes,
            inspection.BodyShape,
            upstreamCode is not null,
            inspection.DiagnosticId);
        // A typed upstream code names the actual failing stage, so it normally classifies the failure.
        // Capacity is the exception: chrono's contract binds that code to 429, and any other
        // status/code combination is malformed producer output rather than evidence of capacity.
        // HTTP status alone cannot: chrono-sandbox returns 429 only for the capacity permit and
        // uses 502 for provisioning/OpenSandbox faults, so status-only mapping reports "capacity
        // unavailable" for sandbox-creation failures and sends triage the wrong way.
        if (upstreamCode is not null)
        {
            if (string.Equals(
                    upstreamCode,
                    "managed_upstream_codex_capacity_unavailable",
                    StringComparison.Ordinal) &&
                status != 429)
            {
                return Failure(
                    CodexExecutionFailureKind.MalformedOutput,
                    upstreamCode,
                    "Managed Codex returned an inconsistent upstream failure.",
                    inspection.DiagnosticId);
            }

            var upstreamFailure = ClassifyUpstreamFailure(upstreamCode);
            return Failure(
                upstreamFailure.Kind,
                upstreamCode,
                upstreamFailure.Message,
                inspection.DiagnosticId);
        }

        return status switch
        {
            401 => Failure(
                CodexExecutionFailureKind.AdmissionDenied,
                upstreamCode ?? "managed_proxy_authentication_failed",
                "Managed Codex proxy authentication failed.",
                inspection.DiagnosticId),
            403 => Failure(
                CodexExecutionFailureKind.AdmissionDenied,
                upstreamCode ?? "managed_proxy_authorization_denied",
                "Managed Codex proxy authorization was denied.",
                inspection.DiagnosticId),
            404 => Failure(
                CodexExecutionFailureKind.TargetNotConfigured,
                upstreamCode ?? "managed_proxy_target_unavailable",
                "Managed Codex proxy target is unavailable.",
                inspection.DiagnosticId),
            408 or 504 => Failure(
                CodexExecutionFailureKind.TimedOut,
                upstreamCode ?? "managed_proxy_timeout",
                "Managed Codex proxy request timed out.",
                inspection.DiagnosticId),
            429 => Failure(
                CodexExecutionFailureKind.CapacityUnavailable,
                upstreamCode ?? "managed_proxy_unavailable",
                "Managed Codex proxy is temporarily unavailable.",
                inspection.DiagnosticId),
            502 or 503 => Failure(
                CodexExecutionFailureKind.TerminalFailure,
                upstreamCode ?? "managed_proxy_unavailable",
                "Managed Codex proxy is temporarily unavailable.",
                inspection.DiagnosticId),
            _ => Failure(
                CodexExecutionFailureKind.TerminalFailure,
                upstreamCode ?? "managed_proxy_failed",
                "Managed Codex proxy request failed.",
                inspection.DiagnosticId),
        };
    }

    /// <summary>
    /// Classifies a proxy failure body into a fixed, non-sensitive shape label. The labels are a
    /// closed set derived from structure only — no upstream content is read into the result.
    /// </summary>
    private static string ClassifyBodyShape(string? response) =>
        ChronoProxyFailureInspector.ClassifyBodyShape(response);

    private static (CodexExecutionFailureKind Kind, string Message) ClassifyUpstreamFailure(
        string upstreamCode) =>
        upstreamCode switch
        {
            "managed_upstream_codex_caller_credential_forwarded" or
            "managed_upstream_codex_delegation_actor_invalid" or
            "managed_upstream_codex_delegation_audience_invalid" or
            "managed_upstream_codex_delegation_duplicated" or
            "managed_upstream_codex_delegation_expired" or
            "managed_upstream_codex_delegation_invalid" or
            "managed_upstream_codex_delegation_issuer_invalid" or
            "managed_upstream_codex_delegation_marker_invalid" or
            "managed_upstream_codex_delegation_missing" or
            "managed_upstream_codex_delegation_not_yet_valid" or
            "managed_upstream_codex_delegation_scope_invalid" or
            "managed_upstream_codex_delegation_subject_invalid" or
            "managed_upstream_codex_delegation_type_invalid" or
            "managed_upstream_codex_prompt_invalid" or
            "managed_upstream_codex_prompt_too_large" or
            "managed_upstream_codex_request_invalid" or
            "managed_upstream_codex_timeout_invalid" or
            "managed_upstream_codex_workspace_invalid" => (
                CodexExecutionFailureKind.AdmissionDenied,
                "Managed Codex request was rejected upstream."),
            "managed_upstream_codex_config_invalid" or
            "managed_upstream_codex_delegation_verifier_unavailable" => (
                CodexExecutionFailureKind.ReadinessFailed,
                "Managed Codex upstream readiness check failed."),
            "managed_upstream_codex_feature_disabled" => (
                CodexExecutionFailureKind.TargetNotConfigured,
                "Managed Codex upstream target is disabled."),
            "managed_upstream_codex_sandbox_creation_failed" or
            "managed_upstream_codex_workspace_preparation_failed" or
            "managed_upstream_codex_opensandbox_unavailable" => (
                CodexExecutionFailureKind.ProvisioningFailed,
                "Managed Codex sandbox provisioning failed upstream."),
            "managed_upstream_codex_opensandbox_timeout" or
            "managed_upstream_codex_sandbox_ready_timeout" or
            "managed_upstream_codex_execution_timeout" or
            "managed_upstream_codex_command_timeout" => (
                CodexExecutionFailureKind.TimedOut,
                "Managed Codex execution timed out upstream."),
            "managed_upstream_codex_capacity_unavailable" => (
                CodexExecutionFailureKind.CapacityUnavailable,
                "Managed Codex capacity is unavailable upstream."),
            "managed_upstream_codex_agent_message_missing" or
            "managed_upstream_codex_execd_terminal_missing" or
            "managed_upstream_codex_output_invalid" or
            "managed_upstream_codex_output_too_large" or
            "managed_upstream_codex_turn_terminal_missing" => (
                CodexExecutionFailureKind.MalformedOutput,
                "Managed Codex returned malformed output upstream."),
            "managed_upstream_codex_cleanup_unconfirmed" => (
                CodexExecutionFailureKind.CleanupFailed,
                "Managed Codex sandbox cleanup failed upstream."),
            "managed_upstream_codex_command_failed" or
            "managed_upstream_codex_turn_failed" => (
                CodexExecutionFailureKind.TerminalFailure,
                "Managed Codex execution failed upstream."),
            _ => (
                CodexExecutionFailureKind.TerminalFailure,
                "Managed Codex execution failed upstream."),
        };

    private static string? Redact(string? value, string rawKey) =>
        value?.Replace(rawKey, "[REDACTED]", StringComparison.Ordinal);

    private static ManagedCodexTransportException Failure(
        CodexExecutionFailureKind kind,
        string code,
        string message,
        string? diagnosticId = null) =>
        new(new CodexExecutionFailure(kind, code, message, diagnosticId));
}
