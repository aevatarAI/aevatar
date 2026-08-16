using System.Globalization;
using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Application.Delivery;

public enum WorkflowDeliveryProvisioningExecutionStatus
{
    Skipped = 0,
    ProvisioningAccepted = 1,
    Failed = 2,
}

public sealed record WorkflowDeliveryProvisioningExecutionResult(
    WorkflowDeliveryProvisioningExecutionStatus Status,
    string InstallationId,
    int Attempt,
    string OperationId,
    string? ErrorCode = null);

public interface IWorkflowDeliveryProvisioningExecutor
{
    Task<WorkflowDeliveryProvisioningExecutionResult> ExecuteAsync(
        WorkflowDeliverySnapshot delivery,
        string continuationClaimantId,
        CancellationToken ct = default);
}

/// <summary>
/// Resumes one actor-owned accepted installation. The caller authority and
/// capability plan come only from the durable delivery replica; bearer tokens
/// are minted for this invocation and never enter actor state or read models.
/// </summary>
public sealed class WorkflowDeliveryProvisioningExecutor : IWorkflowDeliveryProvisioningExecutor
{
    private const string NyxIdProxyScope = "proxy";

    private readonly IStudioWorkflowProvisioningService _provisioning;
    private readonly IWorkflowDeliveryCommandPort _commands;
    private readonly IWorkflowCallerAccessTokenProvider _accessTokenProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowDeliveryProvisioningExecutor> _logger;

    public WorkflowDeliveryProvisioningExecutor(
        IStudioWorkflowProvisioningService provisioning,
        IWorkflowDeliveryCommandPort commands,
        IWorkflowCallerAccessTokenProvider accessTokenProvider,
        TimeProvider timeProvider,
        ILogger<WorkflowDeliveryProvisioningExecutor> logger)
    {
        _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _accessTokenProvider = accessTokenProvider ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkflowDeliveryProvisioningExecutionResult> ExecuteAsync(
        WorkflowDeliverySnapshot delivery,
        string continuationClaimantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var claimantId = WorkflowDeliveryContinuationClaimPolicy.NormalizeClaimantId(
            continuationClaimantId);
        var installation = delivery.Installation;
        if (installation == null || installation.Status != WorkflowInstallationStatus.Accepted)
        {
            return new WorkflowDeliveryProvisioningExecutionResult(
                WorkflowDeliveryProvisioningExecutionStatus.Skipped,
                installation?.InstallationId ?? string.Empty,
                installation?.Attempt ?? 0,
                installation?.OperationId ?? string.Empty);
        }
        var claim = installation.ContinuationClaim;
        var now = _timeProvider.GetUtcNow();
        if (!WorkflowDeliveryContinuationClaimPolicy.IsActiveFor(
                installation,
                claim,
                WorkflowInstallationStatus.Accepted,
                claimantId,
                now))
        {
            return new WorkflowDeliveryProvisioningExecutionResult(
                WorkflowDeliveryProvisioningExecutionStatus.Skipped,
                installation.InstallationId,
                installation.Attempt,
                installation.OperationId);
        }

        ProvisionWorkflowResponse response;
        try
        {
            var owner = RequireOwner(installation.AuthenticatedOwner);
            WorkflowDeliveryAcceptancePolicies.EnsureTriggerSupported(
                delivery.Package,
                installation.TriggerIntent.Kind);
            var acceptancePrompt = BuildAcceptancePrompt(
                delivery.Package.WorkflowName,
                installation.CreatedAtUtc,
                owner.SubjectExternalUserId);
            var bearerToken = await _accessTokenProvider.IssueAsync(
                new WorkflowCallerNyxIdAuthority
                {
                    Platform = owner.SubjectPlatform,
                    Tenant = owner.SubjectTenant,
                    ExternalUserId = owner.SubjectExternalUserId,
                    Scope = NyxIdProxyScope,
                    BindingId = owner.VerifiedBindingId,
                },
                ct);
            var callerCredential = new ProvisionWorkflowCallerCredential(
                owner.SubjectPlatform,
                owner.SubjectExternalUserId,
                NyxIdProxyScope,
                owner.SubjectTenant);
            var executionMode = installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.None
                ? ExternalCapabilityExecutionMode.Interactive
                : ExternalCapabilityExecutionMode.Durable;
            var capabilityAdmission = new WorkflowCapabilityAdmissionContext(
                owner.Owner.OwnerSubject,
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(bearerToken),
                executionMode: executionMode,
                existingPlan: installation.CapabilityAdmissionPlan);
            var shouldSchedule = executionMode == ExternalCapabilityExecutionMode.Durable;
            var request = new ProvisionWorkflowRequest(
                $"{delivery.Package.DisplayName} [{delivery.DeliveryId}]",
                installation.ResolvedYaml,
                Prompt: acceptancePrompt,
                RunImmediately: installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.OneShot,
                Cron: installation.TriggerIntent.Kind == WorkflowDeliveryTriggerKind.Cron
                    ? installation.TriggerIntent.Cron
                    : null,
                Timezone: installation.TriggerIntent.TimeZone,
                Caller: callerCredential)
            {
                TeamId = installation.TeamId,
                CapabilityAdmission = capabilityAdmission,
                AuthenticatedOwner = shouldSchedule ? owner : null,
                ProvisioningBearerToken = shouldSchedule ? bearerToken : null,
                ScheduleOperationId = installation.OperationId,
                ScheduleIdempotencyKey = installation.IdempotencyKey,
            };

            response = await _provisioning.ProvisionAsync(
                delivery.TargetScopeId,
                callerCredential,
                request,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var (code, safeMessage) = MapFailure(exception);
            _logger.LogWarning(
                "Workflow delivery provisioning failed for installation {InstallationId}, attempt {Attempt}, operation {OperationId}; failureType={FailureType} failureCode={FailureCode}.",
                installation.InstallationId,
                installation.Attempt,
                installation.OperationId,
                exception.GetType().Name,
                code);
            await _commands.RecordInstallationFailedAsync(
                new RecordWorkflowInstallationFailedMutation(
                    delivery.DeliveryId,
                    installation.InstallationId,
                    code,
                    safeMessage,
                    WorkflowInstallationStatus.Accepted,
                    installation.Attempt,
                    installation.OperationId,
                    _timeProvider.GetUtcNow(),
                    claim!.ClaimId,
                    claim.ClaimantId),
                ct);
            return new WorkflowDeliveryProvisioningExecutionResult(
                WorkflowDeliveryProvisioningExecutionStatus.Failed,
                installation.InstallationId,
                installation.Attempt,
                installation.OperationId,
                code);
        }

        try
        {
            await _commands.RecordProvisioningAcceptedAsync(
                new RecordWorkflowProvisioningAcceptedMutation(
                    delivery.DeliveryId,
                    installation.InstallationId,
                    response.MemberId,
                    response.WorkflowId,
                    response.PublishedServiceId,
                    response.RevisionId,
                    response.BindingRunId,
                    response.ScheduleId,
                    response.ScheduleProvisioningId,
                    response.ScheduleProvisioningStatus,
                    installation.Attempt,
                    installation.OperationId,
                    _timeProvider.GetUtcNow(),
                    claim!.ClaimId,
                    claim.ClaimantId),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Workflow delivery provisioning succeeded but recording the accepted outcome is uncertain for installation {InstallationId}, attempt {Attempt}, operation {OperationId}; failureType={FailureType}.",
                installation.InstallationId,
                installation.Attempt,
                installation.OperationId,
                exception.GetType().Name);
            throw;
        }

        return new WorkflowDeliveryProvisioningExecutionResult(
            WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted,
            installation.InstallationId,
            installation.Attempt,
            installation.OperationId);
    }

    internal static string BuildAcceptancePrompt(
        string workflowName,
        DateTimeOffset installationCreatedAtUtc,
        string operatorId)
    {
        var runDateValue = installationCreatedAtUtc.UtcDateTime.Date;
        var runDate = runDateValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var periodLabel = runDateValue.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        return workflowName switch
        {
            WorkflowDeliveryAcceptancePolicies.BudgetVarianceMonitor => JsonSerializer.Serialize(new
            {
                period_label = ISOWeek.GetYear(runDateValue).ToString("D4", CultureInfo.InvariantCulture) +
                    "-W" + ISOWeek.GetWeekOfYear(runDateValue).ToString("D2", CultureInfo.InvariantCulture),
                run_date = runDate,
                submit = false,
            }),
            WorkflowDeliveryAcceptancePolicies.AttendanceFillReminder => JsonSerializer.Serialize(new
            {
                period_label = periodLabel,
                days_left = "0",
                submit = false,
            }),
            WorkflowDeliveryAcceptancePolicies.MonthlyAttendanceApproval => JsonSerializer.Serialize(new
            {
                period_label = periodLabel,
                run_date = runDate,
                month_end_only = false,
                submit = false,
            }),
            WorkflowDeliveryAcceptancePolicies.OnboardingEmailApproval => JsonSerializer.Serialize(new
            {
                lark_name = "Aevatar Delivery Preview",
                department = "Workflow Delivery",
                onboarding_date = runDateValue.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                email_username = "aevatar.delivery.preview." +
                    runDateValue.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                operator_id = operatorId,
                run_date = runDate,
                submit = false,
            }),
            WorkflowDeliveryAcceptancePolicies.InvoicePrecheckApproval => JsonSerializer.Serialize(new
            {
                run_date = runDate,
                submit = false,
            }),
            _ => throw new WorkflowDeliveryException(
                "UNSUPPORTED_DELIVERY_PACKAGE",
                "The workflow package has no registered acceptance input contract."),
        };
    }

    private static AuthenticatedAuthorizationOwnerContext RequireOwner(
        AuthenticatedAuthorizationOwnerContext? owner)
    {
        if (owner?.Owner == null ||
            !string.Equals(owner.Owner.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) ||
            owner.Owner.OwnerKind == AuthorizationOwnerKind.Unspecified ||
            string.IsNullOrWhiteSpace(owner.Owner.OwnerSubject) ||
            string.IsNullOrWhiteSpace(owner.SubjectPlatform) ||
            string.IsNullOrWhiteSpace(owner.SubjectExternalUserId) ||
            string.IsNullOrWhiteSpace(owner.VerifiedBindingId))
        {
            throw new UnauthorizedAccessException("workflow_delivery_authorization_owner_invalid");
        }

        return owner;
    }

    private static (string Code, string SafeMessage) MapFailure(Exception exception) => exception switch
    {
        WorkflowDeliveryException delivery => (delivery.Code, delivery.Message),
        WorkflowExternalCapabilityAdmissionException =>
            ("CAPABILITY_ADMISSION_FAILED", "The persisted workflow capability admission could not be revalidated."),
        UnauthorizedAccessException =>
            ("PROVISIONING_UNAUTHORIZED", "The persisted NyxID provisioning authority could not be revalidated."),
        _ => ("PROVISIONING_FAILED", "Workflow provisioning failed."),
    };
}
