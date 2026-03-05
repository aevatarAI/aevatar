using System.Diagnostics;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class CapabilityTraceContextTests
{
    [Fact]
    public void CurrentTraceId_ShouldReflectActivityCurrent()
    {
        using var activity = new Activity("capability-trace-test").Start();
        CapabilityTraceContext.CurrentTraceId().Should().Be(activity.TraceId.ToString());
    }
}
