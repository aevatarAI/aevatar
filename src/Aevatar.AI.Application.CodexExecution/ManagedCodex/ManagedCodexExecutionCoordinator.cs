using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Application.CodexExecution;

public sealed class ManagedCodexExecutionCoordinator(
    IManagedCodexCredentialLifecycle lifecycle,
    IManagedCodexChronoTransport transport,
    ILogger<ManagedCodexExecutionCoordinator> logger) : ICodexExecutionPort
{
    private readonly IManagedCodexCredentialLifecycle _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    private readonly IManagedCodexChronoTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly ILogger<ManagedCodexExecutionCoordinator> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public CodexExecutionTarget.TargetOneofCase TargetKind =>
        CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

    public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
        CodexExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CodexExecutionEvent.Started();
        yield return await ExecuteTerminalAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<CodexExecutionEvent> ExecuteTerminalAsync(
        CodexExecutionRequest request,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var owner = ValidateRequestAndResolveOwner(request);
            var bearerToken = request.Caller.NyxIdAccessToken;
            var credential = await _lifecycle.EnsureReadyAsync(
                    owner,
                    bearerToken,
                    ManagedCodexCredentialReadinessMode.Normal,
                    ct)
                .ConfigureAwait(false);

            try
            {
                var result = await _transport.ExecuteAsync(request, credential, ct)
                    .ConfigureAwait(false);
                return CodexExecutionEvent.Completed(result);
            }
            catch (ManagedCodexTransportException exception)
                when (CanRepair(exception.Failure, bearerToken))
            {
                var repaired = await _lifecycle.EnsureReadyAsync(
                        owner,
                        bearerToken,
                        ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
                        ct)
                    .ConfigureAwait(false);
                var result = await _transport.ExecuteAsync(request, repaired, ct)
                    .ConfigureAwait(false);
                return CodexExecutionEvent.Completed(result);
            }
        }
        catch (ManagedCodexCredentialLifecycleException exception)
        {
            var failure = MapLifecycleFailure(exception);
            _logger.LogWarning(
                "Managed Codex credential readiness failed with code {FailureCode}",
                failure.Code);
            return CodexExecutionEvent.Failed(failure);
        }
        catch (ManagedCodexTransportException exception)
        {
            _logger.LogWarning(
                "Managed Codex transport failed with code {FailureCode}",
                exception.Failure.Code);
            return CodexExecutionEvent.Failed(exception.Failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CodexExecutionEvent.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.Cancelled,
                "managed_execution_cancelled",
                "Managed Codex execution was cancelled."));
        }
        catch (OperationCanceledException)
        {
            return CodexExecutionEvent.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.TimedOut,
                "managed_proxy_timeout",
                "Managed Codex proxy request timed out."));
        }
        catch (ManagedCodexRequestException exception)
        {
            return CodexExecutionEvent.Failed(exception.Failure);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Managed Codex execution failed with exception type {ExceptionType}",
                exception.GetType().Name);
            return CodexExecutionEvent.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.TerminalFailure,
                "managed_execution_failed",
                "Managed Codex execution failed."));
        }
    }

    private static ExternalSubjectRef ValidateRequestAndResolveOwner(
        CodexExecutionRequest? request)
    {
        if (request is null ||
            request.Target?.TargetCase != CodexExecutionTarget.TargetOneofCase.ManagedSandbox ||
            request.Workspace?.WorkspaceCase != CodexExecutionWorkspace.WorkspaceOneofCase.EmptyGit ||
            string.IsNullOrWhiteSpace(request.Prompt) ||
            request.TimeoutSeconds <= 0)
        {
            throw RequestFailure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_request_invalid",
                "Managed Codex execution request is invalid.");
        }

        var authority = request.Caller?.NyxIdAuthority;
        if (authority is null ||
            !string.Equals(
                authority.Platform?.Trim(),
                OwnerScope.NyxIdPlatform,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(authority.ExternalUserId))
        {
            throw RequestFailure(
                CodexExecutionFailureKind.AdmissionDenied,
                "managed_identity_unavailable",
                "A native NyxID user identity is required for managed Codex execution.");
        }

        return new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = authority.Tenant?.Trim() ?? string.Empty,
            ExternalUserId = authority.ExternalUserId.Trim(),
        };
    }

    private static bool CanRepair(
        CodexExecutionFailure failure,
        string? bearerToken) =>
        !string.IsNullOrWhiteSpace(bearerToken) &&
        failure.Code is
            "managed_proxy_authorization_denied" or
            "managed_credential_unavailable";

    private static CodexExecutionFailure MapLifecycleFailure(
        ManagedCodexCredentialLifecycleException exception)
    {
        var kind = exception.Code switch
        {
            "managed_target_disabled" =>
                CodexExecutionFailureKind.TargetNotConfigured,
            "managed_feature_not_enabled" or
            "nyxid_identity_mismatch" =>
                CodexExecutionFailureKind.AdmissionDenied,
            _ => CodexExecutionFailureKind.ProvisioningFailed,
        };
        return new CodexExecutionFailure(kind, exception.Code, exception.Message);
    }

    private static ManagedCodexRequestException RequestFailure(
        CodexExecutionFailureKind kind,
        string code,
        string message) =>
        new(new CodexExecutionFailure(kind, code, message));

    private sealed class ManagedCodexRequestException(CodexExecutionFailure failure)
        : Exception(failure.Message)
    {
        public CodexExecutionFailure Failure { get; } =
            failure ?? throw new ArgumentNullException(nameof(failure));
    }
}
