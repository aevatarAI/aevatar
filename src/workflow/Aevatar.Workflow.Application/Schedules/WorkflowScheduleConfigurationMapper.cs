using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Schedules;

internal static class WorkflowScheduleConfigurationMapper
{
    public static ScheduledDispatchConfiguration ToScheduledDispatchConfiguration(
        WorkflowScheduleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ScheduledDispatchConfiguration(
            configuration.ScheduleId,
            configuration.DisplayName,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    BuildWorkflowServiceIdentity(configuration),
                    "chat",
                    Any.Pack(BuildWorkflowChatRequest(configuration)),
                    configuration.RevisionId,
                    Auth: BuildWorkflowServiceInvocationAuth(configuration))),
            configuration.CronExpression,
            configuration.Timezone,
            configuration.Enabled,
            BuildWorkflowScheduleHeaders(configuration),
            ScheduledDispatchScheduleKind.Workflow);
    }

    private static ServiceIdentity BuildWorkflowServiceIdentity(WorkflowScheduleConfiguration configuration)
    {
        var scopeId = NormalizeRequired(FirstNonBlank(configuration.ScopeId, configuration.TenantId), nameof(configuration.ScopeId));
        var serviceId = NormalizeRequired(FirstNonBlank(configuration.ServiceId, configuration.WorkflowName), nameof(configuration.ServiceId));
        return new ServiceIdentity
        {
            TenantId = scopeId,
            AppId = NormalizeOptional(configuration.AppId, ScopeServiceIdentityDefaults.ServiceAppId),
            Namespace = NormalizeOptional(configuration.Namespace, ScopeServiceIdentityDefaults.ServiceNamespace),
            ServiceId = serviceId,
        };
    }

    private static ChatRequestEvent BuildWorkflowChatRequest(WorkflowScheduleConfiguration configuration)
    {
        var request = new ChatRequestEvent
        {
            Prompt = NormalizeRequired(configuration.Prompt, nameof(configuration.Prompt)),
        };

        foreach (var (key, value) in BuildWorkflowScheduleHeaders(configuration))
            request.Metadata[key] = value;

        return request;
    }

    private static ScheduledServiceInvocationAuth? BuildWorkflowServiceInvocationAuth(
        WorkflowScheduleConfiguration configuration)
    {
        if (configuration.Auth == null)
            return null;
        if (configuration.Auth.SenderNyxId == null)
            throw new ArgumentException("Sender NyxID credential source is required.", nameof(configuration.Auth));

        var senderNyxId = configuration.Auth.SenderNyxId;
        if (senderNyxId.Subject == null)
            throw new ArgumentException("Sender NyxID subject is required.", nameof(configuration.Auth));

        return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef(
                NormalizeRequired(senderNyxId.Subject.Platform, nameof(senderNyxId.Subject.Platform)),
                NormalizeOptional(senderNyxId.Subject.Tenant, string.Empty),
                NormalizeRequired(senderNyxId.Subject.ExternalUserId, nameof(senderNyxId.Subject.ExternalUserId))),
            NormalizeRequired(senderNyxId.Scope, nameof(senderNyxId.Scope))));
    }

    private static IReadOnlyDictionary<string, string> BuildWorkflowScheduleHeaders(
        WorkflowScheduleConfiguration configuration)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in configuration.Headers ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            headers[key.Trim()] = value.Trim();
        }

        return headers;
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{fieldName} is required.", fieldName);

        return normalized;
    }

    private static string NormalizeOptional(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}
