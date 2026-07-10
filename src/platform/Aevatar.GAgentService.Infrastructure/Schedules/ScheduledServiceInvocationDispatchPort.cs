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
                    NormalizeNyxIdAccessToken(exchange.Result.AccessToken, exchange.Role)),
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
        if (dispatch.Auth?.Source == null)
            return null;

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
        if (!resolved.Resolved || string.IsNullOrWhiteSpace(resolved.Secret))
        {
            return ScheduledServiceInvocationCredentialExchangeResult.Failure(
                "Scheduled service invocation durable credential reference could not be resolved.");
        }

        return ScheduledServiceInvocationCredentialExchangeResult.Success(resolved.Secret);
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
            var control = existingControl with
            {
                NyxIdAccessToken = credential.Role == CredentialRole.ScopeOwner
                    ? token
                    : existingControl.NyxIdAccessToken,
                NyxIdOrgToken = credential.Role == CredentialRole.ScopeOwner
                    ? token
                    : existingControl.NyxIdOrgToken,
                SenderNyxIdAccessToken = IsSenderCredential(credential.Role)
                    ? token
                    : existingControl.SenderNyxIdAccessToken,
            };
            chatRequest.LlmControl = control.ToPayload();
            if (projectNyxIdAccessTokenToWorkflowCallerCredential)
                chatRequest.ConnectorHttpAuthorization = $"Bearer {token}";
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

    private enum CredentialRole
    {
        Sender,
        ScopeOwner,
        DurableSender,
    }

    private sealed record CredentialExchange(
        CredentialRole Role,
        ScheduledServiceInvocationCredentialExchangeResult Result);

    private sealed record ExchangedCredential(CredentialRole Role, string AccessToken);

    private static string ToErrorSubject(CredentialRole role) =>
        role switch
        {
            CredentialRole.ScopeOwner => "scope owner",
            CredentialRole.DurableSender => "durable",
            _ => "sender",
        };

    private static bool IsSenderCredential(CredentialRole role) =>
        role is CredentialRole.Sender or CredentialRole.DurableSender;

    private static string ToEmptyTokenError(CredentialRole role) =>
        role == CredentialRole.DurableSender
            ? "Scheduled service invocation durable credential reference resolved an empty access token."
            : $"Scheduled service invocation {ToErrorSubject(role)} NyxID credential exchange returned an empty access token.";

    private static string ToInvalidTokenError(CredentialRole role) =>
        role == CredentialRole.DurableSender
            ? "Scheduled service invocation durable credential reference resolved an invalid access token."
            : $"Scheduled service invocation {ToErrorSubject(role)} NyxID credential exchange returned an invalid access token.";

    private static CredentialRole ToCredentialRole(ScheduledServiceInvocationNyxIdCredentialRole role) =>
        role == ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner
            ? CredentialRole.ScopeOwner
            : CredentialRole.Sender;
}
