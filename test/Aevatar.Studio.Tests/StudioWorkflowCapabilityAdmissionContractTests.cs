using System.Text.Json.Serialization;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkflowCapabilityAdmissionContractTests
{
    [Theory]
    [InlineData(typeof(StudioWorkflowProvisioningService))]
    [InlineData(typeof(StudioMemberWorkflowBindingPort))]
    [InlineData(typeof(StudioMemberService))]
    public void WorkflowWriteService_ShouldRequireUnifiedCapabilityAdmission(Type serviceType)
    {
        serviceType.GetConstructors()
            .Should().ContainSingle()
            .Which.GetParameters()
            .Should().Contain(parameter =>
                parameter.ParameterType == typeof(IWorkflowExternalCapabilityAdmissionService));
    }

    [Theory]
    [InlineData(typeof(ProvisionWorkflowRequest))]
    [InlineData(typeof(WorkflowScheduleProvisioningRequest))]
    [InlineData(typeof(Aevatar.Studio.Application.Provisioning.StudioMemberWorkflowBindingRequest))]
    [InlineData(typeof(UpdateStudioMemberBindingRequest))]
    public void WorkflowWriteRequest_ShouldCarryOnlyJsonIgnoredTransientAdmissionContext(Type requestType)
    {
        var property = requestType.GetProperty("CapabilityAdmission");

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(WorkflowCapabilityAdmissionContext));
        property.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .Should().ContainSingle();
    }

    [Fact]
    public void WorkflowDraftRequest_ShouldNotCarryRuntimeAdmissionContext()
    {
        typeof(SaveWorkflowDraftRequest).GetProperty("CapabilityAdmission").Should().BeNull();
    }

    [Fact]
    public void StudioMemberWorkflowBinding_ShouldCarryCredentialFreeAdmissionPlan()
    {
        var property = typeof(StudioMemberWorkflowBindingSpec)
            .GetProperty("CapabilityAdmissionPlan");

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(WorkflowCapabilityAdmissionPlan));
        property.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .Should().ContainSingle();

        typeof(Aevatar.GAgents.StudioMember.StudioMemberWorkflowBindingRequest)
            .GetProperty("CapabilityAdmissionPlan")
            .Should().NotBeNull();
    }
}
