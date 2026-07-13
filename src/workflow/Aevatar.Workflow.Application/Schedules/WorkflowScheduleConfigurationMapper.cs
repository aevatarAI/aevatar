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
            ScheduledDispatchScheduleKind.Workflow,
            ToScheduledDispatchScheduleMode(configuration.ScheduleMode),
            configuration.OneShotFireAt)
        {
            CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
        };
    }

    public static ScheduledDispatchMutationContext ToScheduledDispatchMutationContext(
        WorkflowScheduleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var context = configuration.MutationContext ?? WorkflowScheduleMutationContext.None;
        return new ScheduledDispatchMutationContext(
            NormalizeOptional(context.AuthenticatedScopeId, string.Empty),
            context.AuthenticatedNyxIdOwnerSubject == null
                ? null
                : MapNyxIdSubject(context.AuthenticatedNyxIdOwnerSubject));
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
            Prompt = NormalizeOptional(configuration.Prompt, string.Empty),
        };

        foreach (var (key, value) in BuildWorkflowScheduleHeaders(configuration))
            request.Metadata[key] = value;

        return request;
    }

    private static ScheduledDispatchScheduleMode ToScheduledDispatchScheduleMode(WorkflowScheduleMode mode) =>
        mode == WorkflowScheduleMode.OneShotAtUtc
            ? ScheduledDispatchScheduleMode.OneShotAtUtc
            : ScheduledDispatchScheduleMode.RecurringCron;

    private static ScheduledServiceInvocationAuth? BuildWorkflowServiceInvocationAuth(
        WorkflowScheduleConfiguration configuration)
    {
        if (configuration.Auth == null)
            return null;

        var hasSenderNyxId = configuration.Auth.SenderNyxId != null;
        var hasScopeOwnerNyxId = configuration.Auth.ScopeOwnerNyxId != null;
        var hasScheduledInvocationAgentKey = configuration.Auth.ScheduledInvocationAgentKey != null;
        if (new[] { hasSenderNyxId, hasScopeOwnerNyxId, hasScheduledInvocationAgentKey }.Count(static hasSource => hasSource) != 1)
            throw new ArgumentException("Exactly one workflow schedule credential source is required.", nameof(configuration.Auth));

        if (hasScheduledInvocationAgentKey)
        {
            var agentKey = configuration.Auth.ScheduledInvocationAgentKey!;
            return new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                agentKey.SecretReference,
                NormalizeRequired(agentKey.ApiKeyId, nameof(agentKey.ApiKeyId)),
                agentKey.KeyExpiresAtUnixMs));
        }

        if (hasScopeOwnerNyxId)
        {
            var scopeOwnerNyxId = configuration.Auth.ScopeOwnerNyxId!;
            return new ScheduledServiceInvocationAuth(
                new ScheduledServiceInvocationNyxIdCredentialSource(
                    scopeOwnerNyxId.OwnerSubject == null ? null! : MapNyxIdSubject(scopeOwnerNyxId.OwnerSubject),
                    NormalizeRequired(scopeOwnerNyxId.Scope, nameof(scopeOwnerNyxId.Scope)),
                    ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner));
        }

        var senderNyxId = configuration.Auth.SenderNyxId!;
        if (senderNyxId.Subject == null)
            throw new ArgumentException("Sender NyxID subject is required.", nameof(configuration.Auth));

        return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            MapNyxIdSubject(senderNyxId.Subject),
            NormalizeRequired(senderNyxId.Scope, nameof(senderNyxId.Scope))));
    }

    private static ScheduledServiceInvocationNyxIdSubjectRef MapNyxIdSubject(
        WorkflowScheduleNyxIdSubjectRef subject) =>
        new(
            NormalizeRequired(subject.Platform, nameof(subject.Platform)),
            NormalizeOptional(subject.Tenant, string.Empty),
            NormalizeRequired(subject.ExternalUserId, nameof(subject.ExternalUserId)));

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
