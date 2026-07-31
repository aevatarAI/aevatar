using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Application.CodexExecution;

public sealed class ManagedCodexExecutionCoordinator(
    IOptions<ManagedCodexOptions> options,
    IManagedCodexCredentialQueryPort queryPort,
    IManagedCodexChronoTransport transport,
    TimeProvider timeProvider,
    ILogger<ManagedCodexExecutionCoordinator> logger) : ICodexExecutionPort
{
    private readonly ManagedCodexOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IManagedCodexCredentialQueryPort _queryPort =
        queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    private readonly IManagedCodexChronoTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
            var now = _timeProvider.GetUtcNow();
            var policy = ManagedCodexCredentialReadiness.Assess(
                _options,
                owner,
                snapshot: null,
                now);
            if (policy.Reason is
                "managed_target_disabled" or
                "managed_feature_not_enabled")
            {
                return ReadinessFailed(policy, stateVersion: 0);
            }

            var snapshot = await _queryPort.ResolveAsync(owner, ct)
                .ConfigureAwait(false);
            var readiness = ManagedCodexCredentialReadiness.Assess(
                _options,
                owner,
                snapshot,
                now);
            if (!readiness.ExecutionReady)
            {
                return ReadinessFailed(
                    readiness,
                    snapshot?.StateVersion ?? 0);
            }

            var result = await _transport.ExecuteAsync(
                    request,
                    snapshot!.Credential.Clone(),
                    ct)
                .ConfigureAwait(false);
            return CodexExecutionEvent.Completed(result);
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
            !string.IsNullOrEmpty(authority.Tenant) ||
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
            Tenant = string.Empty,
            ExternalUserId = authority.ExternalUserId.Trim(),
        };
    }

    private CodexExecutionEvent ReadinessFailed(
        ManagedCodexCredentialReadinessAssessment readiness,
        long stateVersion)
    {
        var kind = readiness.Reason switch
        {
            "managed_target_disabled" =>
                CodexExecutionFailureKind.TargetNotConfigured,
            "managed_feature_not_enabled" =>
                CodexExecutionFailureKind.AdmissionDenied,
            _ => CodexExecutionFailureKind.ProvisioningFailed,
        };
        _logger.LogWarning(
            "Managed Codex credential is not execution-ready with code {FailureCode} at state version {StateVersion}",
            readiness.Reason,
            stateVersion);
        return CodexExecutionEvent.Failed(new CodexExecutionFailure(
            kind,
            readiness.Reason,
            readiness.Message));
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
