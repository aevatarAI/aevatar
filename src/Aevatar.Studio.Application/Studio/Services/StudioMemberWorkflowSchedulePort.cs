using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowSchedulePort : IStudioMemberWorkflowSchedulePort
{
    private const string WorkflowInvokeEndpointId = "chat";
    private const string ObservatoryPath = "/workflow/observatory";
    private const int MaxScheduleGenerations = 50;

    private readonly IStudioMemberService _memberService;
    private readonly IScheduledDispatchApplicationService _scheduleService;

    public StudioMemberWorkflowSchedulePort(
        IStudioMemberService memberService,
        IScheduledDispatchApplicationService scheduleService)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _scheduleService = scheduleService ?? throw new ArgumentNullException(nameof(scheduleService));
    }

    public async Task<StudioMemberWorkflowScheduleResult> EnsureAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var memberId = NormalizeRequired(request.MemberId, nameof(request.MemberId));
        var scheduleCron = NormalizeRequired(request.ScheduleCron, nameof(request.ScheduleCron));
        var scheduleTimezone = NormalizeRequired(request.ScheduleTimezone, nameof(request.ScheduleTimezone));
        var callerSubjectExternalUserId = NormalizeRequired(
            request.CallerSubjectExternalUserId,
            nameof(request.CallerSubjectExternalUserId));

        var member = await _memberService.GetAsync(scopeId, memberId, ct);
        if (!string.Equals(member.Summary.ImplementationKind, MemberImplementationKindNames.Workflow, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"member_id '{memberId}' is not a workflow member and cannot be scheduled as a workflow.");
        }

        var publishedServiceId = NormalizeRequired(
            member.Summary.PublishedServiceId,
            nameof(member.Summary.PublishedServiceId));
        EnsureWorkflowBindingCanBeScheduled(member, memberId, publishedServiceId);

        var schedule = await EnsureScheduleAsync(
            BuildScheduleId(scopeId, memberId),
            request.DisplayName,
            scopeId,
            memberId,
            publishedServiceId,
            NormalizeOptional(request.Prompt) ?? string.Empty,
            BuildScheduleAuth(request, callerSubjectExternalUserId),
            scheduleCron,
            scheduleTimezone,
            ct);

        return new StudioMemberWorkflowScheduleResult(
            Success: schedule.Accepted,
            ScopeId: scopeId,
            MemberId: memberId,
            ScheduleId: NormalizeRequired(schedule.ScheduleId, nameof(schedule.ScheduleId)),
            PublishedServiceId: publishedServiceId,
            ObservatoryUrl: ObservatoryPath,
            Status: schedule.Accepted ? "accepted" : "rejected");
    }

    private async Task<ScheduledDispatchMutationReceipt> EnsureScheduleAsync(
        string baseScheduleId,
        string? displayName,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string prompt,
        ScheduledServiceInvocationAuth auth,
        string cronExpression,
        string timezone,
        CancellationToken ct)
    {
        for (var generation = 1; generation <= MaxScheduleGenerations; generation++)
        {
            var scheduleId = generation == 1 ? baseScheduleId : $"{baseScheduleId}.{generation}";
            try
            {
                return await _scheduleService.EnsureAsync(
                    BuildScheduleConfiguration(
                        scheduleId,
                        displayName,
                        scopeId,
                        memberId,
                        publishedServiceId,
                        prompt,
                        auth,
                        cronExpression,
                        timezone),
                    ct);
            }
            catch (ScheduledDispatchNotFoundException)
            {
            }
        }

        throw new InvalidOperationException(
            $"Studio member workflow schedule for member '{memberId}' exhausted {MaxScheduleGenerations} deleted schedule generations.");
    }

    private static void EnsureWorkflowBindingCanBeScheduled(
        StudioMemberDetailResponse member,
        string memberId,
        string publishedServiceId)
    {
        if (member.LastBinding is not null)
        {
            var boundPublishedServiceId = NormalizeRequired(
                member.LastBinding.PublishedServiceId,
                nameof(member.LastBinding.PublishedServiceId));
            if (!string.Equals(publishedServiceId, boundPublishedServiceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"member_id '{memberId}' binding service id does not match the published service id.");
            }

            return;
        }

        if (member.CurrentBindingRun is null || !IsSchedulableCurrentBindingRun(member.CurrentBindingRun.Status))
        {
            throw new InvalidOperationException(
                $"member_id '{memberId}' has no bound workflow. Bind workflow YAML before scheduling the member.");
        }
    }

    private static bool IsSchedulableCurrentBindingRun(string? status) => status switch
    {
        StudioMemberBindingRunStatusNames.Accepted => true,
        StudioMemberBindingRunStatusNames.AdmissionPending => true,
        StudioMemberBindingRunStatusNames.Admitted => true,
        StudioMemberBindingRunStatusNames.PlatformBindingPending => true,
        StudioMemberBindingRunStatusNames.MemberNotificationPending => true,
        StudioMemberBindingRunStatusNames.Succeeded => true,
        _ => false,
    };

    private static ScheduledDispatchConfiguration BuildScheduleConfiguration(
        string scheduleId,
        string? displayName,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string prompt,
        ScheduledServiceInvocationAuth auth,
        string cronExpression,
        string timezone) =>
        new(
            ScheduleId: scheduleId,
            DisplayName: NormalizeOptional(displayName) ?? $"studio-member-workflow-{memberId}",
            Target: new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    Identity: new ServiceIdentity
                    {
                        TenantId = scopeId,
                        AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                        Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                        ServiceId = publishedServiceId,
                    },
                    EndpointId: WorkflowInvokeEndpointId,
                    Payload: Any.Pack(new ChatRequestEvent
                    {
                        Prompt = prompt,
                        ScopeId = scopeId,
                    }),
                    Auth: auth)),
            CronExpression: cronExpression,
            Timezone: timezone,
            Enabled: true,
            Headers: new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow);

    private static ScheduledServiceInvocationAuth BuildScheduleAuth(
        StudioMemberWorkflowScheduleRequest request,
        string callerSubjectExternalUserId) =>
        new(SenderNyxId: new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef(
                Platform: NormalizeRequired(request.CallerSubjectPlatform, nameof(request.CallerSubjectPlatform)),
                Tenant: NormalizeOptional(request.CallerSubjectTenant) ?? string.Empty,
                ExternalUserId: callerSubjectExternalUserId),
            Scope: ProvisionWorkflowCallerCredential.DefaultScope));

    private static string BuildScheduleId(string scopeId, string memberId)
    {
        var identity = Encoding.UTF8.GetBytes($"{scopeId}\n{memberId}");
        var hash = SHA256.HashData(identity);
        return $"studio-member-workflow-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }
}
