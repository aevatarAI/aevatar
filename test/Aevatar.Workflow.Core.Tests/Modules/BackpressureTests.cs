using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public class BackpressureHelperTests
{
    [Fact]
    public void ResolveMaxConcurrent_WithValidParameter_ReturnsValue()
    {
        var parameters = new Dictionary<string, string> { ["max_concurrent_workers"] = "5" };
        BackpressureHelper.ResolveMaxConcurrent(parameters).Should().Be(5);
    }

    [Fact]
    public void ResolveMaxConcurrent_WithNoParameter_ReturnsFallback()
    {
        var parameters = new Dictionary<string, string>();
        BackpressureHelper.ResolveMaxConcurrent(parameters, 10).Should().Be(10);
    }

    [Fact]
    public void ResolveMaxConcurrent_WithNullParameters_ReturnsFallback()
    {
        BackpressureHelper.ResolveMaxConcurrent(null, 15).Should().Be(15);
    }

    [Fact]
    public void ResolveMaxConcurrent_WithZero_ReturnsFallback()
    {
        var parameters = new Dictionary<string, string> { ["max_concurrent_workers"] = "0" };
        BackpressureHelper.ResolveMaxConcurrent(parameters, 10).Should().Be(10);
    }

    [Fact]
    public void ResolveMaxConcurrent_ClampedToFallback()
    {
        var parameters = new Dictionary<string, string> { ["max_concurrent_workers"] = "100" };
        BackpressureHelper.ResolveMaxConcurrent(parameters, 20).Should().Be(20);
    }

    [Fact]
    public void TryAdmit_UnderLimit_ShouldAdmit()
    {
        var bp = BackpressureHelper.Initialize(3);
        var entry = MakeEntry("s1");

        BackpressureHelper.TryAdmit(bp, entry).Should().BeTrue();
        bp.ActiveWorkers.Should().Be(1);
        bp.Queue.Should().BeEmpty();
    }

    [Fact]
    public void TryAdmit_AtLimit_ShouldQueue()
    {
        var bp = BackpressureHelper.Initialize(2);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1")).Should().BeTrue();
        BackpressureHelper.TryAdmit(bp, MakeEntry("s2")).Should().BeTrue();

        BackpressureHelper.TryAdmit(bp, MakeEntry("s3")).Should().BeFalse();
        bp.ActiveWorkers.Should().Be(2);
        bp.Queue.Should().HaveCount(1);
        bp.Queue[0].StepId.Should().Be("s3");
    }

    [Fact]
    public void TryDrainOne_WithQueuedWork_ShouldDequeue()
    {
        var bp = BackpressureHelper.Initialize(1);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s2"));
        bp.Queue.Should().HaveCount(1);

        var next = BackpressureHelper.TryDrainOne(bp);

        next.Should().NotBeNull();
        next!.StepId.Should().Be("s2");
        bp.ActiveWorkers.Should().Be(1);
        bp.Queue.Should().BeEmpty();
    }

    [Fact]
    public void TryDrainOne_EmptyQueue_ShouldDecrementAndReturnNull()
    {
        var bp = BackpressureHelper.Initialize(3);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1"));
        bp.ActiveWorkers.Should().Be(1);

        var next = BackpressureHelper.TryDrainOne(bp);

        next.Should().BeNull();
        bp.ActiveWorkers.Should().Be(0);
    }

    [Fact]
    public void TryDrainOne_FIFOOrder()
    {
        var bp = BackpressureHelper.Initialize(1);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s2"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s3"));
        bp.Queue.Should().HaveCount(2);

        var first = BackpressureHelper.TryDrainOne(bp);
        first!.StepId.Should().Be("s2");

        var second = BackpressureHelper.TryDrainOne(bp);
        second!.StepId.Should().Be("s3");

        var third = BackpressureHelper.TryDrainOne(bp);
        third.Should().BeNull();
    }

    [Fact]
    public void ToStepRequest_ShouldConvertCorrectly()
    {
        var entry = new BackpressureQueueEntry
        {
            StepId = "s1",
            StepType = "llm_call",
            RunId = "r1",
            Input = "hello",
            TargetRole = "worker",
            Parameters = { ["key"] = "val" },
        };

        var request = BackpressureHelper.ToStepRequest(entry);

        request.StepId.Should().Be("s1");
        request.StepType.Should().Be("llm_call");
        request.RunId.Should().Be("r1");
        request.Input.Should().Be("hello");
        request.TargetRole.Should().Be("worker");
        request.Parameters.Should().ContainKey("key").WhoseValue.Should().Be("val");
    }

    private static BackpressureQueueEntry MakeEntry(string stepId) =>
        new() { StepId = stepId, StepType = "llm_call", RunId = "r1", Input = "test" };
}
