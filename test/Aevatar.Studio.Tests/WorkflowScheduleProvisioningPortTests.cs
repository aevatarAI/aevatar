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
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("runtime-caller-credential"),
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

    private sealed class RecordingProvisioningService : IStudioWorkflowProvisioningService
    {
        public ProvisionWorkflowRequest? Request { get; private set; }

        public Task<ProvisionWorkflowPreparation> PrepareAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProvisionWorkflowResponse> ProvisionAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new ProvisionWorkflowResponse(
                "member-alpha",
                scopeId,
                request.TeamId ?? string.Empty,
                ProvisionWorkflowBindingStatusNames.Accepted,
                "/admin#/observatory"));
        }
    }
}
