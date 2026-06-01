using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowStartupArtifactExemptionTests
{
    [Fact]
    public void WorkflowCapabilitiesStartupArtifact_ShouldNotRemainInProjectionAssembly()
    {
        // Refactor (iter161-cluster-001 #1257-first):
        //   Old pattern: WorkflowCapabilitiesStartupArtifact survived as a ProjectionExempt partial shell after readmodel framing was removed.
        //   New principle: capabilities stay on IWorkflowCapabilitiesPort; projection assembly exposes no startup artifact symbol.
        var assembly = typeof(WorkflowCatalogCurrentStateDocument).Assembly;

        assembly.GetType("Aevatar.Workflow.Projection.ReadModels.WorkflowCapabilitiesStartupArtifact")
            .Should().BeNull();
        assembly.GetType("Aevatar.Workflow.Projection.ReadModels.WorkflowPrimitiveCapabilityReadModel")
            .Should().BeNull();
        assembly.GetType("Aevatar.Workflow.Projection.ReadModels.WorkflowConnectorCapabilityReadModel")
            .Should().BeNull();
    }
}
