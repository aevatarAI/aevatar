using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowPrimitiveCatalogTests
{
    [Fact]
    public void BuiltInCanonicalTypes_ShouldIncludeLeaseAndCanonicalizeMutex()
    {
        WorkflowPrimitiveCatalog.ToCanonicalType("mutex").Should().Be("lease");
        WorkflowPrimitiveCatalog.BuiltInCanonicalTypes.Should().Contain("lease");
    }

    [Fact]
    public void ToCanonicalType_ShouldResolveScheduleWorkflowAlias()
    {
        WorkflowPrimitiveCatalog.ToCanonicalType("schedule_workflow").Should().Be("self_reschedule");
    }
}
