using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Executes one idempotent schedule-provisioning attempt after the member actor
/// has observed the target workflow binding. Projection lag is reported as a
/// retryable result so the actor, rather than the request stack, owns retries.
/// </summary>
public sealed class StudioWorkflowScheduleProvisioningExecutor
    : IStudioWorkflowScheduleProvisioningExecutor
{
    private const string CredentialProvisioningKind = "dedicated_scheduled_invocation_agent_key";
    private const string ScheduleIdAllowedCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-";
    private readonly IStudioMemberWorkflowSchedulePort _schedulePort;

    public StudioWorkflowScheduleProvisioningExecutor(IStudioMemberWorkflowSchedulePort schedulePort)
    {
        _schedulePort = schedulePort ?? throw new ArgumentNullException(nameof(schedulePort));
    }

    public async Task<StudioWorkflowScheduleProvisioningExecutionResult> ExecuteAsync(
        StudioWorkflowScheduleProvisioningExecution execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(execution.Intent);
        var intent = execution.Intent;
        var timing = ResolveScheduleTiming(execution);
        var request = new StudioMemberWorkflowScheduleRequest(
            intent.ScopeId,
            intent.MemberId,
            timing.CronExpression,
            timing.Timezone,
            intent.AuthenticatedOwner)
        {
            TeamId = intent.TeamId,
            CredentialProvisioningKind = CredentialProvisioningKind,
            Prompt = intent.Prompt,
            DisplayName = $"provision-{intent.DisplayName}",
            Enabled = true,
            ScheduleMode = timing.ScheduleMode,
            OneShotFireAt = timing.OneShotFireAt,
            AcceptedBinding = new StudioMemberWorkflowAcceptedBindingContext(
                intent.TeamId,
                intent.PublishedServiceId,
                intent.WorkflowId,
                intent.RevisionId),
        };

        StudioMemberWorkflowAuthorizationResult preflight;
        try
        {
            preflight = await _schedulePort.PreflightForWriteAsync(request, ct);
        }
        catch (StudioMemberAutomationProjectionPendingException ex)
        {
            return StudioWorkflowScheduleProvisioningExecutionResult.Retry(
                "authorization_projection_pending",
                $"required_state_version={ex.RequiredStateVersion}");
        }
        catch (StudioMemberAutomationNotFoundException)
        {
            return StudioWorkflowScheduleProvisioningExecutionResult.Retry(
                "member_projection_pending",
                "The bound workflow member is not visible in the schedule read model yet.");
        }
        catch (InvalidOperationException ex) when (IsBindingProjectionPending(ex.Message))
        {
            return StudioWorkflowScheduleProvisioningExecutionResult.Retry(
                "binding_projection_pending",
                ex.Message);
        }
        catch (StudioMemberAutomationPlanConflictException ex)
            when (IsServingRevisionProjectionPending(ex))
        {
            return StudioWorkflowScheduleProvisioningExecutionResult.Retry(
                ex.Code,
                ex.Message);
        }

        if (!preflight.Success)
        {
            return IsWorkflowEvidenceProjectionPending(preflight)
                ? StudioWorkflowScheduleProvisioningExecutionResult.Retry(
                    "workflow_authorization_evidence_not_found",
                    preflight.Detail)
                : StudioWorkflowScheduleProvisioningExecutionResult.Failed(
                    preflight.FailureCode.ToString(),
                    preflight.Detail);
        }

        var permissionDigest = NormalizeRequired(preflight.Plan?.PermissionDigest, "permissionDigest");
        var policyVersion = NormalizeRequired(preflight.Plan?.CredentialPolicy?.PolicyVersion, "policyVersion");
        const int maxGenerations = 50;
        for (var generation = 1; generation <= maxGenerations; generation++)
        {
            var scheduleId = BuildProvisioningScheduleId(intent.PublishedServiceId, generation);
            var createOperationIdentity = BuildOperationIdentity(
                intent,
                scheduleId,
                permissionDigest,
                timing,
                TeamAutomationOperationKind.Create);
            var existing = await _schedulePort.GetAsync(
                intent.ScopeId,
                intent.TeamId,
                intent.MemberId,
                scheduleId,
                ct);
            if (existing != null)
            {
                if (!string.Equals(
                        existing.PublishedServiceId,
                        intent.PublishedServiceId,
                        StringComparison.Ordinal))
                {
                    throw new StudioMemberAutomationPlanConflictException(
                        "schedule_target_changed",
                        "The stored schedule target no longer matches the provisioned workflow member service identity.");
                }

                if (string.Equals(existing.AuthorizationStatus, "active", StringComparison.Ordinal) &&
                    string.Equals(existing.OperationId, createOperationIdentity.OperationId, StringComparison.Ordinal) &&
                    string.Equals(existing.TargetRevisionId, intent.RevisionId, StringComparison.Ordinal))
                {
                    return StudioWorkflowScheduleProvisioningExecutionResult.Succeeded(
                        existing.ScheduleId,
                        existing.OperationId);
                }

                var replaceOperationIdentity = BuildOperationIdentity(
                    intent,
                    scheduleId,
                    permissionDigest,
                    timing,
                    TeamAutomationOperationKind.Reauthorize);
                try
                {
                    var replacement = await _schedulePort.ReplaceAsync(
                        request with
                        {
                            ScheduleId = scheduleId,
                            OperationId = replaceOperationIdentity.OperationId,
                            IdempotencyKey = replaceOperationIdentity.IdempotencyKey,
                            ConfirmedPolicyVersion = policyVersion,
                        },
                        permissionDigest,
                        ct);
                    return StudioWorkflowScheduleProvisioningExecutionResult.Succeeded(
                        NormalizeRequired(replacement.ScheduleId, nameof(replacement.ScheduleId)),
                        replaceOperationIdentity.OperationId);
                }
                catch (StudioMemberAutomationPlanConflictException ex)
                    when (IsRetryableAuthorizationPlanChange(ex))
                {
                    return StudioWorkflowScheduleProvisioningExecutionResult.Retry(
                        ex.Code,
                        ex.Message);
                }
            }

            try
            {
                var schedule = await _schedulePort.CreateAsync(
                    request with
                    {
                        ScheduleId = scheduleId,
                        OperationId = createOperationIdentity.OperationId,
                        IdempotencyKey = createOperationIdentity.IdempotencyKey,
                        ConfirmedPolicyVersion = policyVersion,
                    },
                    permissionDigest,
                    ct);
                return StudioWorkflowScheduleProvisioningExecutionResult.Succeeded(
                    NormalizeRequired(schedule.ScheduleId, nameof(schedule.ScheduleId)),
                    createOperationIdentity.OperationId);
            }
            catch (ScheduledDispatchNotFoundException)
            {
                // Explicitly deleted schedules are permanent tombstones.
            }
            catch (StudioMemberAutomationPlanConflictException ex)
                when (IsRetryableAuthorizationPlanChange(ex))
            {
                return StudioWorkflowScheduleProvisioningExecutionResult.Retry(
                    ex.Code,
                    ex.Message);
            }
        }

        return StudioWorkflowScheduleProvisioningExecutionResult.Failed(
            "schedule_generations_exhausted",
            $"Provisioning for service '{intent.PublishedServiceId}' exhausted {maxGenerations} deleted schedule generations.");
    }

    private static bool IsWorkflowEvidenceProjectionPending(
        StudioMemberWorkflowAuthorizationResult result) =>
        result.Detail.Contains("workflow_authorization_evidence_not_found", StringComparison.Ordinal);

    private static bool IsBindingProjectionPending(string message) =>
        message.Contains("has no bound workflow", StringComparison.Ordinal) ||
        message.Contains("not assigned to team", StringComparison.Ordinal);

    private static bool IsServingRevisionProjectionPending(
        StudioMemberAutomationPlanConflictException exception) =>
        string.Equals(exception.Code, "serving_revision_not_ready", StringComparison.Ordinal);

    private static bool IsRetryableAuthorizationPlanChange(
        StudioMemberAutomationPlanConflictException exception) =>
        string.Equals(exception.Code, "authorization_plan_changed", StringComparison.Ordinal) &&
        exception.AuthorizationPlanMismatchReason ==
        ScheduledAuthorizationPlanMismatchReason.Unspecified;

    private static ProvisionScheduleTiming ResolveScheduleTiming(
        StudioWorkflowScheduleProvisioningExecution execution)
    {
        var intent = execution.Intent;
        return intent.ScheduleMode switch
        {
            ScheduledDispatchScheduleMode.RecurringCron => new ProvisionScheduleTiming(
                NormalizeRequired(intent.CronExpression, nameof(intent.CronExpression)),
                ScheduledDispatchCalculator.NormalizeTimezone(intent.Timezone),
                ScheduledDispatchScheduleMode.RecurringCron,
                null),
            ScheduledDispatchScheduleMode.OneShotAtUtc => new ProvisionScheduleTiming(
                string.Empty,
                ScheduledDispatchCalculator.DefaultTimezone,
                ScheduledDispatchScheduleMode.OneShotAtUtc,
                execution.OneShotFireAt?.ToUniversalTime()
                    ?? throw new InvalidOperationException("one_shot_fire_at_required")),
            _ => throw new InvalidOperationException("schedule_mode_invalid"),
        };
    }

    private static string BuildProvisioningScheduleId(string publishedServiceId, int generation)
    {
        var normalizedServiceId = NormalizeRequired(publishedServiceId, nameof(publishedServiceId));
        var suffix = normalizedServiceId.All(static ch => ScheduleIdAllowedCharacters.IndexOf(ch) >= 0)
            ? normalizedServiceId
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedServiceId)).AsSpan(0, 16));
        return generation == 1
            ? $"provision-{suffix}"
            : $"provision-{suffix}.{generation}";
    }

    private static ProvisionScheduleOperationIdentity BuildOperationIdentity(
        StudioWorkflowScheduleProvisioningIntent intent,
        string scheduleId,
        string permissionDigest,
        ProvisionScheduleTiming timing,
        TeamAutomationOperationKind operationKind)
    {
        var explicitOperationId = NormalizeOptional(intent.ScheduleOperationId);
        var explicitIdempotencyKey = NormalizeOptional(intent.ScheduleIdempotencyKey);
        if (explicitOperationId != null && explicitIdempotencyKey != null)
            return new ProvisionScheduleOperationIdentity(explicitOperationId, explicitIdempotencyKey);

        var isReplacement = operationKind == TeamAutomationOperationKind.Reauthorize;
        var identity = Encoding.UTF8.GetBytes(string.Join('\n',
            isReplacement
                ? "studio-workflow-provision-schedule-replacement/v1"
                : "studio-workflow-provision-schedule/v1",
            scheduleId,
            permissionDigest,
            NormalizeOptional(intent.Prompt) ?? string.Empty,
            timing.CronExpression,
            timing.Timezone,
            ((int)timing.ScheduleMode).ToString(),
            timing.OneShotFireAt?.ToUniversalTime().UtcTicks.ToString() ?? string.Empty,
            isReplacement ? intent.RevisionId : string.Empty));
        var hash = Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 16));
        return new ProvisionScheduleOperationIdentity(
            isReplacement
                ? $"studio-workflow-provision-replace:{hash}"
                : $"studio-workflow-provision-create:{hash}",
            isReplacement
                ? $"studio-workflow-provision-replacement:{hash}"
                : $"studio-workflow-provision-schedule:{hash}");
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

    private readonly record struct ProvisionScheduleTiming(
        string CronExpression,
        string Timezone,
        ScheduledDispatchScheduleMode ScheduleMode,
        DateTimeOffset? OneShotFireAt);

    private readonly record struct ProvisionScheduleOperationIdentity(
        string OperationId,
        string IdempotencyKey);
}
