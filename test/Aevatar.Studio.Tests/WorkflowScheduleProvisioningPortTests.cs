using Aevatar.GAgentService.Abstractions;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowScheduleProvisioningPortTests
{
    [Fact]
    public async Task ProvisionAsync_ShouldForwardTransientCapabilityAdmissionContext()
    {
        var service = new RecordingProvisioningService();
        var port = new WorkflowScheduleProvisioningPort(service);
        var context = new WorkflowCapabilityAdmissionContext(
            "caller-alpha",
            "runtime-caller-credential",
            "runtime-organization-credential",
            ExternalCapabilityExecutionMode.Durable);

        await port.ProvisionAsync(new WorkflowScheduleProvisioningRequest(
            "scope-alpha",
            "team-alpha",
            "Monitor",
            "name: monitor\nsteps: []\n")
        {
            CapabilityAdmission = context,
        });

        service.Request.Should().NotBeNull();
        service.Request!.CapabilityAdmission.Should().BeSameAs(context);
    }

    [Fact]
    public async Task ProvisionAsync_ShouldMapStagefulReceipt()
    {
        var service = new RecordingProvisioningService
        {
            Response = new ProvisionWorkflowResponse(
                "m-alpha",
                "wf-alpha",
                "scope-alpha",
                "team-alpha",
                ProvisionWorkflowBindingStatusNames.Accepted,
                "/workflow/observatory")
            {
                BindingRunId = "bind-alpha",
                ProvisioningStage = WorkflowScheduleProvisioningStageNames.ScheduleBlocked,
                ScheduleStatus = WorkflowScheduleProvisioningScheduleStatusNames.Blocked,
                StageFailure = new WorkflowScheduleProvisioningStageFailure(
                    WorkflowScheduleProvisioningStageNames.ScheduleBlocked,
                    "owner_llm_authorization_evidence_not_found",
                    "owner_llm_authorization_evidence_not_found"),
            },
        };
        var port = new WorkflowScheduleProvisioningPort(service);

        var result = await port.ProvisionAsync(new WorkflowScheduleProvisioningRequest(
            "scope-alpha",
            "team-alpha",
            "Monitor",
            "name: monitor\nsteps: []\n"));

        result.MemberId.Should().Be("m-alpha");
        result.WorkflowId.Should().Be("wf-alpha");
        result.ScheduleId.Should().BeNull();
        result.BindingRunId.Should().Be("bind-alpha");
        result.ProvisioningStage.Should().Be(WorkflowScheduleProvisioningStageNames.ScheduleBlocked);
        result.ScheduleStatus.Should().Be(WorkflowScheduleProvisioningScheduleStatusNames.Blocked);
        result.StageFailure.Should().BeSameAs(service.Response.StageFailure);
    }

    private sealed class RecordingProvisioningService : IStudioWorkflowProvisioningService
    {
        public ProvisionWorkflowRequest? Request { get; private set; }
        public ProvisionWorkflowResponse Response { get; set; } = new(
            "member-alpha",
            "workflow-alpha",
            "scope-alpha",
            "team-alpha",
            ProvisionWorkflowBindingStatusNames.Accepted,
            "/workflow/observatory");

        public Task<ProvisionWorkflowResponse> ProvisionAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(Response with
            {
                ScopeId = scopeId,
                TeamId = request.TeamId ?? string.Empty,
            });
        }
    }
}
