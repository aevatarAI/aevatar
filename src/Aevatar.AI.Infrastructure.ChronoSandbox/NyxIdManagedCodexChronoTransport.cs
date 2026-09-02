using System.Text.Json;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal sealed class NyxIdManagedCodexChronoTransport(
    IOptions<ManagedCodexOptions> options,
    INyxIdApiClientFactory clientFactory,
    ISecretVault secretVault,
    TimeProvider timeProvider) : IManagedCodexChronoTransport
{
    private readonly ManagedCodexOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly INyxIdApiClientFactory _clientFactory =
        clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly ISecretVault _secretVault =
        secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
        var response = await secret.UseAsync(rawKey => _clientFactory.CreateClient().ProxyRequestBoundedAsync(
                rawKey,
                ManagedCodexOptions.ChronoSandboxServiceSlug,
                credential.ChronoSandboxUserServiceId,
                ManagedCodexOptions.ChronoExecutionPath,
                HttpMethod.Post.Method,
                body,
                extraHeaders: null,
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
                throw ProxyFailure(response.HttpStatus);

            throw Failure(
                CodexExecutionFailureKind.CapacityUnavailable,
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

    private static CodexExecutionResult ParseResponse(string response, string rawKey)
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
                throw ProxyFailure(status);
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
            var exitCode = output.TryGetProperty("exit_code", out var exitCodeElement) &&
                           exitCodeElement.TryGetInt32(out var parsedExitCode)
                ? parsedExitCode
                : (int?)null;
            var elapsed = output.TryGetProperty("execution_time_ms", out var elapsedElement) &&
                          elapsedElement.TryGetInt64(out var parsedElapsed)
                ? parsedElapsed
                : (long?)null;
            var diagnosticId = root.TryGetProperty("diagnostic_id", out var diagnosticElement) &&
                               diagnosticElement.ValueKind == JsonValueKind.String
                ? Redact(diagnosticElement.GetString(), rawKey)
                : null;
            if (exitCode is not 0)
            {
                throw Failure(
                    CodexExecutionFailureKind.TerminalFailure,
                    "managed_execution_nonzero_exit",
                    "Managed Codex execution exited unsuccessfully.",
                    diagnosticId);
            }
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

    private static ManagedCodexTransportException ProxyFailure(int status) => status switch
    {
        401 or 403 => Failure(
            CodexExecutionFailureKind.AdmissionDenied,
            "managed_proxy_authorization_denied",
            "Managed Codex proxy authorization was denied."),
        404 => Failure(
            CodexExecutionFailureKind.TargetNotConfigured,
            "managed_proxy_target_unavailable",
            "Managed Codex proxy target is unavailable."),
        408 or 504 => Failure(
            CodexExecutionFailureKind.TimedOut,
            "managed_proxy_timeout",
            "Managed Codex proxy request timed out."),
        429 or 502 or 503 => Failure(
            CodexExecutionFailureKind.CapacityUnavailable,
            "managed_proxy_unavailable",
            "Managed Codex proxy is temporarily unavailable."),
        _ => Failure(
            CodexExecutionFailureKind.TerminalFailure,
            "managed_proxy_failed",
            "Managed Codex proxy request failed."),
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
