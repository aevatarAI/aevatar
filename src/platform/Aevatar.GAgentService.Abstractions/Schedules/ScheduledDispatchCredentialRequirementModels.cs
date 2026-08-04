using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Schedules;

public enum ScheduledDispatchCredentialRequirementTargetKind
{
    Unspecified = 0,
    Envelope = 1,
    StaticService = 2,
    ScriptingService = 3,
    WorkflowService = 4,
    Connector = 5,
}

public enum ScheduledDispatchCredentialRequirementOperation
{
    Create = 0,
    Ensure = 1,
    Update = 2,
    Fire = 3,
}

public enum ScheduledDispatchCredentialSourceKind
{
    None = 0,
    SenderNyxId = 1,
    ScopeOwnerNyxId = 2,
    LegacyDurableSenderBearer = 3,
    Multiple = 4,
    DurableCredentialReference = 5,
    ScheduledInvocationAgentKey = 6,
}

public enum ScheduledDispatchCredentialViolationCode
{
    None = 0,
    CredentialRequired = 1,
    UnsupportedCredentialSource = 2,
    CurrentSessionCredential = 3,
    InvalidCredentialSource = 4,
}

public sealed record ScheduledDispatchCredentialSourceSummary(
    ScheduledDispatchCredentialSourceKind Kind);

public sealed record ScheduledDispatchPayloadCredentialSignal(
    bool HasCurrentSessionCredential,
    string Source)
{
    public static ScheduledDispatchPayloadCredentialSignal None { get; } = new(false, string.Empty);
}

public sealed record ScheduledDispatchCredentialRequirementRequest(
    string ScheduleId,
    ScheduledDispatchCredentialRequirementOperation Operation,
    ScheduledDispatchScheduleKind ScheduleKind,
    ScheduledDispatchCredentialRequirementTargetKind TargetKind,
    ScheduledDispatchCredentialSourceSummary CredentialSource,
    ScheduledDispatchPayloadCredentialSignal PayloadCredentialSignal);

public sealed record ScheduledDispatchCredentialRequirementDecision(
    bool Allowed,
    bool CredentialRequired,
    ScheduledDispatchCredentialViolationCode ViolationCode,
    string Message)
{
    public static ScheduledDispatchCredentialRequirementDecision Allow(bool credentialRequired) =>
        new(true, credentialRequired, ScheduledDispatchCredentialViolationCode.None, string.Empty);

    public static ScheduledDispatchCredentialRequirementDecision Deny(
        bool credentialRequired,
        ScheduledDispatchCredentialViolationCode violationCode,
        string message) =>
        new(false, credentialRequired, violationCode, string.IsNullOrWhiteSpace(message)
            ? "Scheduled dispatch credential requirement was not satisfied."
            : message.Trim());
}

public interface IScheduledDispatchCredentialRequirementPolicy
{
    ScheduledDispatchCredentialRequirementDecision Evaluate(
        ScheduledDispatchCredentialRequirementRequest request);
}

public static class ScheduledDispatchCredentialRequirementRequests
{
    public const string LegacyConnectorHttpAuthorizationHeader =
        ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey;

    public static ScheduledDispatchCredentialRequirementRequest FromConfiguration(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchCredentialRequirementOperation operation)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var targetKind = ResolveTargetKind(configuration);
        return new ScheduledDispatchCredentialRequirementRequest(
            configuration.ScheduleId ?? string.Empty,
            operation,
            configuration.ScheduleKind,
            targetKind,
            SummarizeAuth(configuration.Target.ServiceInvocation?.Auth),
            SummarizePayloadCredentialSignal(configuration.Target, configuration.Headers));
    }

    public static ScheduledDispatchCredentialRequirementTargetKind ResolveTargetKind(
        ScheduledDispatchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.CredentialRequirementTargetKind != ScheduledDispatchCredentialRequirementTargetKind.Unspecified)
            return configuration.CredentialRequirementTargetKind;

        return configuration.Target.Kind switch
        {
            ScheduledDispatchTargetKind.Envelope => ScheduledDispatchCredentialRequirementTargetKind.Envelope,
            ScheduledDispatchTargetKind.ServiceInvocation
                when configuration.ScheduleKind == ScheduledDispatchScheduleKind.Workflow =>
                    ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
            _ => ScheduledDispatchCredentialRequirementTargetKind.Unspecified,
        };
    }

    public static ScheduledDispatchCredentialSourceSummary SummarizeAuth(
        ScheduledServiceInvocationAuth? auth)
    {
        if (auth?.Source == null)
            return new ScheduledDispatchCredentialSourceSummary(ScheduledDispatchCredentialSourceKind.None);

        var kind = auth.Source switch
        {
            ScheduledServiceInvocationNyxIdCredentialSource { Role: ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner } =>
                ScheduledDispatchCredentialSourceKind.ScopeOwnerNyxId,
            ScheduledServiceInvocationNyxIdCredentialSource =>
                ScheduledDispatchCredentialSourceKind.SenderNyxId,
            ScheduledServiceInvocationDurableCredentialReference =>
                ScheduledDispatchCredentialSourceKind.DurableCredentialReference,
            ScheduledInvocationAgentKeyCredentialReference =>
                ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey,
            _ => ScheduledDispatchCredentialSourceKind.Multiple,
        };
        return new ScheduledDispatchCredentialSourceSummary(kind);
    }

    public static ScheduledDispatchPayloadCredentialSignal SummarizePayloadCredentialSignal(
        ScheduledDispatchTargetDescriptor target,
        IReadOnlyDictionary<string, string>? headers)
    {
        var headerSignal = SummarizeHeaderCredentialSignal(headers);
        if (headerSignal.HasCurrentSessionCredential)
            return headerSignal;

        return target.Kind switch
        {
            ScheduledDispatchTargetKind.Envelope => SummarizePayloadCredentialSignal(target.Envelope?.Payload),
            ScheduledDispatchTargetKind.ServiceInvocation => SummarizePayloadCredentialSignal(
                target.ServiceInvocation?.Payload),
            _ => ScheduledDispatchPayloadCredentialSignal.None,
        };
    }

    public static ScheduledDispatchPayloadCredentialSignal SummarizePayloadCredentialSignal(
        Any? payload,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var headerSignal = SummarizeHeaderCredentialSignal(headers);
        if (headerSignal.HasCurrentSessionCredential)
            return headerSignal;

        if (payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return ScheduledDispatchPayloadCredentialSignal.None;

        if (!string.IsNullOrWhiteSpace(chatRequest.ConnectorHttpAuthorization))
            return new ScheduledDispatchPayloadCredentialSignal(true, nameof(ChatRequestEvent.ConnectorHttpAuthorization));

        if (!string.IsNullOrWhiteSpace(chatRequest.CallerSourceReadableNyxIdBearerToken))
        {
            return new ScheduledDispatchPayloadCredentialSignal(
                true,
                nameof(ChatRequestEvent.CallerSourceReadableNyxIdBearerToken));
        }

        if (!string.IsNullOrWhiteSpace(chatRequest.LlmControl?.NyxIdAccessToken) ||
            !string.IsNullOrWhiteSpace(chatRequest.LlmControl?.NyxIdOrgToken) ||
            !string.IsNullOrWhiteSpace(chatRequest.LlmControl?.SenderNyxIdAccessToken))
        {
            return new ScheduledDispatchPayloadCredentialSignal(true, nameof(ChatRequestEvent.LlmControl));
        }

        if (!string.IsNullOrWhiteSpace(chatRequest.ToolContext?.Credentials?.NyxIdAccessToken) ||
            !string.IsNullOrWhiteSpace(chatRequest.ToolContext?.Credentials?.NyxIdOrgToken) ||
            !string.IsNullOrWhiteSpace(chatRequest.ToolContext?.Credentials?.SenderNyxIdAccessToken) ||
            !string.IsNullOrWhiteSpace(
                chatRequest.ToolContext?.Credentials?.SourceReadableNyxIdAccessToken))
        {
            return new ScheduledDispatchPayloadCredentialSignal(true, "ToolContext.Credentials");
        }

        return ScheduledDispatchPayloadCredentialSignal.None;
    }

    private static ScheduledDispatchPayloadCredentialSignal SummarizeHeaderCredentialSignal(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers == null)
            return ScheduledDispatchPayloadCredentialSignal.None;

        foreach (var (key, value) in headers)
        {
            if (ScheduledServiceInvocationPayloadPolicy.IsConnectorHttpAuthorizationKey(key) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return new ScheduledDispatchPayloadCredentialSignal(
                    true,
                    LegacyConnectorHttpAuthorizationHeader);
            }
        }

        return ScheduledDispatchPayloadCredentialSignal.None;
    }
}
