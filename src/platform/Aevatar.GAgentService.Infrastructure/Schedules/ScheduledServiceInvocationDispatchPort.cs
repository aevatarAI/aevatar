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
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";

    private readonly IServiceInvocationPort _serviceInvocationPort;
    private readonly IScheduledServiceInvocationCredentialExchangePort _credentialExchangePort;
    private readonly ISecretVault? _secretVault;
    private readonly ILogger<ScheduledServiceInvocationDispatchPort> _logger;

    public ScheduledServiceInvocationDispatchPort(
        IServiceInvocationPort serviceInvocationPort,
        IScheduledServiceInvocationCredentialExchangePort credentialExchangePort,
        ISecretVault? secretVault = null,
        ILogger<ScheduledServiceInvocationDispatchPort>? logger = null)
    {
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
        _credentialExchangePort = credentialExchangePort
            ?? throw new ArgumentNullException(nameof(credentialExchangePort));
        _secretVault = secretVault;
        _logger = logger ?? NullLogger<ScheduledServiceInvocationDispatchPort>.Instance;
    }

    public async Task<ScheduledServiceInvocationDispatchReceipt> DispatchAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(dispatch.Request);

        var request = WithScheduleId(
            await BuildInvocationRequestAsync(dispatch, ct),
            dispatch.ScheduleId);
        _logger.LogInformation(
            "Scheduled service invocation credential projection prepared. scheduleId={ScheduleId} serviceKey={ServiceKey} endpointId={EndpointId} projectWorkflowCallerCredential={ProjectWorkflowCallerCredential} hasConnectorAuthorization={HasConnectorAuthorization} hasOwnerLlmToken={HasOwnerLlmToken} hasSenderLlmToken={HasSenderLlmToken}",
            dispatch.ScheduleId ?? string.Empty,
            FormatServiceKey(request.Identity),
            request.EndpointId ?? string.Empty,
            dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential,
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

    private async Task<ServiceInvocationRequest> BuildInvocationRequestAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.Auth?.DurableSenderBearerToken))
            throw new InvalidOperationException("Scheduled service invocation durable bearer auth is no longer supported.");

        var exchange = await ExchangeCredentialAsync(dispatch, ct);
        if (exchange == null)
        {
            return EnrichChatPayload(
                dispatch.Request,
                dispatch.Headers,
                credential: null,
                projectNyxIdAccessTokenToWorkflowCallerCredential:
                    dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential);
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

        return EnrichChatPayload(
            dispatch.Request,
            dispatch.Headers,
            new ExchangedCredential(
                exchange.Role,
                NormalizeNyxIdAccessToken(exchange.Result.AccessToken, ToErrorSubject(exchange.Role))),
            dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential);
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
        if (dispatch.Auth == null)
            return null;
        if (dispatch.Auth.ScheduledInvocationAgentKey != null)
        {
            var result = await ResolveScheduledInvocationAgentKeyAsync(
                dispatch.Auth.ScheduledInvocationAgentKey,
                ct);
            return new CredentialExchange(CredentialRole.ScheduledInvocationAgentKey, result);
        }
        if (dispatch.Auth.ScopeOwnerNyxId != null)
        {
            var result = await ExchangeScopeOwnerNyxIdAsync(
                dispatch.Auth.ScopeOwnerNyxId,
                dispatch.Request.Identity,
                ct);
            return new CredentialExchange(CredentialRole.ScopeOwner, result);
        }
        if (dispatch.Auth.SenderNyxId != null)
        {
            var result = await ExchangeSenderNyxIdAsync(dispatch.Auth.SenderNyxId, ct);
            return new CredentialExchange(CredentialRole.Sender, result);
        }

        return null;
    }

    private async Task<ScheduledServiceInvocationCredentialExchangeResult> ExchangeScopeOwnerNyxIdAsync(
        ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource source,
        ServiceIdentity serviceIdentity,
        CancellationToken ct) =>
        await _credentialExchangePort.IssueScopeOwnerNyxIdAsync(source, serviceIdentity, ct);

    private async Task<ScheduledServiceInvocationCredentialExchangeResult> ExchangeSenderNyxIdAsync(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        CancellationToken ct) =>
        await _credentialExchangePort.IssueSenderNyxIdAsync(source, ct);

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

            return ScheduledServiceInvocationCredentialExchangeResult.Success(accessToken);
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
        if ((headers == null || headers.Count == 0) && credential == null)
            return request;

        var cloned = request.Clone();
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
            var control = existingControl with
            {
                NyxIdAccessToken = ownerCredential
                    ? token
                    : existingControl.NyxIdAccessToken,
                NyxIdOrgToken = ownerCredential
                    ? token
                    : existingControl.NyxIdOrgToken,
                SenderNyxIdAccessToken = credential.Role == CredentialRole.Sender
                    ? token
                    : existingControl.SenderNyxIdAccessToken,
            };
            chatRequest.LlmControl = control.ToPayload();
            if (projectNyxIdAccessTokenToWorkflowCallerCredential &&
                credential.Role != CredentialRole.ScheduledInvocationAgentKey)
            {
                chatRequest.ConnectorHttpAuthorization = $"Bearer {token}";
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

    private static string NormalizeNyxIdAccessToken(string? accessToken, string credentialSubject)
    {
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(accessToken);
        if (parsed.IsMissing)
            throw new InvalidOperationException($"Scheduled service invocation {credentialSubject} NyxID credential exchange returned an empty access token.");
        if (parsed.IsInvalid)
            throw new InvalidOperationException($"Scheduled service invocation {credentialSubject} NyxID credential exchange returned an invalid access token.");

        return parsed.NormalizedBearerToken!;
    }

    private enum CredentialRole
    {
        Sender,
        ScopeOwner,
        ScheduledInvocationAgentKey,
    }

    private sealed record CredentialExchange(
        CredentialRole Role,
        ScheduledServiceInvocationCredentialExchangeResult Result);

    private sealed record ExchangedCredential(CredentialRole Role, string AccessToken);

    private static string ToErrorSubject(CredentialRole role) =>
        role switch
        {
            CredentialRole.ScopeOwner => "scope owner",
            CredentialRole.ScheduledInvocationAgentKey => "scheduled invocation agent key",
            _ => "sender",
        };

}
