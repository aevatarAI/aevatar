using Aevatar.GAgents.NyxidChat;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class WorkflowInteractiveActionSurfaceCapabilitiesTests
{
    [Fact]
    public void Registrations_ShouldExposeOnlyServiceConnectAndKeyCreate()
    {
        var registrations = WorkflowInteractiveActionSurfaceCapabilities.Registrations;

        registrations.Keys.Should().BeEquivalentTo(
        [
            WorkflowInteractiveActionParams.ActionParamsOneofCase.CatalogService,
            WorkflowInteractiveActionParams.ActionParamsOneofCase.KeyCreate,
        ]);
        registrations[WorkflowInteractiveActionParams.ActionParamsOneofCase.CatalogService]
            .WireAction.Should().Be("service.connect");
        registrations[WorkflowInteractiveActionParams.ActionParamsOneofCase.KeyCreate]
            .WireAction.Should().Be("key.create");
    }
}
