using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Thin adapter exposing the narrow, tool-facing
/// <see cref="IWorkflowScheduleProvisioningPort"/> over the existing
/// <see cref="IStudioWorkflowProvisioningService"/>. It introduces no new
/// behavior — it maps the tool-shaped request onto the service's
/// <see cref="ProvisionWorkflowRequest"/> (member create → bind YAML →
/// <c>ScheduleKind=Workflow</c> scheduled-dispatch, Observatory-delivered) and
/// maps the result back, dropping every channel/Lark concern.
///
/// Lives in the impl assembly (which already references everything the service
/// needs) so the agent tool provider can depend only on the Abstractions project
/// and never on this application impl — keeping the thin-abstraction layering the
/// other tool providers follow.
///
/// On the channel-free studio path the scope IS the caller's NyxID subject, so the
/// subject external user id defaults to the scope when the caller does not supply
/// one; the schedule re-mints a short-lived token from that subject ref on every
/// fire (no caller bearer is persisted or replayed).
/// </summary>
public sealed class WorkflowScheduleProvisioningPort : IWorkflowScheduleProvisioningPort
{
    private readonly IStudioWorkflowProvisioningService _provisioningService;

    public WorkflowScheduleProvisioningPort(IStudioWorkflowProvisioningService provisioningService)
    {
        _provisioningService = provisioningService
            ?? throw new ArgumentNullException(nameof(provisioningService));
    }

    public async Task<WorkflowScheduleProvisioningResult> ProvisionAsync(
        WorkflowScheduleProvisioningRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var externalUserId = string.IsNullOrWhiteSpace(request.CallerSubjectExternalUserId)
            ? request.ScopeId
            : request.CallerSubjectExternalUserId;

        var callerCredential = new ProvisionWorkflowCallerCredential(
            Platform: request.CallerSubjectPlatform,
            ExternalUserId: externalUserId,
            Scope: ProvisionWorkflowCallerCredential.DefaultScope,
            Tenant: request.CallerSubjectTenant);

        var serviceRequest = new ProvisionWorkflowRequest(
            DisplayName: request.DisplayName,
            WorkflowYaml: request.WorkflowYaml,
            Prompt: request.Prompt,
            RunImmediately: request.RunImmediately,
            Cron: request.ScheduleCron,
            Timezone: request.ScheduleTimezone,
            Caller: callerCredential);

        var response = await _provisioningService.ProvisionAsync(
            request.ScopeId,
            callerCredential,
            serviceRequest,
            ct);

        return new WorkflowScheduleProvisioningResult(
            MemberId: response.MemberId,
            ScopeId: response.ScopeId,
            BindingStatus: response.BindingStatus,
            ObservatoryUrl: response.ObservatoryUrl)
        {
            ScheduleId = response.ScheduleId,
            BindingRunId = response.BindingRunId,
            BindingRunStatus = response.BindingRunStatus,
            BindingFailure = response.BindingFailure == null
                ? null
                : new WorkflowScheduleProvisioningFailure(
                    response.BindingFailure.Code,
                    response.BindingFailure.Message,
                    response.BindingFailure.FailedAt),
        };
    }
}
