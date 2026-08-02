using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class ScheduledServiceInvocationDispatchPort : IScheduledServiceInvocationDispatchPort
{
    private static readonly TimeSpan DurableCredentialProjectionTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProjectedCredentialCleanupTimeout = TimeSpan.FromSeconds(5);

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
        ValidateAuthorizationFact(dispatch);
        ValidateWorkflowAgentKeyIntegrity(dispatch);

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
        catch (WorkflowExternalCapabilityAdmissionException ex)
        {
            await TryRevokeProjectedCredentialAsync(
                prepared.DurableCallerCredential,
                "scheduled-workflow-dispatch-failed");
            throw new ScheduledWorkflowAdmissionException(ex.StableCode, ex.SafeMessage);
        }
        catch
        {
            await TryRevokeProjectedCredentialAsync(
                prepared.DurableCallerCredential,
                "scheduled-workflow-dispatch-failed");
            throw;
        }
    }

    private void ValidateAuthorizationFact(ScheduledServiceInvocationDispatchRequest dispatch)
    {
        var fact = dispatch.AuthorizationFact;
        if (fact == null &&
            dispatch.Auth?.Source is not ScheduledInvocationAgentKeyCredentialReference)
            return;

        if (fact == null ||
            IsCoreAuthorizationFactInvalid(fact, _timeProvider.GetUtcNow()) ||
            RequiresCatalogAuthority(fact) && IsCatalogAuthorityInvalid(fact.Authority) ||
            AreServiceGrantsInvalid(fact) ||
            IsDisclosureInvalid(fact.Disclosure))
        {
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.AuthorizationFactInvalid,
                "Scheduled invocation authorization fact is missing, stale, or malformed.");
        }

        if (dispatch.Auth?.Source is not ScheduledInvocationAgentKeyCredentialReference agentKey ||
            agentKey.KeyExpiresAtUnixMs <= 0 ||
            DateTimeOffset.FromUnixTimeMilliseconds(agentKey.KeyExpiresAtUnixMs) > fact.ExpiresAt)
        {
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.AuthorizationFactInvalid,
                "Scheduled invocation authorization fact requires a constrained scheduled agent key.");
        }
    }

    private static bool IsCoreAuthorizationFactInvalid(
        ScheduledInvocationAuthorizationFact fact,
        DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(fact.PermissionDigest) ||
        string.IsNullOrWhiteSpace(fact.PolicyVersion) ||
        string.IsNullOrWhiteSpace(fact.Owner.Authority) ||
        string.IsNullOrWhiteSpace(fact.Owner.OwnerSubject) ||
        string.IsNullOrWhiteSpace(fact.Scopes) ||
        fact.ExpiresAt <= now;

    private static bool IsCatalogAuthorityInvalid(ScheduledInvocationAuthorizationAuthority authority) =>
        authority.CatalogStateVersion <= 0 ||
        string.IsNullOrWhiteSpace(authority.CatalogContentDigest) ||
        string.IsNullOrWhiteSpace(authority.CatalogContractVersion) ||
        string.IsNullOrWhiteSpace(authority.CatalogPolicyVersion) ||
        authority.CatalogEvaluatedAt == default;

    private static bool RequiresCatalogAuthority(ScheduledInvocationAuthorizationFact fact) =>
        !fact.ServiceGrantsNotRequired ||
        fact.ServiceGrants.Count > 0 ||
        fact.Authority.OwnerLlmStateVersion > 0 ||
        fact.OwnerLLMSelection != null;

    private static bool AreServiceGrantsInvalid(ScheduledInvocationAuthorizationFact fact) =>
        fact.ServiceGrants.Count == 0 && !fact.ServiceGrantsNotRequired ||
        fact.ServiceGrants.Any(IsServiceGrantInvalid);

    private static bool IsServiceGrantInvalid(ScheduledInvocationAuthorizationServiceGrant grant) =>
        string.IsNullOrWhiteSpace(grant.ServiceId) ||
        grant.NodeIds == null ||
        grant.NodeIds.Any(string.IsNullOrWhiteSpace) ||
        grant.NodeGrantsNotRequired && grant.NodeIds.Count != 0 ||
        !grant.NodeGrantsNotRequired && grant.NodeIds.Count == 0;

    private static bool IsDisclosureInvalid(ScheduledInvocationAuthorizationDisclosure disclosure) =>
        !disclosure.DedicatedToSchedule ||
        !disclosure.SecretManagedByAevatar ||
        disclosure.BrowserReceivesRawKey;

    private static void ValidateWorkflowAgentKeyIntegrity(
        ScheduledServiceInvocationDispatchRequest dispatch)
    {
        if (!dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential ||
            dispatch.Auth?.Source is not ScheduledInvocationAgentKeyCredentialReference)
        {
            return;
        }

        var authority = dispatch.Auth.CallerAuthority;
        if (authority == null ||
            !IsCanonicalAuthorityValue(authority.Platform) ||
            !IsCanonicalAuthorityValue(authority.ExternalUserId) ||
            !IsCanonicalAuthorityValue(authority.Scope) ||
            !IsCanonicalAuthorityValue(authority.BindingId))
        {
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CallerAuthorityInvalid,
                "Scheduled workflow Agent Key caller authority is missing or malformed.");
        }

        ValidateOwnerLLMSelectionAndPayload(dispatch.Request, dispatch.AuthorizationFact!);
    }

    private static bool IsCanonicalAuthorityValue(string? value) =>
        !string.IsNullOrEmpty(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static void ValidateOwnerLLMSelectionAndPayload(
        ServiceInvocationRequest request,
        ScheduledInvocationAuthorizationFact fact)
    {
        var chatRequest = new ChatRequestEvent();
        bool hasChatPayload;
        try
        {
            hasChatPayload = request.Payload?.TryUnpack(out chatRequest) == true;
        }
        catch (InvalidProtocolBufferException)
        {
            ThrowOwnerLLMPayloadMismatch();
            return;
        }

        if (!hasChatPayload)
        {
            ThrowOwnerLLMPayloadMismatch();
            return;
        }

        var control = chatRequest?.LlmControl;
        var route = control?.NyxIdRoutePreference ?? string.Empty;
        var model = control?.ModelOverride ?? string.Empty;

        if (fact.Authority.OwnerLlmStateVersion <= 0)
        {
            if (route.Length > 0 || model.Length > 0)
                ThrowOwnerLLMPayloadMismatch();
            return;
        }

        var selection = fact.OwnerLLMSelection;
        if (!IsValidExplicitOwnerLLMSelection(selection) ||
            (selection!.RouteKind == LLMRouteKind.NyxIdUserService &&
             !fact.ServiceGrants.Any(grant => string.Equals(
                 grant.ServiceId,
                 selection.NyxIdUserServiceId,
                 StringComparison.Ordinal))))
        {
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.OwnerLLMSelectionInvalid,
                "Scheduled workflow owner LLM selection is missing or malformed.");
        }

        if (!string.Equals(route, selection.RouteValue, StringComparison.Ordinal) ||
            !string.Equals(model, selection.Model, StringComparison.Ordinal))
        {
            ThrowOwnerLLMPayloadMismatch();
        }
    }

    private static bool IsValidExplicitOwnerLLMSelection(
        ScheduledInvocationOwnerLLMSelection? selection)
    {
        if (!ScheduledInvocationOwnerLLMSelectionPolicy.IsDurableSelectionValid(selection))
            return false;

        try
        {
            LLMSelectionPolicy.ValidateSelection(new LLMSelection
            {
                RouteKind = selection!.RouteKind,
                RouteValue = selection.RouteValue,
                NyxIdUserServiceId = selection.NyxIdUserServiceId,
                ServiceSlugSnapshot = selection.ServiceSlugSnapshot,
                ModelSelection = new LLMModelSelection
                {
                    Kind = LLMModelSelectionKind.ExplicitModel,
                    ModelId = selection.Model,
                },
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ThrowOwnerLLMPayloadMismatch() =>
        throw new ScheduledServiceInvocationAuthorizationException(
            ScheduledServiceInvocationAuthorizationFailureCode.OwnerLLMPayloadMismatch,
            "Scheduled workflow owner LLM payload does not match the authorization fact.");

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
                        CreateBorrowedDurableCallerCredential(agentKey, dispatch.Auth.CallerAuthority)),
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
            if (exchange.Result.AuthorizationFailureCode is { } authorizationFailureCode)
            {
                throw new ScheduledServiceInvocationAuthorizationException(
                    authorizationFailureCode,
                    string.IsNullOrWhiteSpace(exchange.Result.Error)
                        ? $"Scheduled service invocation {ToErrorSubject(exchange.Role)} credential resolution failed."
                        : exchange.Result.Error.Trim());
            }
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
        ScheduledInvocationAgentKeyCredentialReference source,
        ScheduledCallerNyxIdAuthority? callerAuthority)
    {
        var reference = source.SecretReference;
        if (string.IsNullOrWhiteSpace(reference.Ref))
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceMissing,
                "Scheduled invocation agent key secret reference is missing.");
        if (!string.Equals(
                reference.Purpose,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                StringComparison.Ordinal))
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid,
                "Scheduled invocation agent key secret reference purpose is invalid.");
        if (string.IsNullOrWhiteSpace(reference.OwnerScopeKey))
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid,
                "Scheduled invocation agent key owner scope is missing.");
        if (string.IsNullOrWhiteSpace(source.ApiKeyId))
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.ApiKeyIdMissing,
                "Scheduled invocation agent key id is missing.");

        return new DurableCallerCredentialRef
        {
            Ref = reference.Ref,
            Purpose = reference.Purpose,
            OwnerScopeKey = reference.OwnerScopeKey,
            SubjectId = source.ApiKeyId,
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
            ScheduledCallerNyxIdAuthority = NormalizeScheduledCallerNyxIdAuthority(callerAuthority),
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
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialVaultUnavailable,
                "Scheduled workflow caller credential vault is not configured.");

        var ownerScopeKey = ResolveOwnerScopeKey(dispatch);
        var subjectId = ResolveSubjectId(dispatch.Auth, role);
        var callerAuthority = NormalizeScheduledCallerNyxIdAuthority(dispatch.Auth?.CallerAuthority);
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

        if (dispatch.Auth.Source is ScheduledServiceInvocationNyxIdCredentialSource nyxId)
        {
            var result = await _credentialExchangePort.IssueNyxIdAsync(nyxId, ct);
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
                "Scheduled service invocation durable credential vault is not configured.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialVaultUnavailable);

        var secretReference = credential.SecretReference;
        if (secretReference == null ||
            string.IsNullOrWhiteSpace(credential.CredentialId) ||
            string.IsNullOrWhiteSpace(secretReference.Ref) ||
            string.IsNullOrWhiteSpace(secretReference.OwnerScopeKey))
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential reference is incomplete.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceMissing);
        }

        if (!string.Equals(secretReference.Purpose, CredentialSecretPurposes.ScheduledNyxApiKey, StringComparison.Ordinal))
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential reference purpose is invalid.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid);
        }

        ResolveSecretResult resolved;
        try
        {
            resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    secretReference.Ref.Trim(),
                    CredentialSecretPurposes.ScheduledNyxApiKey,
                    secretReference.OwnerScopeKey.Trim(),
                    credential.CredentialId.Trim(),
                    "scheduled-dispatch-fire"),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled durable credential vault resolve failed.");
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential vault is unavailable.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialVaultUnavailable);
        }
        if (!resolved.Resolved)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential reference could not be resolved.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialUnresolvable);
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
                "Scheduled invocation agent key resolver is not configured.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialVaultUnavailable);
        }

        var reference = source.SecretReference;
        var expiresAtUnixMs = source.KeyExpiresAtUnixMs > 0
            ? source.KeyExpiresAtUnixMs
            : reference.ExpiresAtUnixMs;
        if (expiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled invocation agent key is expired.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialExpired);
        }

        try
        {
            var accessToken = await ResolveScheduledInvocationAgentKeySecretAsync(source, ct);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                    "Scheduled invocation agent key could not be resolved.",
                    ScheduledServiceInvocationAuthorizationFailureCode.CredentialUnresolvable);
            }

            return ScheduledServiceInvocationCredentialExchangeResult.Success(
                accessToken,
                DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ScheduledServiceInvocationAuthorizationException ex)
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(ex.Message, ex.Code);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Scheduled invocation agent key vault resolve failed.");
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled invocation agent key vault is unavailable.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialVaultUnavailable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled invocation agent key resolve failed.");
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled invocation agent key vault is unavailable.",
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialVaultUnavailable);
        }
    }

    private async Task<string?> ResolveScheduledInvocationAgentKeySecretAsync(
        ScheduledInvocationAgentKeyCredentialReference source,
        CancellationToken ct)
    {
        var reference = source.SecretReference;
        if (string.IsNullOrWhiteSpace(reference.Ref))
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceMissing,
                "Scheduled invocation agent key secret reference is missing.");

        if (!string.Equals(
                reference.Purpose,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                StringComparison.Ordinal))
        {
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid,
                "Scheduled invocation agent key secret reference purpose is invalid.");
        }

        var apiKeyId = source.ApiKeyId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKeyId))
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.ApiKeyIdMissing,
                "Scheduled invocation agent key id is missing.");

        var ownerScopeKey = reference.OwnerScopeKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ownerScopeKey))
            throw new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid,
                "Scheduled invocation agent key owner scope is missing.");

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
                if (ScheduledServiceInvocationPayloadPolicy.IsConnectorHttpAuthorizationKey(key))
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
                    NyxIdAccessToken = null,
                    NyxIdOrgToken = null,
                    SenderNyxIdAccessToken = null,
                }
                : existingControl with
                {
                    NyxIdAccessToken = ownerCredential
                        ? token
                        : existingControl.NyxIdAccessToken,
                    NyxIdOrgToken = ownerCredential
                        ? token
                        : existingControl.NyxIdOrgToken,
                    SenderNyxIdAccessToken = IsSenderCredential(credential.Role)
                        ? token
                        : existingControl.SenderNyxIdAccessToken,
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

        return !string.IsNullOrWhiteSpace(chatRequest.LlmControl?.NyxIdAccessToken) ||
               !string.IsNullOrWhiteSpace(chatRequest.LlmControl?.NyxIdOrgToken);
    }

    private static bool HasSenderLlmToken(ServiceInvocationRequest request)
    {
        if (request.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return false;

        return !string.IsNullOrWhiteSpace(chatRequest.LlmControl?.SenderNyxIdAccessToken);
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
            ? auth?.ScopeOwnerNyxId?.OwnerSubject
            : auth?.SenderNyxId?.Subject;
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

    private static ScheduledCallerNyxIdAuthority? NormalizeScheduledCallerNyxIdAuthority(
        ScheduledCallerNyxIdAuthority? source)
    {
        if (source == null)
            return null;

        var platform = source.Platform?.Trim() ?? string.Empty;
        var externalUserId = source.ExternalUserId?.Trim() ?? string.Empty;
        var scope = source.Scope?.Trim() ?? string.Empty;
        var bindingId = source.BindingId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(platform) ||
            string.IsNullOrWhiteSpace(externalUserId) ||
            string.IsNullOrWhiteSpace(scope) ||
            string.IsNullOrWhiteSpace(bindingId))
        {
            throw new InvalidOperationException(
                "Scheduled workflow NyxID caller authority is incomplete.");
        }

        return new ScheduledCallerNyxIdAuthority
        {
            Platform = platform,
            Tenant = source.Tenant?.Trim() ?? string.Empty,
            ExternalUserId = externalUserId,
            Scope = scope,
            BindingId = bindingId,
        };
    }

    private static bool TryResolveWorkflowCallerAuthority(
        ScheduledServiceInvocationAuth? auth,
        out ScheduledCallerNyxIdAuthority authority)
    {
        if (auth?.Source is not ScheduledServiceInvocationNyxIdCredentialSource)
        {
            authority = new ScheduledCallerNyxIdAuthority();
            return false;
        }

        authority = NormalizeScheduledCallerNyxIdAuthority(auth?.CallerAuthority) ??
                    new ScheduledCallerNyxIdAuthority();
        return auth?.CallerAuthority != null;
    }

    private static CredentialRole ResolveCredentialRole(ScheduledServiceInvocationAuth? auth) =>
        auth?.Source switch
        {
            ScheduledServiceInvocationNyxIdCredentialSource nyxId => ToCredentialRole(nyxId.Role),
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

    private static CredentialRole ToCredentialRole(ScheduledServiceInvocationNyxIdCredentialRole role) =>
        role == ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner
            ? CredentialRole.ScopeOwner
            : CredentialRole.Sender;
}
