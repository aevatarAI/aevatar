using System.Reflection;
using Aevatar.Bootstrap.Hosting;
using FluentAssertions;

namespace Aevatar.Bootstrap.Tests;

public sealed class HostObservabilityRegistrationTests
{
    [Fact]
    public void WorkflowTelemetry_ShouldRegisterMeterAndCoverMaximumToolTimeout()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var meterNames = typeof(AevatarHostObservabilityExtensions)
            .GetField("CoreMeterNames", flags)!
            .GetValue(null)
            .Should().BeOfType<string[]>().Subject;
        var buckets = typeof(AevatarHostObservabilityExtensions)
            .GetField("DefaultWorkflowToolCallLatencyBucketsMs", flags)!
            .GetValue(null)
            .Should().BeOfType<double[]>().Subject;

        meterNames.Should().Contain("Aevatar.Workflow");
        buckets.Should().BeInAscendingOrder();
        buckets.Should().Contain(1_800_000d);
    }
}
