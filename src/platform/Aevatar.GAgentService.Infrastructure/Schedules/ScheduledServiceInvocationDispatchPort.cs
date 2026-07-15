using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class ScheduledServiceInvocationDispatchPort : IScheduledServiceInvocationDispatchPort
{
    private static readonly TimeSpan DurableCredentialProjectionTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProjectedCredentialCleanupTimeout = TimeSpan.FromSeconds(5);
    private const string LegacyConnectorHttpAuthorizationBlockedKey =
        ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey;

    private readonly IServiceInvocationPort _serviceInvocationPort;
    private readonly IScheduledServiceInvocationCredentialExchangePort _credentialExchangePort;
    private readonly ISecretVault? _secretVault;
    private readonly ILogger<ScheduledServiceInvocationDispatchPort> _logger;
    private readonly TimeProvider _timeProvider;

    public ScheduledServiceInvocationDispatchPort(
        IServiceInvocationPort serviceInvocationPort,
        IScheduledServiceInvocationCredentialExchangePort credentialExchangePort,
        ILogger<ScheduledServiceInvocationDispatchPort>? logger = null)
        : this(serviceInvocationPort, credentialExchangePort, secretVault: null, logger, timeProvider: null)
    {
    }

    public ScheduledServiceInvocationDispatchPort(
        IServiceInvocationPort serviceInvocationPort,
        IScheduledServiceInvocationCredentialExchangePort credentialExchangePort,
        ISecretVault? secretVault,
        ILogger<ScheduledServiceInvocationDispatchPort>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
        _credentialExchangePort = credentialExchangePort
            ?? throw new ArgumentNullException(nameof(credentialExchangePort));
        _secretVault = secretVault;
        _logger = logger ?? NullLogger<ScheduledServiceInvocationDispatchPort>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ScheduledServiceInvocationDispatchReceipt> DispatchAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(dispatch.Request);

        var prepared = await BuildInvocationRequestAsync(dispatch, ct);
        try
        {
            var request = WithScheduleId(prepared.Request, dispatch.ScheduleId);
            _logger.LogInformation(
                "Scheduled service invocation credential projection prepared. scheduleId={ScheduleId} serviceKey={ServiceKey} endpointId={EndpointId} hasConnectorAuthorization={HasConnectorAuthorization} hasOwnerLlmToken={HasOwnerLlmToken} hasSenderLlmToken={HasSenderLlmToken}",
                dispatch.ScheduleId ?? string.Empty,
                FormatServiceKey(request.Identity),
                request.EndpointId ?? string.Empty,
                HasConnectorAuthorization(request),
                HasOwnerLlmToken(request),
                HasSenderLlmToken(request));
            var receipt = await _serviceInvocationPort.InvokeAsync(request, ct);
            return new ScheduledServiceInvocationDispatchReceipt(
                true,
                receipt.CommandId ?? string.Empty,
                receipt.TargetActorId ?? string.Empty,
                receipt.CorrelationId ?? string.Empty);
        }
        catch
        {
            await TryRevokeProjectedCredentialAsync(
                prepared.DurableCallerCredential,
                "scheduled-workflow-dispatch-failed");
            throw;
        }
    }

    private async Task<PreparedInvocationRequest> BuildInvocationRequestAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct)
    {
        if (dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential &&
            dispatch.Auth?.Source is ScheduledInvocationAgentKeyCredentialReference agentKey)
        {
            return new PreparedInvocationRequest(
                EnrichChatPayload(
                    dispatch.Request,
                    dispatch.Headers,
                    new ExchangedCredential(
                        CredentialRole.ScheduledInvocationAgentKey,
                        string.Empty,
                        CreateBorrowedDurableCallerCredential(agentKey)),
                    projectNyxIdAccessTokenToWorkflowCallerCredential: true),
                DurableCallerCredential: null);
        }

        if (dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential &&
            TryResolveWorkflowCallerAuthority(dispatch.Auth, out var authority))
        {
            return new PreparedInvocationRequest(
                EnrichChatPayload(
                    dispatch.Request,
                    dispatch.Headers,
                    new ExchangedCredential(
                        ResolveCredentialRole(dispatch.Auth),
                        string.Empty,
                        new DurableCallerCredentialRef
                        {
                            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
                            ScheduledCallerNyxIdAuthority = authority,
                        }),
                    projectNyxIdAccessTokenToWorkflowCallerCredential: true),
                DurableCallerCredential: null);
        }

        var exchange = await ExchangeCredentialAsync(dispatch, ct);
        if (exchange == null)
        {
            return new PreparedInvocationRequest(
                EnrichChatPayload(
                    dispatch.Request,
                    dispatch.Headers,
                    credential: null,
                    projectNyxIdAccessTokenToWorkflowCallerCredential:
                        dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential),
                DurableCallerCredential: null);
        }

        if (!exchange.Result.Succeeded)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(exchange.Result.Error)
                ? $"Scheduled service invocation {ToErrorSubject(exchange.Role)} NyxID credential exchange failed."
                : exchange.Result.Error.Trim());
        }

        _logger.LogInformation(
            "Scheduled service invocation NyxID credential exchange succeeded. scheduleId={ScheduleId} serviceKey={ServiceKey} endpointId={EndpointId} credentialRole={CredentialRole} projectWorkflowCallerCredential={ProjectWorkflowCallerCredential} hasAccessToken={HasAccessToken}",
            dispatch.ScheduleId ?? string.Empty,
            FormatServiceKey(dispatch.Request.Identity),
            dispatch.Request.EndpointId ?? string.Empty,
            ToErrorSubject(exchange.Role),
            dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential,
            !string.IsNullOrWhiteSpace(exchange.Result.AccessToken));

        var token = NormalizeNyxIdAccessToken(exchange.Result.AccessToken, exchange.Role);
        var durableCallerCredential = dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential &&
                                      exchange.Role != CredentialRole.ScheduledInvocationAgentKey
            ? await StoreDurableCallerCredentialAsync(
                dispatch,
                exchange.Role,
                token,
                ResolveProjectedCredentialExpiry(exchange),
                ct)
            : null;

        try
        {
            return new PreparedInvocationRequest(
                EnrichChatPayload(
                    dispatch.Request,
                    dispatch.Headers,
                    new ExchangedCredential(
                        exchange.Role,
                        token,
                        durableCallerCredential),
                    dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential),
                durableCallerCredential);
        }
        catch
        {
            await TryRevokeProjectedCredentialAsync(
                durableCallerCredential,
                "scheduled-workflow-dispatch-preparation-failed");
            throw;
        }
    }

    private static DurableCallerCredentialRef CreateBorrowedDurableCallerCredential(
        ScheduledInvocationAgentKeyCredentialReference source)
    {
        var reference = source.SecretReference;
        if (string.IsNullOrWhiteSpace(reference.Ref))
            throw new InvalidOperationException("Scheduled invocation agent key secret reference is missing.");
        if (string.IsNullOrWhiteSpace(reference.Purpose))
            throw new InvalidOperationException("Scheduled invocation agent key secret reference purpose is missing.");
        if (string.IsNullOrWhiteSpace(reference.OwnerScopeKey))
            throw new InvalidOperationException("Scheduled invocation agent key owner scope is missing.");
        if (string.IsNullOrWhiteSpace(source.ApiKeyId))
            throw new InvalidOperationException("Scheduled invocation agent key id is missing.");

        return new DurableCallerCredentialRef
        {
            Ref = reference.Ref,
            Purpose = reference.Purpose,
            OwnerScopeKey = reference.OwnerScopeKey,
            SubjectId = source.ApiKeyId,
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };
    }

    private async Task<DurableCallerCredentialRef> StoreDurableCallerCredentialAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CredentialRole role,
        string token,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        if (_secretVault == null)
            throw new InvalidOperationException("Scheduled workflow caller credential vault is not configured.");

        var ownerScopeKey = ResolveOwnerScopeKey(dispatch);
        var subjectId = ResolveSubjectId(dispatch.Auth, role);
        var callerAuthority = ResolveScheduledCallerNyxIdAuthority(dispatch.Auth, role);
        var stored = await _secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            ownerScopeKey,
            subjectId,
            token,
            "scheduled-workflow-caller-credential",
            expiresAt),
            ct);

        return new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = stored.Reference.Purpose,
            OwnerScopeKey = stored.Reference.OwnerScopeKey,
            SubjectId = subjectId,
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
            ScheduledCallerNyxIdAuthority = callerAuthority,
        };
    }

    private async Task TryRevokeProjectedCredentialAsync(
        DurableCallerCredentialRef? reference,
        string auditReason)
    {
        if (reference == null || _secretVault == null)
            return;

        var cleanupCts = new CancellationTokenSource();
        var revokeTask = RevokeProjectedCredentialAsync(
            reference,
            auditReason,
            cleanupCts.Token);
        try
        {
            await revokeTask.WaitAsync(ProjectedCredentialCleanupTimeout, _timeProvider);
            cleanupCts.Dispose();
        }
        catch (TimeoutException ex)
        {
            RequestProjectedCredentialCleanupCancellation(cleanupCts, reference.Ref);
            _logger.LogWarning(
                ex,
                "Scheduled workflow caller credential cleanup timed out after {TimeoutSeconds}s. credentialRef={CredentialRef}",
                ProjectedCredentialCleanupTimeout.TotalSeconds,
                reference.Ref);
        }
        catch (Exception ex)
        {
            RequestProjectedCredentialCleanupCancellation(cleanupCts, reference.Ref);
            _logger.LogWarning(
                ex,
                "Scheduled workflow caller credential cleanup boundary failed. credentialRef={CredentialRef}",
                reference.Ref);
        }
    }

    private async Task RevokeProjectedCredentialAsync(
        DurableCallerCredentialRef reference,
        string auditReason,
        CancellationToken ct)
    {
        try
        {
            await _secretVault!.RevokeAsync(new RevokeSecretRequest(
                reference.Ref,
                CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                reference.OwnerScopeKey,
                reference.SubjectId,
                auditReason), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The short-lived credential's backend TTL remains the durable cleanup fallback.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Scheduled workflow caller credential cleanup failed after dispatch failure. credentialRef={CredentialRef}",
                reference.Ref);
        }
    }

    private void RequestProjectedCredentialCleanupCancellation(
        CancellationTokenSource cleanupCts,
        string credentialRef)
    {
        try
        {
            _ = ObserveProjectedCredentialCleanupCancellationAsync(
                cleanupCts.CancelAsync(),
                cleanupCts,
                credentialRef);
        }
        catch (Exception ex)
        {
            cleanupCts.Dispose();
            _logger.LogWarning(
                ex,
                "Scheduled workflow caller credential cleanup cancellation failed. credentialRef={CredentialRef}",
                credentialRef);
        }
    }

    private async Task ObserveProjectedCredentialCleanupCancellationAsync(
        Task cancellationTask,
        CancellationTokenSource cleanupCts,
        string credentialRef)
    {
        try
        {
            await cancellationTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Scheduled workflow caller credential cleanup cancellation failed. credentialRef={CredentialRef}",
                credentialRef);
        }
        finally
        {
            cleanupCts.Dispose();
        }
    }

    private static ServiceInvocationRequest WithScheduleId(ServiceInvocationRequest request, string? scheduleId)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
            return request;

        if (string.Equals(request.ScheduleId, scheduleId.Trim(), StringComparison.Ordinal))
            return request;

        var cloned = request.Clone();
        cloned.ScheduleId = scheduleId.Trim();
        return cloned;
    }

    private async Task<CredentialExchange?> ExchangeCredentialAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct)
    {
        if (dispatch.Auth?.Source == null)
            return null;

        if (dispatch.Auth.Source is ScheduledInvocationAgentKeyCredentialReference agentKey)
        {
            var result = await ResolveScheduledInvocationAgentKeyAsync(agentKey, ct);
            return new CredentialExchange(CredentialRole.ScheduledInvocationAgentKey, result);
        }

        if (dispatch.Auth.Source is ScheduledServiceInvocationDurableCredentialReference)
        {
            var durableResult = await ResolveDurableCredentialReferenceAsync(
                (ScheduledServiceInvocationDurableCredentialReference)dispatch.Auth.Source,
                ct);
            return new CredentialExchange(CredentialRole.DurableSender, durableResult);
        }

        if (dispatch.Auth.Source is ScheduledServiceInvocationIdentityCredentialSource nyxId)
        {
            var result = await _credentialExchangePort.IssueAsync(nyxId, ct);
            return new CredentialExchange(ToCredentialRole(nyxId.Role), result);
        }

        throw new InvalidOperationException("Scheduled service invocation credential source is not supported.");
    }

    private async Task<ScheduledServiceInvocationCredentialExchangeResult> ResolveDurableCredentialReferenceAsync(
        ScheduledServiceInvocationDurableCredentialReference credential,
        CancellationToken ct)
    {
        if (_secretVault == null)
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential vault is not configured.");

        var secretReference = credential.SecretReference;
        if (secretReference == null ||
            string.IsNullOrWhiteSpace(credential.CredentialId) ||
            string.IsNullOrWhiteSpace(secretReference.Ref) ||
            string.IsNullOrWhiteSpace(secretReference.OwnerScopeKey))
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential reference is incomplete.");
        }

        if (!string.Equals(secretReference.Purpose, CredentialSecretPurposes.ScheduledNyxApiKey, StringComparison.Ordinal))
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential reference purpose is invalid.");
        }

        var resolved = await _secretVault.ResolveAsync(
            new ResolveSecretRequest(
                secretReference.Ref.Trim(),
                CredentialSecretPurposes.ScheduledNyxApiKey,
                secretReference.OwnerScopeKey.Trim(),
                credential.CredentialId.Trim(),
                "scheduled-dispatch-fire"),
            ct);
        if (!resolved.Resolved)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential reference could not be resolved.");
        }

        return ScheduledServiceInvocationCredentialExchangeResult.Success(
            resolved.Secret!,
            secretReference.ExpiresAtUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(secretReference.ExpiresAtUnixMs)
                : null);
    }

    private async Task<ScheduledServiceInvocationCredentialExchangeResult> ResolveScheduledInvocationAgentKeyAsync(
        ScheduledInvocationAgentKeyCredentialReference source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();

        if (_secretVault == null)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled invocation agent key resolver is not configured.");
        }

        var reference = source.SecretReference;
        var expiresAtUnixMs = source.KeyExpiresAtUnixMs > 0
            ? source.KeyExpiresAtUnixMs
            : reference.ExpiresAtUnixMs;
        if (expiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled invocation agent key is expired.");
        }

        try
        {
            var accessToken = await ResolveScheduledInvocationAgentKeySecretAsync(source, ct);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                    "Scheduled invocation agent key could not be resolved.");
            }

            return ScheduledServiceInvocationCredentialExchangeResult.Success(
                accessToken,
                DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled invocation agent key resolve failed.");
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled invocation agent key resolve failed.");
        }
    }

    private async Task<string?> ResolveScheduledInvocationAgentKeySecretAsync(
        ScheduledInvocationAgentKeyCredentialReference source,
        CancellationToken ct)
    {
        var reference = source.SecretReference;
        if (string.IsNullOrWhiteSpace(reference.Ref))
            throw new InvalidOperationException("Scheduled invocation agent key secret reference is missing.");

        if (!string.Equals(
                reference.Purpose,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Scheduled invocation agent key secret reference purpose is invalid.");
        }

        var apiKeyId = source.ApiKeyId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKeyId))
            throw new InvalidOperationException("Scheduled invocation agent key id is missing.");

        var ownerScopeKey = reference.OwnerScopeKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ownerScopeKey))
            throw new InvalidOperationException("Scheduled invocation agent key owner scope is missing.");

        var resolved = await _secretVault!.ResolveAsync(new ResolveSecretRequest(
                reference.Ref,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                ownerScopeKey,
                apiKeyId,
                "scheduled-service-invocation-dispatch"),
            ct);
        return resolved.Resolved ? resolved.Secret : null;
    }

    private static ServiceInvocationRequest EnrichChatPayload(
        ServiceInvocationRequest request,
        IReadOnlyDictionary<string, string>? headers,
        ExchangedCredential? credential,
        bool projectNyxIdAccessTokenToWorkflowCallerCredential)
    {
        var sanitizedRequest = ScheduledServiceInvocationPayloadPolicy.StripScheduleOwnedCredentialFields(request);
        if ((headers == null || headers.Count == 0) && credential == null)
            return sanitizedRequest;

        var cloned = sanitizedRequest.Clone();
        if (cloned.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return cloned;

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                if (string.Equals(key, LegacyConnectorHttpAuthorizationBlockedKey, StringComparison.Ordinal))
                    continue;

                chatRequest.Metadata[key] = value;
            }
        }

        if (credential != null)
        {
            var token = credential.AccessToken;
            var existingControl = LLMControlContextMapper.FromPayload(chatRequest.LlmControl);
            var ownerCredential = credential.Role is CredentialRole.ScopeOwner or CredentialRole.ScheduledInvocationAgentKey;
            var projectWorkflowCallerCredential =
                projectNyxIdAccessTokenToWorkflowCallerCredential;
            var control = projectWorkflowCallerCredential
                ? existingControl with
                {
                    CredentialRef = null,
                    OrganizationCredentialRef = null,
                    SenderCredentialRef = null,
                }
                : existingControl with
                {
                    CredentialRef = ownerCredential
                        ? token
                        : existingControl.CredentialRef,
                    OrganizationCredentialRef = ownerCredential
                        ? token
                        : existingControl.OrganizationCredentialRef,
                    SenderCredentialRef = IsSenderCredential(credential.Role)
                        ? token
                        : existingControl.SenderCredentialRef,
                };
            chatRequest.LlmControl = control.ToPayload();
            if (projectWorkflowCallerCredential)
            {
                chatRequest.ConnectorHttpAuthorization = string.Empty;
                chatRequest.CallerDurableCredential = credential.DurableCallerCredential?.Clone();
            }
        }

        cloned.Payload = Any.Pack(chatRequest);
        return cloned;
    }

    private static bool HasConnectorAuthorization(ServiceInvocationRequest request)
    {
        if (request.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return false;

        return !string.IsNullOrWhiteSpace(chatRequest.ConnectorHttpAuthorization);
    }

    private static bool HasOwnerLlmToken(ServiceInvocationRequest request)
    {
        if (request.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return false;

        return !string.IsNullOrWhiteSpace(chatRequest.LlmControl?.CredentialRef) ||
               !string.IsNullOrWhiteSpace(chatRequest.LlmControl?.OrganizationCredentialRef);
    }

    private static bool HasSenderLlmToken(ServiceInvocationRequest request)
    {
        if (request.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return false;

        return !string.IsNullOrWhiteSpace(chatRequest.LlmControl?.SenderCredentialRef);
    }

    private static string FormatServiceKey(ServiceIdentity? identity) =>
        identity == null
            ? string.Empty
            : $"{identity.TenantId}:{identity.AppId}:{identity.Namespace}:{identity.ServiceId}";

    private static string NormalizeNyxIdAccessToken(string? accessToken, CredentialRole role)
    {
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(accessToken);
        if (parsed.IsMissing)
            throw new InvalidOperationException(ToEmptyTokenError(role));
        if (parsed.IsInvalid)
            throw new InvalidOperationException(ToInvalidTokenError(role));

        return parsed.NormalizedBearerToken!;
    }

    private DateTimeOffset ResolveProjectedCredentialExpiry(CredentialExchange exchange)
    {
        var now = _timeProvider.GetUtcNow();
        // Prefer exchange-provided expiry; fall back to a short host-owned TTL so projected
        // vault entries never become unbounded when the broker omits exp.
        var expiresAt = exchange.Result.ExpiresAt ?? now.Add(DurableCredentialProjectionTtl);
        if (expiresAt <= now)
        {
            throw new InvalidOperationException(
                $"Scheduled service invocation {ToErrorSubject(exchange.Role)} credential exchange returned an expired credential.");
        }

        return expiresAt;
    }

    private enum CredentialRole
    {
        Sender,
        ScopeOwner,
        DurableSender,
        ScheduledInvocationAgentKey,
    }

    private sealed record CredentialExchange(
        CredentialRole Role,
        ScheduledServiceInvocationCredentialExchangeResult Result);

    private sealed record PreparedInvocationRequest(
        ServiceInvocationRequest Request,
        DurableCallerCredentialRef? DurableCallerCredential);

    private static string ResolveOwnerScopeKey(ScheduledServiceInvocationDispatchRequest dispatch)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ScheduleId))
            return $"schedule:{dispatch.ScheduleId.Trim()}";
        if (!string.IsNullOrWhiteSpace(dispatch.Request.Identity?.TenantId))
            return $"tenant:{dispatch.Request.Identity.TenantId.Trim()}";

        return $"service:{FormatServiceKey(dispatch.Request.Identity)}";
    }

    private static string ResolveSubjectId(
        ScheduledServiceInvocationAuth? auth,
        CredentialRole role)
    {
        if (role == CredentialRole.DurableSender &&
            auth?.Source is ScheduledServiceInvocationDurableCredentialReference durable &&
            !string.IsNullOrWhiteSpace(durable.CredentialId))
        {
            return durable.CredentialId.Trim();
        }

        var subject = role == CredentialRole.ScopeOwner
            ? auth?.ScopeOwnerIdentity?.OwnerSubject
            : auth?.SenderIdentity?.Subject;
        if (subject == null)
        {
            return role switch
            {
                CredentialRole.ScopeOwner => "scope-owner",
                CredentialRole.ScheduledInvocationAgentKey => "scheduled-invocation-agent-key",
                CredentialRole.DurableSender => "durable",
                _ => "sender",
            };
        }

        return string.Join(
            ":",
            (subject.Platform ?? string.Empty).Trim(),
            (subject.Tenant ?? string.Empty).Trim(),
            (subject.ExternalUserId ?? string.Empty).Trim());
    }

    private static ScheduledCallerNyxIdAuthority? ResolveScheduledCallerNyxIdAuthority(
        ScheduledServiceInvocationAuth? auth,
        CredentialRole role)
    {
        if (role is not (CredentialRole.Sender or CredentialRole.ScopeOwner))
            return null;

        var source = auth?.Identity;
        var subject = source?.Subject;
        var platform = subject?.Platform?.Trim() ?? string.Empty;
        var externalUserId = subject?.ExternalUserId?.Trim() ?? string.Empty;
        var scope = source?.Scope?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(platform) ||
            string.IsNullOrWhiteSpace(externalUserId) ||
            string.IsNullOrWhiteSpace(scope))
        {
            throw new InvalidOperationException(
                "Scheduled workflow NyxID caller authority is incomplete.");
        }

        return new ScheduledCallerNyxIdAuthority
        {
            Platform = platform,
            Tenant = subject?.Tenant?.Trim() ?? string.Empty,
            ExternalUserId = externalUserId,
            Scope = scope,
        };
    }

    private static bool TryResolveWorkflowCallerAuthority(
        ScheduledServiceInvocationAuth? auth,
        out ScheduledCallerNyxIdAuthority authority)
    {
        var role = ResolveCredentialRole(auth);
        authority = ResolveScheduledCallerNyxIdAuthority(auth, role) ?? new ScheduledCallerNyxIdAuthority();
        return role is CredentialRole.Sender or CredentialRole.ScopeOwner;
    }

    private static CredentialRole ResolveCredentialRole(ScheduledServiceInvocationAuth? auth) =>
        auth?.Source switch
        {
            ScheduledServiceInvocationIdentityCredentialSource nyxId => ToCredentialRole(nyxId.Role),
            ScheduledServiceInvocationDurableCredentialReference => CredentialRole.DurableSender,
            ScheduledInvocationAgentKeyCredentialReference => CredentialRole.ScheduledInvocationAgentKey,
            _ => CredentialRole.Sender,
        };

    private sealed record ExchangedCredential(
        CredentialRole Role,
        string AccessToken,
        DurableCallerCredentialRef? DurableCallerCredential);

    private static string ToErrorSubject(CredentialRole role) =>
        role switch
        {
            CredentialRole.ScopeOwner => "scope owner",
            CredentialRole.DurableSender => "durable",
            CredentialRole.ScheduledInvocationAgentKey => "scheduled invocation agent key",
            _ => "sender",
        };

    private static bool IsSenderCredential(CredentialRole role) =>
        role is CredentialRole.Sender or CredentialRole.DurableSender;

    private static string ToEmptyTokenError(CredentialRole role) =>
        role switch
        {
            CredentialRole.DurableSender =>
                "Scheduled service invocation durable credential reference resolved an empty access token.",
            CredentialRole.ScheduledInvocationAgentKey =>
                "Scheduled invocation agent key resolved an empty access token.",
            _ =>
                $"Scheduled service invocation {ToErrorSubject(role)} NyxID credential exchange returned an empty access token.",
        };

    private static string ToInvalidTokenError(CredentialRole role) =>
        role switch
        {
            CredentialRole.DurableSender =>
                "Scheduled service invocation durable credential reference resolved an invalid access token.",
            CredentialRole.ScheduledInvocationAgentKey =>
                "Scheduled invocation agent key resolved an invalid access token.",
            _ =>
                $"Scheduled service invocation {ToErrorSubject(role)} NyxID credential exchange returned an invalid access token.",
        };

    private static CredentialRole ToCredentialRole(ScheduledServiceInvocationIdentityCredentialRole role) =>
        role == ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner
            ? CredentialRole.ScopeOwner
            : CredentialRole.Sender;
}
