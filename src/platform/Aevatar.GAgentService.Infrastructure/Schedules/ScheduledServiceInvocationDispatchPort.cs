using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
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
    private readonly ILogger<ScheduledServiceInvocationDispatchPort> _logger;

    public ScheduledServiceInvocationDispatchPort(
        IServiceInvocationPort serviceInvocationPort,
        IScheduledServiceInvocationCredentialExchangePort credentialExchangePort,
        ILogger<ScheduledServiceInvocationDispatchPort>? logger = null)
    {
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
        _credentialExchangePort = credentialExchangePort
            ?? throw new ArgumentNullException(nameof(credentialExchangePort));
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
                NormalizeNyxIdAccessToken(exchange.Result.AccessToken, ToErrorSubject(exchange.Role)),
                BuildWorkflowNyxIdSource(exchange.Role, dispatch.Auth!)),
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
            var control = existingControl with
            {
                NyxIdAccessToken = credential.Role == CredentialRole.ScopeOwner
                    ? token
                    : existingControl.NyxIdAccessToken,
                NyxIdOrgToken = credential.Role == CredentialRole.ScopeOwner
                    ? token
                    : existingControl.NyxIdOrgToken,
                SenderNyxIdAccessToken = credential.Role == CredentialRole.Sender
                    ? token
                    : existingControl.SenderNyxIdAccessToken,
            };
            chatRequest.LlmControl = control.ToPayload();
            if (projectNyxIdAccessTokenToWorkflowCallerCredential)
            {
                if (credential.WorkflowSource != null)
                {
                    chatRequest.ConnectorHttpAuthorization = string.Empty;
                    cloned.WorkflowCallerNyxIdCredential = ToServiceInvocationWorkflowSource(credential.WorkflowSource);
                }
                else
                {
                    chatRequest.ConnectorHttpAuthorization = $"Bearer {token}";
                }
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
    }

    private sealed record CredentialExchange(
        CredentialRole Role,
        ScheduledServiceInvocationCredentialExchangeResult Result);

    private sealed record ExchangedCredential(
        CredentialRole Role,
        string AccessToken,
        WorkflowNyxIdCredentialSource? WorkflowSource);

    private static string ToErrorSubject(CredentialRole role) =>
        role == CredentialRole.ScopeOwner ? "scope owner" : "sender";

    private static WorkflowNyxIdCredentialSource? BuildWorkflowNyxIdSource(
        CredentialRole role,
        ScheduledServiceInvocationAuth auth)
    {
        var source = role == CredentialRole.ScopeOwner
            ? ToWorkflowSource(ResolveScopeOwnerSource(auth.ScopeOwnerNyxId))
            : ToWorkflowSource(auth.SenderNyxId);
        return IsUsableWorkflowSource(source) ? source : null;
    }

    private static ScheduledServiceInvocationNyxIdCredentialSource? ResolveScopeOwnerSource(
        ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource? source)
    {
        if (source == null)
            return null;
        if (source.OwnerSubject == null)
            throw new ArgumentException("Schedule scope owner NyxID subject is required for workflow credential projection.", nameof(source));

        return new ScheduledServiceInvocationNyxIdCredentialSource(source.OwnerSubject, source.Scope);
    }

    private static WorkflowNyxIdCredentialSource? ToWorkflowSource(
        ScheduledServiceInvocationNyxIdCredentialSource? source)
    {
        if (source?.Subject == null)
            return null;

        return new WorkflowNyxIdCredentialSource
        {
            Subject = new WorkflowNyxIdSubjectRef
            {
                Platform = source.Subject.Platform?.Trim() ?? string.Empty,
                Tenant = source.Subject.Tenant?.Trim() ?? string.Empty,
                ExternalUserId = source.Subject.ExternalUserId?.Trim() ?? string.Empty,
            },
            Scope = source.Scope?.Trim() ?? string.Empty,
        };
    }

    private static ServiceInvocationWorkflowNyxIdCredentialSource ToServiceInvocationWorkflowSource(
        WorkflowNyxIdCredentialSource source) =>
        new()
        {
            Subject = new ServiceInvocationWorkflowNyxIdSubjectRef
            {
                Platform = source.Subject?.Platform ?? string.Empty,
                Tenant = source.Subject?.Tenant ?? string.Empty,
                ExternalUserId = source.Subject?.ExternalUserId ?? string.Empty,
            },
            Scope = source.Scope ?? string.Empty,
        };

    private static bool IsUsableWorkflowSource(WorkflowNyxIdCredentialSource? source) =>
        source?.Subject != null &&
        !string.IsNullOrWhiteSpace(source.Subject.Platform) &&
        !string.IsNullOrWhiteSpace(source.Subject.Tenant) &&
        !string.IsNullOrWhiteSpace(source.Subject.ExternalUserId) &&
        !string.IsNullOrWhiteSpace(source.Scope);
}
