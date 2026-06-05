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
}
