using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioMemberWorkflowSchedulePort : IStudioMemberWorkflowSchedulePort
{
    private const string WorkflowInvokeEndpointId = "chat";
    private const string ObservatoryPath = "/workflow/observatory";
    private const int MaxScheduleGenerations = 50;

    private readonly IStudioMemberService _memberService;
    private readonly IScheduledDispatchApplicationService _scheduleService;
    private readonly IScheduledInvocationAuthorizationPlanner _authorizationPlanner;
    private readonly IStudioScheduledCredentialMaterializer _credentialMaterializer;

    public StudioMemberWorkflowSchedulePort(
        IStudioMemberService memberService,
        IScheduledDispatchApplicationService scheduleService,
        IScheduledInvocationAuthorizationPlanner authorizationPlanner,
        IStudioScheduledCredentialMaterializer credentialMaterializer)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _scheduleService = scheduleService ?? throw new ArgumentNullException(nameof(scheduleService));
        _authorizationPlanner = authorizationPlanner ?? throw new ArgumentNullException(nameof(authorizationPlanner));
        _credentialMaterializer = credentialMaterializer ?? throw new ArgumentNullException(nameof(credentialMaterializer));
    }

    public async Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveAuthorizationRequestAsync(request, ct);
        var result = await _authorizationPlanner.PlanAsync(resolved.AuthorizationRequest, ct);
        return new StudioMemberWorkflowAuthorizationResult(
            result.Success, result.Plan, result.FailureCode, result.Detail);
    }

    public Task<StudioMemberWorkflowScheduleResult> CreateAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default) =>
        ApplyAsync(request, confirmedPermissionDigest, ct);

    public Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default) =>
        ApplyAsync(request, confirmedPermissionDigest, ct);

    private async Task<StudioMemberWorkflowScheduleResult> ApplyAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = await ResolveAuthorizationRequestAsync(request, ct);
        var current = await _authorizationPlanner.PlanAsync(resolved.AuthorizationRequest, ct);
        if (!current.Success)
            throw new InvalidOperationException(current.Detail);
        if (!string.Equals(current.Plan!.PermissionDigest, confirmedPermissionDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("authorization_plan_changed");

        var scopeId = resolved.ScopeId;
        var memberId = resolved.MemberId;
        var scheduleCron = NormalizeRequired(request.ScheduleCron, nameof(request.ScheduleCron));
        var scheduleTimezone = NormalizeRequired(request.ScheduleTimezone, nameof(request.ScheduleTimezone));
        var publishedServiceId = resolved.PublishedServiceId;
        var callerSubject = BuildCallerSubject(request);
        var scheduleId = BuildScheduleId(scopeId, memberId);
        var ownerScope = BuildOwnerScope(request);
        var bearerToken = NormalizeRequired(request.ProvisioningBearerToken, nameof(request.ProvisioningBearerToken));
        var credential = await _credentialMaterializer.MaterializeAsync(
            bearerToken, current.Plan, scheduleId, ownerScope, ct);

        ScheduledDispatchMutationReceipt schedule;
        try
        {
            EnsureCredentialMatchesPlan(credential, current.Plan);
            schedule = await EnsureScheduleAsync(
                scheduleId,
                request.DisplayName,
                scopeId,
                memberId,
                publishedServiceId,
                NormalizeOptional(request.Prompt) ?? string.Empty,
                BuildScheduleAuth(credential),
                ToScheduleAuthorizationFact(current.Plan),
                new ScheduledDispatchMutationContext(scopeId, callerSubject),
                scheduleCron,
                scheduleTimezone,
                ct);
            if (!schedule.Accepted)
                throw new InvalidOperationException("scheduled_dispatch_rejected");
        }
        catch
        {
            await _credentialMaterializer.RevokeAsync(
                bearerToken, scheduleId, ownerScope, credential, CancellationToken.None);
            throw;
        }

        return new StudioMemberWorkflowScheduleResult(
            Success: schedule.Accepted,
            ScopeId: scopeId,
            MemberId: memberId,
            ScheduleId: NormalizeRequired(schedule.ScheduleId, nameof(schedule.ScheduleId)),
            PublishedServiceId: publishedServiceId,
            ObservatoryUrl: ObservatoryPath,
            Status: schedule.Accepted ? "accepted" : "rejected");
    }

    private async Task<ResolvedStudioAuthorizationRequest> ResolveAuthorizationRequestAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct)
    {
        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var memberId = NormalizeRequired(request.MemberId, nameof(request.MemberId));
        var member = await _memberService.GetAsync(scopeId, memberId, ct);
        if (!string.Equals(member.Summary.ImplementationKind, MemberImplementationKindNames.Workflow, StringComparison.Ordinal))
            throw new InvalidOperationException($"member_id '{memberId}' is not a workflow member and cannot be scheduled as a workflow.");

        var publishedServiceId = NormalizeRequired(member.Summary.PublishedServiceId, nameof(member.Summary.PublishedServiceId));
        EnsureWorkflowBindingCanBeScheduled(member, memberId, publishedServiceId);
        var workflowRevision = member.LastBinding?.RevisionId ?? member.Summary.LastBoundRevisionId ?? string.Empty;
        var workflowId = NormalizeRequired(member.ImplementationRef?.WorkflowId, "workflowId");
        var target = new ScheduledInvocationTarget
        {
            Studio = new StudioScheduledInvocationTarget
            {
                ScopeId = scopeId,
                TeamId = member.Summary.TeamId ?? string.Empty,
                MemberId = memberId,
                PublishedServiceId = publishedServiceId,
                WorkflowId = workflowId,
                WorkflowRevision = workflowRevision,
            },
        };
        var authority = new Aevatar.Studio.Application.Authorization.ScheduledInvocationAuthorizationAuthority();
        return new ResolvedStudioAuthorizationRequest(
            scopeId,
            memberId,
            publishedServiceId,
            new ScheduledInvocationAuthorizationRequest(
                target,
                request.AuthenticatedOwner,
                [],
                authority,
                request.CredentialExpiresAtUtc,
                DateTimeOffset.UtcNow)
            {
                ServiceGrantsNotRequired = false,
            });
    }

    private async Task<ScheduledDispatchMutationReceipt> EnsureScheduleAsync(
        string baseScheduleId,
        string? displayName,
        string scopeId,
        string memberId,
        string publishedServiceId,
        string prompt,
        ScheduledServiceInvocationAuth auth,
        ScheduledInvocationAuthorizationFact authorizationFact,
        ScheduledDispatchMutationContext mutationContext,
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
                        authorizationFact,
                        cronExpression,
                        timezone),
                    mutationContext,
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
        ScheduledInvocationAuthorizationFact authorizationFact,
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
                    Auth: auth,
                    AuthorizationFact: authorizationFact)),
            CronExpression: cronExpression,
            Timezone: timezone,
            Enabled: true,
            Headers: new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduleKind: ScheduledDispatchScheduleKind.Workflow);

    private static ScheduledInvocationAuthorizationFact ToScheduleAuthorizationFact(
        ScheduledInvocationAuthorizationPlan plan) => new(
        plan.PermissionDigest,
        plan.CredentialPolicy.PolicyVersion,
        new ScheduledInvocationAuthorizationOwner(
            plan.Owner.Authority,
            plan.Owner.OwnerKind.ToString(),
            plan.Owner.OwnerSubject),
        plan.NyxIdServiceGrants.Select(static grant => new ScheduledInvocationAuthorizationServiceGrant(
            grant.UserServiceId,
            grant.NodeGrants.Select(static node => node.NodeId).ToArray(),
            grant.NodeGrantsNotRequired)).ToArray(),
        plan.CredentialPolicy.Scopes,
        plan.CredentialPolicy.ExpiresAt.ToDateTimeOffset(),
        plan.CredentialPolicy.ServiceGrantsNotRequired,
        new Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationDisclosure(
            plan.Disclosure.DedicatedToSchedule,
            plan.Disclosure.SecretManagedByAevatar,
            plan.Disclosure.BrowserReceivesRawKey,
            plan.Disclosure.DeleteRevokesCredential,
            plan.Disclosure.PauseResumeRevokesCredential),
        new Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationAuthority(
            plan.Authority.MemberStateVersion,
            plan.Authority.WorkflowStateVersion,
            plan.Authority.ConnectorStateVersion,
            plan.Authority.OwnerLlmStateVersion,
            plan.Authority.CatalogStateVersion,
            plan.Authority.CatalogObservedAt.ToDateTimeOffset(),
            plan.Authority.CatalogFreshUntil.ToDateTimeOffset(),
            plan.Authority.CatalogExternalRevision,
            plan.Authority.CatalogContentDigest));

    private static ScheduledServiceInvocationAuth BuildScheduleAuth(StudioScheduledCredential credential) =>
        new(new ScheduledInvocationAgentKeyCredentialReference(
            credential.SecretReference.Clone(),
            credential.ApiKeyId,
            credential.ExpiresAtUtc.ToUnixTimeMilliseconds()));

    private static void EnsureCredentialMatchesPlan(
        StudioScheduledCredential credential,
        ScheduledInvocationAuthorizationPlan plan)
    {
        if (credential.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            credential.ExpiresAtUtc > plan.CredentialPolicy.ExpiresAt.ToDateTimeOffset())
            throw new InvalidOperationException("scheduled_credential_expiry_mismatch");
        if (!string.Equals(credential.SecretReference.Purpose,
                CredentialSecretPurposes.ScheduledInvocationAgentKey, StringComparison.Ordinal))
            throw new InvalidOperationException("scheduled_credential_purpose_mismatch");
    }

    private static OwnerScope BuildOwnerScope(StudioMemberWorkflowScheduleRequest request)
    {
        var owner = request.AuthenticatedOwner;
        return string.Equals(owner.SubjectPlatform, OwnerScope.NyxIdPlatform, StringComparison.Ordinal)
            ? OwnerScope.ForNyxIdNative(owner.Owner.OwnerSubject)
            : OwnerScope.ForChannel(
                owner.Owner.OwnerSubject,
                owner.SubjectPlatform.Trim().ToLowerInvariant(),
                NormalizeRequired(owner.SubjectTenant, nameof(owner.SubjectTenant)),
                owner.SubjectExternalUserId);
    }

    private static ScheduledServiceInvocationNyxIdSubjectRef BuildCallerSubject(
        StudioMemberWorkflowScheduleRequest request) =>
        new(
            Platform: NormalizeOptional(request.CallerSubjectPlatform) ?? NormalizeRequired(request.AuthenticatedOwner.SubjectPlatform, nameof(request.AuthenticatedOwner.SubjectPlatform)),
            Tenant: NormalizeOptional(request.CallerSubjectTenant) ?? NormalizeOptional(request.AuthenticatedOwner.SubjectTenant) ?? string.Empty,
            ExternalUserId: NormalizeRequired(request.AuthenticatedOwner.SubjectExternalUserId, nameof(request.AuthenticatedOwner.SubjectExternalUserId)));

    private sealed record ResolvedStudioAuthorizationRequest(
        string ScopeId,
        string MemberId,
        string PublishedServiceId,
        ScheduledInvocationAuthorizationRequest AuthorizationRequest);

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
